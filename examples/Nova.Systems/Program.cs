using Client;
using Nova.Systems;

await using var ecs = new ECS();
var world = ecs.GetWorld();

var setControllerIoSystem = new SetControllerIOSystem();
world.AddSystem(setControllerIoSystem);

await ecs.WaitForShutdownAsync();
