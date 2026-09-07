using Client;
using NATS.Client.Core;
using Nova.Systems;
using Nova.V1;

await using var nats = new NatsConnection(NatsConfig.CreateOpts());
await nats.ConnectAsync();

await using var ecs = new ECS(nats);
var world = ecs.GetWorld();

// NOVA injects NOVA_API and CELL_NAME into every app container; the NOVA_* overrides
// are for running this system outside an instance.
var novaBaseUrl = FirstSet("NOVA_BASE_URL", "NOVA_API") ?? "http://localhost:80";
var novaToken = Environment.GetEnvironmentVariable("NOVA_ACCESS_TOKEN") ?? "";

using var novaClient = new NovaIoClient(novaBaseUrl);
if (!string.IsNullOrEmpty(novaToken))
    novaClient.SetAuthToken(novaToken);

// Demo data: the IO entities the system drives.
var cell = FirstSet("NOVA_CELL", "CELL_NAME") ?? "cell";
var controller = Environment.GetEnvironmentVariable("NOVA_CONTROLLER") ?? "ur10e";

world.Commands.CreateEntity(
    new NovaControllerId { Cell = cell, Controller = controller },
    new DigitalOutputRequest { Io = "DO_1", Value = true },
    new IoOutputState { Io = "DO_1", ValueType = "boolean" });

world.Commands.CreateEntity(
    new NovaControllerId { Cell = cell, Controller = controller },
    new AnalogIntOutputRequest { Io = "AO_1", Value = 42 },
    new IoOutputState { Io = "AO_1", ValueType = "integer" });

world.Commands.CreateEntity(
    new NovaControllerId { Cell = cell, Controller = controller },
    new AnalogFloatOutputRequest { Io = "AO_2", Value = 3.14 },
    new IoOutputState { Io = "AO_2", ValueType = "float" });

await world.FlushAsync();
Console.WriteLine("[SetControllerIO] Seeded demo IO entities.");

world.AddSystem(new SetControllerIOSystem(novaClient));

await ecs.WaitForShutdownAsync();

static string? FirstSet(params string[] names) => names
    .Select(Environment.GetEnvironmentVariable)
    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?
    .Trim();
