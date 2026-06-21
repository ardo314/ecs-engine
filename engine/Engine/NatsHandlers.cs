using System.Collections.Concurrent;
using Engine.Core.Messages;
using MessagePack;
using NATS.Client.Core;

namespace Engine.Coordinator;

/// <summary>
/// Manages NATS subscriptions for system registration, unregistration, and entity spawn requests.
/// </summary>
public class NatsHandlers
{
    private readonly NatsConnection _nats;
    private readonly SystemRegistry _registry;
    private readonly WorldState _world;
    private readonly WatchManager _watchManager;
    private readonly ConcurrentQueue<EntitySpawnRequest> _pendingSpawns;

    public NatsHandlers(NatsConnection nats, SystemRegistry registry, WorldState world, WatchManager watchManager, ConcurrentQueue<EntitySpawnRequest> pendingSpawns)
    {
        _nats = nats;
        _registry = registry;
        _world = world;
        _watchManager = watchManager;
        _pendingSpawns = pendingSpawns;
    }

    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Completes when all subscriptions are active.
    /// </summary>
    public Task Ready => _ready.Task;

    /// <summary>
    /// Starts all background NATS subscriptions. Returns when cancelled.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var regSub = await _nats.SubscribeCoreAsync<byte[]>("engine.system.register", cancellationToken: cancellationToken);
        var unregSub = await _nats.SubscribeCoreAsync<byte[]>("engine.system.unregister", cancellationToken: cancellationToken);
        var spawnSub = await _nats.SubscribeCoreAsync<byte[]>("engine.entity.spawn.request", cancellationToken: cancellationToken);
        var querySystemsSub = await _nats.SubscribeCoreAsync<byte[]>("engine.query.systems", cancellationToken: cancellationToken);
        var queryEntitiesSub = await _nats.SubscribeCoreAsync<byte[]>("engine.query.entities", cancellationToken: cancellationToken);
        var watchSubSub = await _nats.SubscribeCoreAsync<byte[]>("engine.watch.subscribe", cancellationToken: cancellationToken);
        var watchUnsubSub = await _nats.SubscribeCoreAsync<byte[]>("engine.watch.unsubscribe", cancellationToken: cancellationToken);

        _ready.TrySetResult();
        Console.WriteLine("[Coordinator] NATS subscriptions active.");

        var regTask = ProcessRegistrations(regSub, cancellationToken);
        var unregTask = ProcessUnregistrations(unregSub, cancellationToken);
        var spawnTask = ProcessSpawnRequests(spawnSub, cancellationToken);
        var querySystemsTask = ProcessQuerySystems(querySystemsSub, cancellationToken);
        var queryEntitiesTask = ProcessQueryEntities(queryEntitiesSub, cancellationToken);
        var watchSubTask = ProcessWatchSubscribe(watchSubSub, cancellationToken);
        var watchUnsubTask = ProcessWatchUnsubscribe(watchUnsubSub, cancellationToken);

        await Task.WhenAll(regTask, unregTask, spawnTask, querySystemsTask, queryEntitiesTask, watchSubTask, watchUnsubTask);
    }

    private async Task ProcessRegistrations(INatsSub<byte[]> sub, CancellationToken cancellationToken)
    {
        await foreach (var msg in sub.Msgs.ReadAllAsync(cancellationToken))
        {
            try
            {
                var descriptor = MessagePackSerializer.Deserialize<SystemDescriptor>(msg.Data!);
                _registry.Register(descriptor);
                _watchManager.NotifySystemsChanged();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Coordinator] Failed to deserialize registration: {ex.Message}");
            }
        }
    }

    private async Task ProcessUnregistrations(INatsSub<byte[]> sub, CancellationToken cancellationToken)
    {
        await foreach (var msg in sub.Msgs.ReadAllAsync(cancellationToken))
        {
            try
            {
                var unreg = MessagePackSerializer.Deserialize<SystemUnregister>(msg.Data!);
                _registry.Unregister(unreg);
                _watchManager.NotifySystemsChanged();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Coordinator] Failed to deserialize unregister: {ex.Message}");
            }
        }
    }

    private async Task ProcessSpawnRequests(INatsSub<byte[]> sub, CancellationToken cancellationToken)
    {
        await foreach (var msg in sub.Msgs.ReadAllAsync(cancellationToken))
        {
            try
            {
                var req = MessagePackSerializer.Deserialize<EntitySpawnRequest>(msg.Data!);
                _pendingSpawns.Enqueue(req);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Coordinator] Failed to deserialize spawn request: {ex.Message}");
            }
        }
    }

    private async Task ProcessQuerySystems(INatsSub<byte[]> sub, CancellationToken cancellationToken)
    {
        await foreach (var msg in sub.Msgs.ReadAllAsync(cancellationToken))
        {
            try
            {
                var response = BuildSystemsResponse();
                var payload = MessagePackSerializer.Serialize(response);
                if (msg.ReplyTo is not null)
                {
                    await _nats.PublishAsync(msg.ReplyTo, payload, cancellationToken: cancellationToken);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Coordinator] Failed to handle query.systems: {ex.Message}");
            }
        }
    }

    private async Task ProcessQueryEntities(INatsSub<byte[]> sub, CancellationToken cancellationToken)
    {
        await foreach (var msg in sub.Msgs.ReadAllAsync(cancellationToken))
        {
            try
            {
                QueryEntitiesRequest? request = null;
                if (msg.Data is { Length: > 0 })
                {
                    request = MessagePackSerializer.Deserialize<QueryEntitiesRequest>(msg.Data);
                }

                var response = BuildEntitiesResponse(request?.ComponentFilter);
                var payload = MessagePackSerializer.Serialize(response);
                if (msg.ReplyTo is not null)
                {
                    await _nats.PublishAsync(msg.ReplyTo, payload, cancellationToken: cancellationToken);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Coordinator] Failed to handle query.entities: {ex.Message}");
            }
        }
    }

    private async Task ProcessWatchSubscribe(INatsSub<byte[]> sub, CancellationToken cancellationToken)
    {
        await foreach (var msg in sub.Msgs.ReadAllAsync(cancellationToken))
        {
            try
            {
                var request = MessagePackSerializer.Deserialize<WatchRequest>(msg.Data!);
                var response = _watchManager.Register(request);
                var payload = MessagePackSerializer.Serialize(response);
                if (msg.ReplyTo is not null)
                {
                    await _nats.PublishAsync(msg.ReplyTo, payload, cancellationToken: cancellationToken);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Coordinator] Failed to handle watch.subscribe: {ex.Message}");
            }
        }
    }

    private async Task ProcessWatchUnsubscribe(INatsSub<byte[]> sub, CancellationToken cancellationToken)
    {
        await foreach (var msg in sub.Msgs.ReadAllAsync(cancellationToken))
        {
            try
            {
                var cancel = MessagePackSerializer.Deserialize<WatchCancel>(msg.Data!);
                _watchManager.Cancel(cancel.WatchId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Coordinator] Failed to handle watch.unsubscribe: {ex.Message}");
            }
        }
    }

    internal QuerySystemsResponse BuildSystemsResponse()
    {
        var unique = _registry.GetUniqueSystems();
        var stages = _registry.ComputeStages();

        var systemInfos = unique.Select(s => new SystemInfo
        {
            Name = s.Name,
            InstanceId = s.InstanceId,
            Reads = s.Reads,
            Writes = s.Writes
        }).ToArray();

        var stageNames = stages.Select(stage =>
            stage.Select(s => s.Name).ToArray()
        ).ToArray();

        return new QuerySystemsResponse
        {
            Systems = systemInfos,
            Stages = stageNames
        };
    }

    internal QueryEntitiesResponse BuildEntitiesResponse(string[]? componentFilter)
    {
        IEnumerable<ulong> entities;
        if (componentFilter is { Length: > 0 })
        {
            entities = _world.GetEntitiesWith(componentFilter);
        }
        else
        {
            entities = _world.GetAllEntities();
        }

        var snapshots = new List<EntitySnapshot>();
        foreach (var entityId in entities)
        {
            var comps = _world.GetAllComponents(entityId);
            snapshots.Add(new EntitySnapshot
            {
                EntityId = entityId,
                Components = comps != null ? new Dictionary<string, byte[]>(comps) : new()
            });
        }

        return new QueryEntitiesResponse { Entities = snapshots.ToArray() };
    }
}
