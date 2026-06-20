using Client;
using Engine.Core;
using Examples.Components;
using NATS.Client.Core;

var natsUrl = Environment.GetEnvironmentVariable("NATS_URL") ?? "nats://localhost:4222";

Console.WriteLine("MovementSystem starting...");

await using var nats = new NatsConnection(new NatsOpts { Url = natsUrl });
await nats.ConnectAsync();

Console.WriteLine($"Connected to NATS at {natsUrl}");

var tickCount = 0ul;

var runner = new SystemRunner(
    systemName: "Movement",
    nats: nats,
    reads: [ComponentTypeId.Of<Velocity>().TypeName],
    writes: [ComponentTypeId.Of<Position>().TypeName],
    tickHandler: async (buffer, dt) =>
    {
        var positions = buffer.GetComponents<Position>();
        var velocities = buffer.GetComponents<Velocity>();

        // Build velocity lookup by entity
        var velocityMap = new Dictionary<ulong, Velocity>();
        foreach (var (entity, vel) in velocities)
        {
            velocityMap[entity.Id] = vel;
        }

        // Update positions: position += velocity * dt
        foreach (var (entity, pos) in positions)
        {
            if (velocityMap.TryGetValue(entity.Id, out var vel))
            {
                var newPos = new Position(
                    pos.X + vel.X * dt,
                    pos.Y + vel.Y * dt,
                    pos.Z + vel.Z * dt);
                buffer.SetComponent(entity, newPos);
            }
        }

        tickCount++;
        if (tickCount % 20 == 0)
        {
            var sample = buffer.GetComponents<Position>();
            if (sample.Length > 0)
            {
                var (e, p) = sample[0];
                Console.WriteLine($"[Movement] Tick {tickCount} | Entity {e.Id} Position: ({p.X:F2}, {p.Y:F2}, {p.Z:F2})");
            }
        }

        await Task.CompletedTask;
    });

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

await runner.RunAsync(cts.Token);
