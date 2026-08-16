using Client;
using Engine.Core;
using Nova.Components;

namespace Nova.Systems;

public class SetControllerIOSystem : SystemBase
{
    private EntityQuery _q = null!;
    private NovaIoClient _novaClient = null!;
    private ulong _tickCount;

    protected override void OnCreate()
    {
        _q = NewQuery()
            .With(Query.ReadOnly<NovaControllerId>())
            .With(Query.ReadWrite<IoOutputState>())
            .WithAny(
                Query.ReadOnly<DigitalOutputRequest>(),
                Query.ReadOnly<AnalogIntOutputRequest>(),
                Query.ReadOnly<AnalogFloatOutputRequest>());

        var novaBaseUrl = Environment.GetEnvironmentVariable("NOVA_BASE_URL") ?? "http://localhost:80";
        var novaToken = Environment.GetEnvironmentVariable("NOVA_ACCESS_TOKEN") ?? "";

        _novaClient = new NovaIoClient(novaBaseUrl);
        if (!string.IsNullOrEmpty(novaToken))
            _novaClient.SetAuthToken(novaToken);

        // Spawn example IO entities
        var cell = Environment.GetEnvironmentVariable("NOVA_CELL") ?? "cell";
        var controller = Environment.GetEnvironmentVariable("NOVA_CONTROLLER") ?? "ur10e";

        Commands.CreateEntity(
            new NovaControllerId(cell, controller),
            new DigitalOutputRequest("DO_1", true));

        Commands.CreateEntity(
            new NovaControllerId(cell, controller),
            new AnalogIntOutputRequest("AO_1", 42));

        Commands.CreateEntity(
            new NovaControllerId(cell, controller),
            new AnalogFloatOutputRequest("AO_2", 3.14));

        Console.WriteLine("[SetControllerIO] Entities queued for spawning.");
    }

    protected override void OnDestroy()
    {
        _novaClient?.Dispose();
    }

    protected override async Task OnUpdateAsync()
    {
        // Group IO requests by (cell, controller) for batching
        var batches = new Dictionary<(string Cell, string Controller), List<(Entity Entity, IoValuePayload Payload)>>();

        foreach (var entity in _q.Entities)
        {
            var controllerId = _q.Get<NovaControllerId>(entity);
            var key = (controllerId.Cell, controllerId.Controller);
            if (!batches.ContainsKey(key))
                batches[key] = [];

            if (_q.TryGet<DigitalOutputRequest>(entity, out var dig))
                batches[key].Add((entity, IoValuePayload.Boolean(dig.Io, dig.Value)));

            if (_q.TryGet<AnalogIntOutputRequest>(entity, out var ai))
                batches[key].Add((entity, IoValuePayload.Integer(ai.Io, ai.Value)));

            if (_q.TryGet<AnalogFloatOutputRequest>(entity, out var af))
                batches[key].Add((entity, IoValuePayload.Float(af.Io, af.Value)));
        }

        // Send batched IO requests to Nova
        foreach (var ((cell, controller), entries) in batches)
        {
            var payloads = entries.Select(e => e.Payload).ToList();
            var success = await _novaClient.SetOutputValuesAsync(cell, controller, payloads);

            foreach (var (entity, payload) in entries)
            {
                var state = new IoOutputState(
                    Io: payload.Io,
                    ValueType: payload.ValueType,
                    Value: payload.Value?.ToString() ?? "",
                    Confirmed: success);
                _q.Set(entity, state);
            }

            if (success)
                Console.WriteLine($"[SetControllerIO] Set {payloads.Count} IO(s) on {cell}/{controller}");
            else
                Console.WriteLine($"[SetControllerIO] FAILED to set IO(s) on {cell}/{controller}");
        }

        _tickCount++;
    }
}
