using Client;
using Engine.Core;
using Nova.Components;
using Nova.Systems;

Serialization.Initialize();

var natsUrl = Environment.GetEnvironmentVariable("NATS_URL") ?? "nats://localhost:4222";
var novaBaseUrl = Environment.GetEnvironmentVariable("NOVA_BASE_URL") ?? "http://localhost:80";
var novaToken = Environment.GetEnvironmentVariable("NOVA_ACCESS_TOKEN") ?? "";

Console.WriteLine("[SetControllerIO] Starting...");

using var novaClient = new NovaIoClient(novaBaseUrl);
if (!string.IsNullOrEmpty(novaToken))
    novaClient.SetAuthToken(novaToken);

var tickCount = 0ul;

await using var runner = new SystemRunner(
    systemName: "SetControllerIO",
    natsUrl: natsUrl,
    reads: [
        ComponentTypeId.Of<ControllerRef>().TypeName,
        ComponentTypeId.Of<DigitalOutputRequest>().TypeName,
        ComponentTypeId.Of<AnalogIntOutputRequest>().TypeName,
        ComponentTypeId.Of<AnalogFloatOutputRequest>().TypeName
    ],
    writes: [ComponentTypeId.Of<IoOutputState>().TypeName],
    tickHandler: async (buffer, dt) =>
    {
        var controllers = buffer.GetComponents<ControllerRef>();
        var digitalRequests = buffer.GetComponents<DigitalOutputRequest>();
        var analogIntRequests = buffer.GetComponents<AnalogIntOutputRequest>();
        var analogFloatRequests = buffer.GetComponents<AnalogFloatOutputRequest>();

        // Build lookups by entity ID
        var digitalMap = digitalRequests.ToDictionary(x => x.Item1.Id, x => x.Item2);
        var analogIntMap = analogIntRequests.ToDictionary(x => x.Item1.Id, x => x.Item2);
        var analogFloatMap = analogFloatRequests.ToDictionary(x => x.Item1.Id, x => x.Item2);

        // Group IO requests by (cell, controller) for batching
        var batches = new Dictionary<(string Cell, string Controller), List<(Entity Entity, IoValuePayload Payload)>>();

        foreach (var (entity, controllerRef) in controllers)
        {
            var key = (controllerRef.Cell, controllerRef.Controller);
            if (!batches.ContainsKey(key))
                batches[key] = [];

            if (digitalMap.TryGetValue(entity.Id, out var dig))
                batches[key].Add((entity, IoValuePayload.Boolean(dig.Io, dig.Value)));

            if (analogIntMap.TryGetValue(entity.Id, out var ai))
                batches[key].Add((entity, IoValuePayload.Integer(ai.Io, ai.Value)));

            if (analogFloatMap.TryGetValue(entity.Id, out var af))
                batches[key].Add((entity, IoValuePayload.Float(af.Io, af.Value)));
        }

        // Send batched IO requests to Nova
        foreach (var ((cell, controller), entries) in batches)
        {
            var payloads = entries.Select(e => e.Payload).ToList();
            var success = await novaClient.SetOutputValuesAsync(cell, controller, payloads);

            // Write confirmation state back to entities
            foreach (var (entity, payload) in entries)
            {
                var state = new IoOutputState(
                    Io: payload.Io,
                    ValueType: payload.ValueType,
                    Value: payload.Value?.ToString() ?? "",
                    Confirmed: success);
                buffer.SetComponent(entity, state);
            }

            if (success)
                Console.WriteLine($"[SetControllerIO] Set {payloads.Count} IO(s) on {cell}/{controller}");
            else
                Console.WriteLine($"[SetControllerIO] FAILED to set IO(s) on {cell}/{controller}");
        }

        tickCount++;
    });

await runner.ConnectAsync();

// Spawn example entities — one digital output and one analog output
Console.WriteLine("[SetControllerIO] Spawning example IO entities...");

var cell = Environment.GetEnvironmentVariable("NOVA_CELL") ?? "cell";
var controller = Environment.GetEnvironmentVariable("NOVA_CONTROLLER") ?? "ur10e";

await runner.SpawnEntityAsync(
    new ControllerRef(cell, controller),
    new DigitalOutputRequest("DO_1", true));

await runner.SpawnEntityAsync(
    new ControllerRef(cell, controller),
    new AnalogIntOutputRequest("AO_1", 42));

await runner.SpawnEntityAsync(
    new ControllerRef(cell, controller),
    new AnalogFloatOutputRequest("AO_2", 3.14));

Console.WriteLine("[SetControllerIO] Entities spawned. Running...");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

await runner.RunAsync(cts.Token);
