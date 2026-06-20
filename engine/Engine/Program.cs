using System.Collections.Concurrent;
using Engine.Coordinator;
using Engine.Core.Messages;
using MessagePack;
using NATS.Client.Core;

var natsUrl = Environment.GetEnvironmentVariable("NATS_URL") ?? "nats://localhost:4222";
var tickRate = int.TryParse(Environment.GetEnvironmentVariable("TICK_RATE"), out var tr) ? tr : 20;
var seedCount = args.Length >= 2 && args[0] == "--seed" && int.TryParse(args[1], out var sc) ? sc : 0;

Console.WriteLine("Engine coordinator starting...");

await using var nats = new NatsConnection(new NatsOpts { Url = natsUrl });
await nats.ConnectAsync();

Console.WriteLine($"Connected to NATS at {natsUrl}");

var world = new WorldState();
var registry = new SystemRegistry();
var pendingSpawns = new ConcurrentQueue<EntitySpawnRequest>();
var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

// Subscribe to system registration
var regSub = await nats.SubscribeCoreAsync<byte[]>("engine.system.register", cancellationToken: cts.Token);
_ = Task.Run(async () =>
{
    await foreach (var msg in regSub.Msgs.ReadAllAsync(cts.Token))
    {
        try
        {
            var descriptor = MessagePackSerializer.Deserialize<SystemDescriptor>(msg.Data!);
            registry.Register(descriptor);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Coordinator] Failed to deserialize registration: {ex.Message}");
        }
    }
}, cts.Token);

// Subscribe to system unregistration
var unregSub = await nats.SubscribeCoreAsync<byte[]>("engine.system.unregister", cancellationToken: cts.Token);
_ = Task.Run(async () =>
{
    await foreach (var msg in unregSub.Msgs.ReadAllAsync(cts.Token))
    {
        try
        {
            var unreg = MessagePackSerializer.Deserialize<SystemUnregister>(msg.Data!);
            registry.Unregister(unreg);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Coordinator] Failed to deserialize unregister: {ex.Message}");
        }
    }
}, cts.Token);

// Subscribe to entity spawn requests
var spawnSub = await nats.SubscribeCoreAsync<byte[]>("engine.entity.spawn.request", cancellationToken: cts.Token);
_ = Task.Run(async () =>
{
    await foreach (var msg in spawnSub.Msgs.ReadAllAsync(cts.Token))
    {
        try
        {
            var req = MessagePackSerializer.Deserialize<EntitySpawnRequest>(msg.Data!);
            pendingSpawns.Enqueue(req);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Coordinator] Failed to deserialize spawn request: {ex.Message}");
        }
    }
}, cts.Token);

// Seed entities if requested
if (seedCount > 0)
{
    Console.WriteLine($"[Coordinator] Seeding {seedCount} entities...");
    for (var i = 0; i < seedCount; i++)
    {
        var entityId = world.AllocateEntity();
        // Seed with Position and Velocity component types (empty data — systems will use defaults)
        world.SetComponent(entityId, "Examples.Components.Position", MessagePackSerializer.Serialize(new float[] { 0f, 0f, 0f }));
        world.SetComponent(entityId, "Examples.Components.Velocity", MessagePackSerializer.Serialize(new float[] { 1f, 0.5f, 0.25f }));
    }
    Console.WriteLine($"[Coordinator] Seeded {seedCount} entities. Total: {world.EntityCount}");
}

// Tick loop
var tickInterval = TimeSpan.FromMilliseconds(1000.0 / tickRate);
ulong tickId = 0;

Console.WriteLine($"[Coordinator] Starting tick loop at {tickRate} Hz ({tickInterval.TotalMilliseconds:F1}ms interval)");

while (!cts.Token.IsCancellationRequested)
{
    var tickStart = DateTime.UtcNow;
    tickId++;

    // 1. Process pending spawns
    while (pendingSpawns.TryDequeue(out var spawnReq))
    {
        var entityId = world.AllocateEntity();
        for (var i = 0; i < spawnReq.ComponentTypes.Length && i < spawnReq.ComponentData.Length; i++)
        {
            world.SetComponent(entityId, spawnReq.ComponentTypes[i], spawnReq.ComponentData[i]);
        }

        var created = new EntityCreated { EntityId = entityId, ComponentTypes = spawnReq.ComponentTypes };
        await nats.PublishAsync("engine.entity.create", MessagePackSerializer.Serialize(created), cancellationToken: cts.Token);
    }

    // 2. Compute execution stages
    var stages = registry.ComputeStages();

    // 3. Execute each stage
    for (var stageIdx = 0; stageIdx < stages.Count; stageIdx++)
    {
        var stage = stages[stageIdx];

        foreach (var sys in stage)
        {
            // Determine which entities match this system's query (reads ∪ writes)
            var allTypes = sys.Reads.Concat(sys.Writes).Distinct().ToList();
            var matchingEntities = world.GetEntitiesWith(allTypes);

            if (matchingEntities.Count == 0)
                continue;

            var entityArray = matchingEntities.ToArray();

            // Send schedule message
            var schedule = new SystemSchedule
            {
                TickId = tickId,
                ShardCount = allTypes.Count
            };
            await nats.PublishAsync(
                $"engine.system.schedule.{sys.Name}",
                MessagePackSerializer.Serialize(schedule),
                cancellationToken: cts.Token);

            // Send one ComponentShard per component type
            foreach (var compType in allTypes)
            {
                // Pack all entity data for this component type into a single byte[]
                var dataChunks = new List<byte[]>();
                foreach (var eid in entityArray)
                {
                    var data = world.GetComponent(eid, compType);
                    dataChunks.Add(data ?? []);
                }

                var shard = new ComponentShard
                {
                    TickId = tickId,
                    Entities = entityArray,
                    ComponentType = compType,
                    Data = MessagePackSerializer.Serialize(dataChunks.ToArray())
                };
                await nats.PublishAsync(
                    $"engine.component.set.{sys.Name}",
                    MessagePackSerializer.Serialize(shard),
                    cancellationToken: cts.Token);
            }
        }

        // Wait for acks from all systems in this stage (with timeout)
        var expectedAcks = new HashSet<string>(stage.Select(s => s.Name));
        var receivedAcks = new HashSet<string>();
        var ackDeadline = DateTime.UtcNow.AddSeconds(5);

        if (expectedAcks.Count > 0)
        {
            var ackSub = await nats.SubscribeCoreAsync<byte[]>("engine.coord.tick.done", cancellationToken: cts.Token);

            try
            {
                while (receivedAcks.Count < expectedAcks.Count && DateTime.UtcNow < ackDeadline)
                {
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
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
                    catch (OperationCanceledException) when (!cts.Token.IsCancellationRequested)
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
                var changesSub = await nats.SubscribeCoreAsync<byte[]>($"engine.component.changed.{sys.Name}", cancellationToken: cts.Token);
                using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                drainCts.CancelAfter(100); // Brief drain window

                try
                {
                    await foreach (var changeMsg in changesSub.Msgs.ReadAllAsync(drainCts.Token))
                    {
                        var changes = MessagePackSerializer.Deserialize<ComponentChanges>(changeMsg.Data!);
                        if (changes.TickId != tickId) continue;

                        var entityData = MessagePackSerializer.Deserialize<byte[][]>(changes.Data);
                        for (var i = 0; i < changes.Entities.Length && i < entityData.Length; i++)
                        {
                            world.SetComponent(changes.Entities[i], changes.ComponentType, entityData[i]);
                        }
                    }
                }
                catch (OperationCanceledException) when (!cts.Token.IsCancellationRequested)
                {
                    // Done draining
                }
                finally
                {
                    await changesSub.UnsubscribeAsync();
                }
            }
        }
    }

    // Log every 100 ticks
    if (tickId % 100 == 0)
    {
        Console.WriteLine($"[Coordinator] Tick {tickId} complete. Entities: {world.EntityCount}, Systems: {registry.GetSystemNames().Count}");
    }

    // Sleep for remainder of tick interval
    var elapsed = DateTime.UtcNow - tickStart;
    var sleepTime = tickInterval - elapsed;
    if (sleepTime > TimeSpan.Zero)
    {
        try { await Task.Delay(sleepTime, cts.Token); }
        catch (OperationCanceledException) { break; }
    }
}

Console.WriteLine($"[Coordinator] Shutting down after {tickId} ticks.");
