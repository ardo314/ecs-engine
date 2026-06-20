using Engine.Core;
using Engine.Core.Messages;
using MessagePack;
using NATS.Client.Core;

namespace Client;

/// <summary>
/// Connects to the engine coordinator via NATS and runs a system function each tick.
/// </summary>
public class SystemRunner
{
    private readonly string _systemName;
    private readonly NatsConnection _nats;
    private readonly string[] _reads;
    private readonly string[] _writes;
    private readonly Func<ComponentBuffer, float, Task> _tickHandler;
    private readonly string _instanceId = Guid.NewGuid().ToString("N");

    public SystemRunner(
        string systemName,
        NatsConnection nats,
        string[] reads,
        string[] writes,
        Func<ComponentBuffer, float, Task> tickHandler)
    {
        _systemName = systemName;
        _nats = nats;
        _reads = reads;
        _writes = writes;
        _tickHandler = tickHandler;
    }

    public string InstanceId => _instanceId;

    /// <summary>
    /// Registers with the coordinator, subscribes to component data, and runs the tick loop.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
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
                await _nats.PublishAsync("engine.system.register", registrationBytes, cancellationToken: regCts.Token);
                try { await Task.Delay(1000, regCts.Token); }
                catch (OperationCanceledException) { break; }
            }
        }, regCts.Token);

        Console.WriteLine($"[{_systemName}] Registering (instance {_instanceId}). Reads: [{string.Join(", ", _reads)}], Writes: [{string.Join(", ", _writes)}]");

        // Subscribe to schedule messages
        var scheduleSub = await _nats.SubscribeCoreAsync<byte[]>(
            $"engine.system.schedule.{_systemName}",
            queueGroup: _systemName,
            cancellationToken: cancellationToken);

        // Subscribe to component data
        var dataSub = await _nats.SubscribeCoreAsync<byte[]>(
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
                    await _nats.PublishAsync(
                        $"engine.component.changed.{_systemName}",
                        MessagePackSerializer.Serialize(change),
                        cancellationToken: cancellationToken);
                }

                // Acknowledge tick completion
                var ack = new TickAck { TickId = schedule.TickId, InstanceId = _instanceId };
                await _nats.PublishAsync(
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
                await _nats.PublishAsync(
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
