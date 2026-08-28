using Engine.Core;
using Engine.Core.Messages;
using MessagePack;
using NATS.Client.Core;

namespace Client;

/// <summary>
/// Publishes buffered structural changes to the coordinator. Shared by systems
/// and by <see cref="World"/>, which applies commands outside any system.
/// </summary>
internal static class CommandPublisher
{
    /// <summary>
    /// Polls the coordinator's query endpoint until it answers, so commands
    /// published afterwards are not dropped into the void.
    /// </summary>
    internal static async Task WaitForCoordinatorAsync(INatsConnection nats, CancellationToken ct)
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

    /// <summary>
    /// Publishes and clears every command buffered in <paramref name="ecb"/>.
    /// </summary>
    internal static async Task PublishAsync(
        INatsConnection nats, EntityCommandBuffer ecb, CancellationToken ct)
    {
        if (!ecb.HasPendingCommands) return;

        foreach (var spawn in ecb.Spawns)
        {
            await nats.PublishAsync(
                "engine.entity.spawn.request",
                MessagePackSerializer.Serialize(spawn),
                cancellationToken: ct);
        }

        if (ecb.Destroys.Count > 0)
        {
            var destroyReq = new EntityDestroyRequest { EntityIds = ecb.Destroys.ToArray() };
            await nats.PublishAsync(
                "engine.entity.destroy.request",
                MessagePackSerializer.Serialize(destroyReq),
                cancellationToken: ct);
        }

        foreach (var add in ecb.Adds)
        {
            await nats.PublishAsync(
                "engine.entity.component.add",
                MessagePackSerializer.Serialize(add),
                cancellationToken: ct);
        }

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
