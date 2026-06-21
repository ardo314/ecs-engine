using Client;
using Engine.Core;
using Examples.Components;

Serialization.Initialize();

var natsUrl = Environment.GetEnvironmentVariable("NATS_URL") ?? "nats://localhost:4222";
var entityCount = int.TryParse(Environment.GetEnvironmentVariable("SEED_ENTITIES"), out var ec) ? ec : 10;

Console.WriteLine("MovementSystem starting...");

var tickCount = 0ul;

await using var runner = new SystemRunner(
    systemName: "Movement",
    natsUrl: natsUrl,
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

await runner.ConnectAsync();

// Spawn entities via the coordinator
Console.WriteLine($"[Movement] Requesting {entityCount} entities...");
for (var i = 0; i < entityCount; i++)
{
    await runner.SpawnEntityAsync(new Position(0f, 0f, 0f), new Velocity(1f, 0.5f, 0.25f));
}
Console.WriteLine($"[Movement] Spawn requests sent.");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

await runner.RunAsync(cts.Token);
