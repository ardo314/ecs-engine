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
/// Connects a <see cref="SystemBase"/> to the engine coordinator via NATS
/// and runs its tick loop.
/// </summary>
public class SystemRunner : IAsyncDisposable
{
    private readonly SystemBase _system;
    private readonly string _natsUrl;
    private NatsConnection? _nats;
    private readonly string _instanceId = Guid.NewGuid().ToString("N");

    public SystemRunner(SystemBase system, string? natsUrl = null)
    {
        _system = system;
        _natsUrl = natsUrl ?? Environment.GetEnvironmentVariable("NATS_URL") ?? "nats://localhost:4222";
    }

    public string InstanceId => _instanceId;

    public string SystemName => _system.SystemName;

    /// <summary>
    /// Connects to the message transport.
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        _nats = new NatsConnection(new NatsOpts { Url = _natsUrl });
        await _nats.ConnectAsync();
        Console.WriteLine($"[{SystemName}] Connected to transport.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_nats is not null)
            await _nats.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Registers with the coordinator, subscribes to component data, and runs the tick loop.
    /// Calls OnUpdateAsync each tick, then flushes query mutations and ECB commands.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var nats = _nats ?? throw new InvalidOperationException("Call ConnectAsync before RunAsync.");
        var systemName = SystemName;

        // Build registration descriptor from all queries
        var descriptor = new SystemDescriptor
        {
            Name = systemName,
            InstanceId = _instanceId,
            Queries = _system.GetQueryDescriptors()
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

        var reads = _system.GetAllReadTypes();
        var writes = _system.GetAllWriteTypes();
        Console.WriteLine($"[{systemName}] Registering (instance {_instanceId}). Reads: [{string.Join(", ", reads)}], Writes: [{string.Join(", ", writes)}]");

        // Subscribe to schedule messages
        var scheduleSub = await nats.SubscribeCoreAsync<byte[]>(
            $"engine.system.schedule.{systemName}",
            queueGroup: systemName,
            cancellationToken: cancellationToken);

        // Subscribe to component data
        var dataSub = await nats.SubscribeCoreAsync<byte[]>(
            $"engine.component.set.{systemName}",
            queueGroup: systemName,
            cancellationToken: cancellationToken);

        // OnCreate commands are dropped if the coordinator is not subscribed yet, and a
        // system with no entities is never scheduled — so wait for a reply before flushing.
        await WaitForCoordinatorAsync(nats, cancellationToken);
        await FlushEcbAsync(nats, cancellationToken);

        var firstSchedule = true;

        try
        {
            await foreach (var schedMsg in scheduleSub.Msgs.ReadAllAsync(cancellationToken))
            {
                if (firstSchedule)
                {
                    firstSchedule = false;
                    await regCts.CancelAsync();
                    Console.WriteLine($"[{systemName}] Registered successfully — receiving ticks.");
                }

                var schedule = MessagePackSerializer.Deserialize<SystemSchedule>(schedMsg.Data!);

                // Receive shards into a dictionary
                var shards = new Dictionary<string, (ulong[] Entities, byte[][] Data)>();
                var shardsReceived = 0;
                using var shardCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                shardCts.CancelAfter(TimeSpan.FromSeconds(5));

                try
                {
                    await foreach (var dataMsg in dataSub.Msgs.ReadAllAsync(shardCts.Token))
                    {
                        var shard = MessagePackSerializer.Deserialize<ComponentShard>(dataMsg.Data!);
                        if (shard.TickId != schedule.TickId) continue;

                        var entityData = MessagePackSerializer.Deserialize<byte[][]>(shard.Data);
                        shards[shard.ComponentType] = (shard.Entities, entityData);
                        shardsReceived++;

                        if (shardsReceived >= schedule.ShardCount)
                            break;
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    Console.WriteLine($"[{systemName}] Timeout waiting for shards (got {shardsReceived}/{schedule.ShardCount})");
                }

                // Populate all queries with the shard data
                foreach (var query in _system.GetQueries())
                {
                    query.Populate(shards, schedule.TickId);
                }

                // Set tick state on the system
                _system.DeltaTime = 1.0f / 20f; // TODO: receive from schedule
                _system.TickId = schedule.TickId;

                // Execute the system's update
                await _system.InvokeOnUpdateAsync();

                // Flush query mutations
                foreach (var query in _system.GetQueries())
                {
                    var mutations = query.FlushMutations();
                    foreach (var change in mutations)
                    {
                        await nats.PublishAsync(
                            $"engine.component.changed.{systemName}",
                            MessagePackSerializer.Serialize(change),
                            cancellationToken: cancellationToken);
                    }
                }

                // Flush ECB commands
                await FlushEcbAsync(nats, cancellationToken);

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
            var unreg = new SystemUnregister { Name = systemName, InstanceId = _instanceId };
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
            Console.WriteLine($"[{systemName}] Shut down.");
        }
    }

    /// <summary>
    /// Polls the coordinator's query endpoint until it answers.
    /// </summary>
    private static async Task WaitForCoordinatorAsync(NatsConnection nats, CancellationToken ct)
    {
        var announced = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attempt.CancelAfter(TimeSpan.FromSeconds(2));
                await nats.RequestAsync<byte[], byte[]>(
                    "engine.query.systems", [], cancellationToken: attempt.Token);
                return;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                if (!announced)
                {
                    announced = true;
                    Console.WriteLine("Waiting for coordinator...");
                }
                await Task.Delay(250, ct);
            }
        }
    }

    private async Task FlushEcbAsync(NatsConnection nats, CancellationToken ct)    {
        var ecb = _system.Commands;
        if (!ecb.HasPendingCommands) return;

        // Spawns
        foreach (var spawn in ecb.Spawns)
        {
            await nats.PublishAsync(
                "engine.entity.spawn.request",
                MessagePackSerializer.Serialize(spawn),
                cancellationToken: ct);
        }

        // Destroys
        if (ecb.Destroys.Count > 0)
        {
            var destroyReq = new EntityDestroyRequest { EntityIds = ecb.Destroys.ToArray() };
            await nats.PublishAsync(
                "engine.entity.destroy.request",
                MessagePackSerializer.Serialize(destroyReq),
                cancellationToken: ct);
        }

        // Component adds
        foreach (var add in ecb.Adds)
        {
            await nats.PublishAsync(
                "engine.entity.component.add",
                MessagePackSerializer.Serialize(add),
                cancellationToken: ct);
        }

        // Component removes
        foreach (var remove in ecb.Removes)
        {
            await nats.PublishAsync(
                "engine.entity.component.remove",
                MessagePackSerializer.Serialize(remove),
                cancellationToken: ct);
        }

        ecb.Clear();
    }
}
