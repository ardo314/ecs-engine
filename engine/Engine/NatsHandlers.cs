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
    private readonly ConcurrentQueue<EntitySpawnRequest> _pendingSpawns;

    public NatsHandlers(NatsConnection nats, SystemRegistry registry, ConcurrentQueue<EntitySpawnRequest> pendingSpawns)
    {
        _nats = nats;
        _registry = registry;
        _pendingSpawns = pendingSpawns;
    }

    /// <summary>
    /// Starts all background NATS subscriptions. Returns when cancelled.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var regTask = SubscribeRegistrations(cancellationToken);
        var unregTask = SubscribeUnregistrations(cancellationToken);
        var spawnTask = SubscribeSpawnRequests(cancellationToken);

        await Task.WhenAll(regTask, unregTask, spawnTask);
    }

    private async Task SubscribeRegistrations(CancellationToken cancellationToken)
    {
        var sub = await _nats.SubscribeCoreAsync<byte[]>("engine.system.register", cancellationToken: cancellationToken);
        await foreach (var msg in sub.Msgs.ReadAllAsync(cancellationToken))
        {
            try
            {
                var descriptor = MessagePackSerializer.Deserialize<SystemDescriptor>(msg.Data!);
                _registry.Register(descriptor);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Coordinator] Failed to deserialize registration: {ex.Message}");
            }
        }
    }

    private async Task SubscribeUnregistrations(CancellationToken cancellationToken)
    {
        var sub = await _nats.SubscribeCoreAsync<byte[]>("engine.system.unregister", cancellationToken: cancellationToken);
        await foreach (var msg in sub.Msgs.ReadAllAsync(cancellationToken))
        {
            try
            {
                var unreg = MessagePackSerializer.Deserialize<SystemUnregister>(msg.Data!);
                _registry.Unregister(unreg);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Coordinator] Failed to deserialize unregister: {ex.Message}");
            }
        }
    }

    private async Task SubscribeSpawnRequests(CancellationToken cancellationToken)
    {
        var sub = await _nats.SubscribeCoreAsync<byte[]>("engine.entity.spawn.request", cancellationToken: cancellationToken);
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
}
