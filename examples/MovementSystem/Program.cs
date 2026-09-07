using Client;
using Examples;
using Movement.V1;
using NATS.Client.Core;

await using var nats = new NatsConnection(NatsConfig.CreateOpts());
await nats.ConnectAsync();

await using var ecs = new ECS(nats);
var world = ecs.GetWorld();

var entityCount = int.TryParse(
    Environment.GetEnvironmentVariable("SEED_ENTITIES"), out var ec) ? ec : 10;

for (var i = 0; i < entityCount; i++)
    world.Commands.CreateEntity(
        new Position { X = 0f, Y = 0f, Z = 0f },
        new Velocity { X = 1f, Y = 0.5f, Z = 0.25f });

await world.FlushAsync();
Console.WriteLine($"[Movement] Seeded {entityCount} entities.");

world.AddSystem(new MovementSystem());

await ecs.WaitForShutdownAsync();
