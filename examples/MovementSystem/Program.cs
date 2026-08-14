using Client;
using Examples;

await using var ecs = new ECS();
var world = ecs.GetWorld();

var movementSystem = new MovementSystem();
world.AddSystem(movementSystem);

await ecs.WaitForShutdownAsync();
