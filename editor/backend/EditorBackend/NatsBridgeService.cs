using System.Text.Json;
using Engine.Core;
using Engine.Core.Messages;
using MessagePack;
using NATS.Client.Core;

namespace EditorBackend;

/// <summary>
/// Background service that registers a watch with the coordinator and pushes
/// deserialized state snapshots to all connected WebSocket clients.
/// </summary>
public class NatsBridgeService : BackgroundService
{
    private readonly NatsConnection _nats;
    private readonly WsBroadcaster _wsManager;
    private readonly Guid _watchId = Guid.NewGuid();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public NatsBridgeService(NatsConnection nats, WsBroadcaster wsManager)
    {
        _nats = nats;
        _wsManager = wsManager;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for NATS connection to be ready
        while (_nats.ConnectionState != NatsConnectionState.Open && !stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(500, stoppingToken);
        }

        var watchRequest = new WatchRequest
        {
            WatchId = _watchId,
            IncludeSystems = true,
            IncludeEntities = true
        };

        // Send watch subscribe request and get the data subject
        var reply = await _nats.RequestAsync<byte[], byte[]>(
            "engine.watch.subscribe",
            MessagePackSerializer.Serialize(watchRequest),
            cancellationToken: stoppingToken);

        var watchResponse = MessagePackSerializer.Deserialize<WatchResponse>(reply.Data!);
        Console.WriteLine($"[EditorBridge] Watch registered, subscribing to {watchResponse.DataSubject}");

        // Subscribe to the watch data subject
        var dataSub = await _nats.SubscribeCoreAsync<byte[]>(watchResponse.DataSubject, cancellationToken: stoppingToken);

        // Track last systems/stages so we can include them in every snapshot
        SystemInfo[]? lastSystems = null;
        string[][]? lastStages = null;

        try
        {
            await foreach (var msg in dataSub.Msgs.ReadAllAsync(stoppingToken))
            {
                try
                {
                    var watchData = MessagePackSerializer.Deserialize<WatchData>(msg.Data!);

                    // Update systems cache if provided
                    if (watchData.Systems is not null)
                        lastSystems = watchData.Systems;
                    if (watchData.Stages is not null)
                        lastStages = watchData.Stages;

                    // Build JSON-friendly snapshot
                    var snapshot = new EditorSnapshot
                    {
                        Type = "snapshot",
                        TickId = watchData.TickId,
                        Systems = lastSystems ?? [],
                        Stages = lastStages ?? [],
                        Entities = DeserializeEntities(watchData.Entities)
                    };

                    var json = JsonSerializer.Serialize(snapshot, JsonOptions);
                    _wsManager.UpdateCache(json);
                    await _wsManager.BroadcastAsync(json, stoppingToken);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[EditorBridge] Error processing watch data: {ex.Message}");
                }
            }
        }
        finally
        {
            // Cancel the watch on shutdown
            try
            {
                var cancel = new WatchCancel { WatchId = _watchId };
                await _nats.PublishAsync(
                    "engine.watch.unsubscribe",
                    MessagePackSerializer.Serialize(cancel));
                Console.WriteLine("[EditorBridge] Watch cancelled.");
            }
            catch
            {
                // Best-effort cleanup
            }
        }
    }

    private static EditorEntity[] DeserializeEntities(EntitySnapshot[]? entities)
    {
        if (entities is null)
            return [];

        var result = new EditorEntity[entities.Length];
        for (var i = 0; i < entities.Length; i++)
        {
            var components = new Dictionary<string, object?>();
            foreach (var (typeName, data) in entities[i].Components)
            {
                try
                {
                    // Deserialize the raw MessagePack blob into a dynamic object.
                    // ContractlessStandardResolver serialises structs/records as maps,
                    // so this gives us { "X": 1.0, "Y": 2.0, ... } etc.
                    var value = MessagePackSerializer.Deserialize<object>(data, Serialization.Options);
                    components[typeName] = value;
                }
                catch
                {
                    components[typeName] = null;
                }
            }

            result[i] = new EditorEntity
            {
                EntityId = entities[i].EntityId,
                Components = components
            };
        }

        return result;
    }
}

// JSON DTOs for the WebSocket protocol

internal class EditorSnapshot
{
    public string Type { get; init; } = "snapshot";
    public ulong TickId { get; init; }
    public SystemInfo[] Systems { get; init; } = [];
    public string[][] Stages { get; init; } = [];
    public EditorEntity[] Entities { get; init; } = [];
}

internal class EditorEntity
{
    public ulong EntityId { get; init; }
    public Dictionary<string, object?> Components { get; init; } = new();
}
