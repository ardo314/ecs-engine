using System.Collections.Concurrent;
using Engine.Core.Messages;
using MessagePack;
using NATS.Client.Core;

namespace Engine.Coordinator;

/// <summary>
/// Runs the fixed-timestep tick loop: processes spawns, schedules systems, collects mutations.
/// </summary>
public class TickLoop
{
    private readonly NatsConnection _nats;
    private readonly WorldState _world;
    private readonly SystemRegistry _registry;
    private readonly WatchManager _watchManager;
    private readonly NatsHandlers _handlers;
    private readonly ConcurrentQueue<EntitySpawnRequest> _pendingSpawns;
    private readonly int _tickRate;

    public TickLoop(
        NatsConnection nats,
        WorldState world,
        SystemRegistry registry,
        WatchManager watchManager,
        NatsHandlers handlers,
        ConcurrentQueue<EntitySpawnRequest> pendingSpawns,
        int tickRate)
    {
        _nats = nats;
        _world = world;
        _registry = registry;
        _watchManager = watchManager;
        _handlers = handlers;
        _pendingSpawns = pendingSpawns;
        _tickRate = tickRate;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var tickInterval = TimeSpan.FromMilliseconds(1000.0 / _tickRate);
        ulong tickId = 0;

        Console.WriteLine($"[Coordinator] Starting tick loop at {_tickRate} Hz ({tickInterval.TotalMilliseconds:F1}ms interval)");

        while (!cancellationToken.IsCancellationRequested)
        {
            var tickStart = DateTime.UtcNow;
            tickId++;

            await ProcessPendingSpawns(tickId, cancellationToken);

            var stages = _registry.ComputeStages();

            for (var stageIdx = 0; stageIdx < stages.Count; stageIdx++)
            {
                await ExecuteStage(stages[stageIdx], stageIdx, tickId, cancellationToken);
            }

            if (tickId % 100 == 0)
            {
                Console.WriteLine($"[Coordinator] Tick {tickId} complete. Entities: {_world.EntityCount}, Systems: {_registry.GetSystemNames().Count}");
            }

            await PushWatchData(tickId, cancellationToken);

            var elapsed = DateTime.UtcNow - tickStart;
            var sleepTime = tickInterval - elapsed;
            if (sleepTime > TimeSpan.Zero)
            {
                try { await Task.Delay(sleepTime, cancellationToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        Console.WriteLine($"[Coordinator] Shutting down after {tickId} ticks.");
    }

    private async Task ProcessPendingSpawns(ulong tickId, CancellationToken cancellationToken)
    {
        while (_pendingSpawns.TryDequeue(out var spawnReq))
        {
            var entityId = _world.AllocateEntity();
            for (var i = 0; i < spawnReq.ComponentTypes.Length && i < spawnReq.ComponentData.Length; i++)
            {
                _world.SetComponent(entityId, spawnReq.ComponentTypes[i], spawnReq.ComponentData[i]);
            }

            var created = new EntityCreated { EntityId = entityId, ComponentTypes = spawnReq.ComponentTypes };
            await _nats.PublishAsync("engine.entity.create", MessagePackSerializer.Serialize(created), cancellationToken: cancellationToken);
        }
    }

    private async Task ExecuteStage(List<SystemDescriptor> stage, int stageIdx, ulong tickId, CancellationToken cancellationToken)
    {
        foreach (var sys in stage)
        {
            var allTypes = sys.Reads.Concat(sys.Writes).Distinct().ToList();
            var matchingEntities = _world.GetEntitiesWith(allTypes);

            if (matchingEntities.Count == 0)
                continue;

            var entityArray = matchingEntities.ToArray();

            var schedule = new SystemSchedule
            {
                TickId = tickId,
                ShardCount = allTypes.Count
            };
            await _nats.PublishAsync(
                $"engine.system.schedule.{sys.Name}",
                MessagePackSerializer.Serialize(schedule),
                cancellationToken: cancellationToken);

            foreach (var compType in allTypes)
            {
                var dataChunks = new List<byte[]>();
                foreach (var eid in entityArray)
                {
                    var data = _world.GetComponent(eid, compType);
                    dataChunks.Add(data ?? []);
                }

                var shard = new ComponentShard
                {
                    TickId = tickId,
                    Entities = entityArray,
                    ComponentType = compType,
                    Data = MessagePackSerializer.Serialize(dataChunks.ToArray())
                };
                await _nats.PublishAsync(
                    $"engine.component.set.{sys.Name}",
                    MessagePackSerializer.Serialize(shard),
                    cancellationToken: cancellationToken);
            }
        }

        // Wait for acks from all systems in this stage
        var expectedAcks = new HashSet<string>(stage.Select(s => s.Name));
        var receivedAcks = new HashSet<string>();
        var ackDeadline = DateTime.UtcNow.AddSeconds(5);

        if (expectedAcks.Count == 0)
            return;

        var ackSub = await _nats.SubscribeCoreAsync<byte[]>("engine.coord.tick.done", cancellationToken: cancellationToken);

        try
        {
            while (receivedAcks.Count < expectedAcks.Count && DateTime.UtcNow < ackDeadline)
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(ackDeadline - DateTime.UtcNow);

                try
                {
                    await foreach (var ackMsg in ackSub.Msgs.ReadAllAsync(timeoutCts.Token))
                    {
                        var ack = MessagePackSerializer.Deserialize<TickAck>(ackMsg.Data!);
                        if (ack.TickId == tickId)
                        {
                            receivedAcks.Add(ack.InstanceId);
                        }
                        if (receivedAcks.Count >= expectedAcks.Count)
                            break;
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Timeout waiting for acks
                }
                break;
            }
        }
        finally
        {
            await ackSub.UnsubscribeAsync();
        }

        if (receivedAcks.Count < expectedAcks.Count)
        {
            Console.WriteLine($"[Coordinator] Tick {tickId} stage {stageIdx}: timeout waiting for acks ({receivedAcks.Count}/{expectedAcks.Count})");
        }

        // Collect mutations from systems in this stage
        foreach (var sys in stage)
        {
            var changesSub = await _nats.SubscribeCoreAsync<byte[]>($"engine.component.changed.{sys.Name}", cancellationToken: cancellationToken);
            using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            drainCts.CancelAfter(100);

            try
            {
                await foreach (var changeMsg in changesSub.Msgs.ReadAllAsync(drainCts.Token))
                {
                    var changes = MessagePackSerializer.Deserialize<ComponentChanges>(changeMsg.Data!);
                    if (changes.TickId != tickId) continue;

                    var entityData = MessagePackSerializer.Deserialize<byte[][]>(changes.Data);
                    for (var i = 0; i < changes.Entities.Length && i < entityData.Length; i++)
                    {
                        _world.SetComponent(changes.Entities[i], changes.ComponentType, entityData[i]);
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Done draining
            }
            finally
            {
                await changesSub.UnsubscribeAsync();
            }
        }
    }

    private async Task PushWatchData(ulong tickId, CancellationToken cancellationToken)
    {
        var watches = _watchManager.GetActiveWatches();
        if (watches.Count == 0)
            return;

        foreach (var watch in watches)
        {
            var data = new WatchData
            {
                WatchId = watch.WatchId,
                TickId = tickId
            };

            if (_watchManager.ShouldIncludeSystems(watch))
            {
                var sysResponse = _handlers.BuildSystemsResponse();
                data = data with
                {
                    Systems = sysResponse.Systems,
                    Stages = sysResponse.Stages
                };
            }

            if (watch.IncludeEntities)
            {
                var entResponse = _handlers.BuildEntitiesResponse(watch.ComponentFilter);
                data = data with { Entities = entResponse.Entities };
            }

            await _nats.PublishAsync(
                watch.DataSubject,
                MessagePackSerializer.Serialize(data),
                cancellationToken: cancellationToken);
        }
    }
}
