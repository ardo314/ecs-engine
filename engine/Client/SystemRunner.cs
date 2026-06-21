using Engine.Core;
using Engine.Core.Messages;
using MessagePack;
using NATS.Client.Core;

namespace Client;

// Ensure contractless serialization is configured when using the Client SDK.
internal static class ClientModuleInit
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    #pragma warning disable CA2255 // ModuleInitializer is intentional in this SDK
    internal static void Init() => Serialization.Initialize();
    #pragma warning restore CA2255
}

/// <summary>
/// Connects to the engine coordinator via NATS and runs a system function each tick.
/// </summary>
public class SystemRunner : IAsyncDisposable
{
    private readonly string _systemName;
    private readonly string _natsUrl;
    private NatsConnection? _nats;
    private readonly string[] _reads;
    private readonly string[] _writes;
    private readonly Func<ComponentBuffer, float, Task> _tickHandler;
    private readonly string _instanceId = Guid.NewGuid().ToString("N");

    public SystemRunner(
        string systemName,
        string? natsUrl = null,
        string[]? reads = null,
        string[]? writes = null,
        Func<ComponentBuffer, float, Task>? tickHandler = null)
    {
        _systemName = systemName;
        _natsUrl = natsUrl ?? Environment.GetEnvironmentVariable("NATS_URL") ?? "nats://localhost:4222";
        _reads = reads ?? [];
        _writes = writes ?? [];
        _tickHandler = tickHandler ?? ((_, _) => Task.CompletedTask);
    }

    public string InstanceId => _instanceId;

    /// <summary>
    /// Connects to the message transport. Must be called before RunAsync or SpawnEntityAsync.
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        _nats = new NatsConnection(new NatsOpts { Url = _natsUrl });
        await _nats.ConnectAsync();
        Console.WriteLine($"[{_systemName}] Connected to transport.");
    }

    /// <summary>
    /// Requests the coordinator to spawn an entity with the given components.
    /// </summary>
    public async Task SpawnEntityAsync(params IComponent[] components)
    {
        var nats = _nats ?? throw new InvalidOperationException("Call ConnectAsync before spawning entities.");
        var types = new string[components.Length];
        var data = new byte[components.Length][];
        for (var i = 0; i < components.Length; i++)
        {
            var type = components[i].GetType();
            types[i] = type.FullName ?? type.Name;
            data[i] = MessagePackSerializer.Serialize(type, components[i]);
        }
        var req = new EntitySpawnRequest { ComponentTypes = types, ComponentData = data };
        await nats.PublishAsync("engine.entity.spawn.request", MessagePackSerializer.Serialize(req));
    }

    public async ValueTask DisposeAsync()
    {
        if (_nats is not null)
            await _nats.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Registers with the coordinator, subscribes to component data, and runs the tick loop.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var nats = _nats ?? throw new InvalidOperationException("Call ConnectAsync before RunAsync.");

        // Register with coordinator (re-publish periodically to survive startup race)
        var descriptor = new SystemDescriptor
        {
            Name = _systemName,
            InstanceId = _instanceId,
            Reads = _reads,
            Writes = _writes
        };
        var registrationBytes = MessagePackSerializer.Serialize(descriptor);

        // Publish registration on a timer until we receive our first schedule
        using var regCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = Task.Run(async () =>
        {
            while (!regCts.Token.IsCancellationRequested)
            {
                await nats.PublishAsync("engine.system.register", registrationBytes, cancellationToken: regCts.Token);
                try { await Task.Delay(1000, regCts.Token); }
                catch (OperationCanceledException) { break; }
            }
        }, regCts.Token);

        Console.WriteLine($"[{_systemName}] Registering (instance {_instanceId}). Reads: [{string.Join(", ", _reads)}], Writes: [{string.Join(", ", _writes)}]");

        // Subscribe to schedule messages
        var scheduleSub = await nats.SubscribeCoreAsync<byte[]>(
            $"engine.system.schedule.{_systemName}",
            queueGroup: _systemName,
            cancellationToken: cancellationToken);

        // Subscribe to component data
        var dataSub = await nats.SubscribeCoreAsync<byte[]>(
            $"engine.component.set.{_systemName}",
            queueGroup: _systemName,
            cancellationToken: cancellationToken);

        var buffer = new ComponentBuffer();
        var firstSchedule = true;

        try
        {
            await foreach (var schedMsg in scheduleSub.Msgs.ReadAllAsync(cancellationToken))
            {
                if (firstSchedule)
                {
                    firstSchedule = false;
                    await regCts.CancelAsync();
                    Console.WriteLine($"[{_systemName}] Registered successfully — receiving ticks.");
                }

                var schedule = MessagePackSerializer.Deserialize<SystemSchedule>(schedMsg.Data!);
                buffer.Clear();
                buffer.SetTickId(schedule.TickId);

                // Receive shards
                var shardsReceived = 0;
                using var shardCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                shardCts.CancelAfter(TimeSpan.FromSeconds(5));

                try
                {
                    await foreach (var dataMsg in dataSub.Msgs.ReadAllAsync(shardCts.Token))
                    {
                        var shard = MessagePackSerializer.Deserialize<ComponentShard>(dataMsg.Data!);
                        if (shard.TickId != schedule.TickId) continue;

                        buffer.AddShard(shard);
                        shardsReceived++;

                        if (shardsReceived >= schedule.ShardCount)
                            break;
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    Console.WriteLine($"[{_systemName}] Timeout waiting for shards (got {shardsReceived}/{schedule.ShardCount})");
                }

                // Execute tick handler
                var dt = 1.0f / 20f; // TODO: receive from TickStart or schedule
                await _tickHandler(buffer, dt);

                // Publish mutations
                var mutations = buffer.FlushMutations();
                foreach (var change in mutations)
                {
                    await nats.PublishAsync(
                        $"engine.component.changed.{_systemName}",
                        MessagePackSerializer.Serialize(change),
                        cancellationToken: cancellationToken);
                }

                // Acknowledge tick completion
                var ack = new TickAck { TickId = schedule.TickId, InstanceId = _instanceId };
                await nats.PublishAsync(
                    "engine.coord.tick.done",
                    MessagePackSerializer.Serialize(ack),
                    cancellationToken: cancellationToken);
            }
        }
        finally
        {
            // Unregister on shutdown
            var unreg = new SystemUnregister { Name = _systemName, InstanceId = _instanceId };
            try
            {
                await nats.PublishAsync(
                    "engine.system.unregister",
                    MessagePackSerializer.Serialize(unreg),
                    cancellationToken: CancellationToken.None);
            }
            catch { /* best-effort */ }

            await scheduleSub.UnsubscribeAsync();
            await dataSub.UnsubscribeAsync();
            Console.WriteLine($"[{_systemName}] Shut down.");
        }
    }
}
