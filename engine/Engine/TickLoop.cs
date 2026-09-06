using Ecs.Protocol;
using Ecs.Protocol.V1;
using Google.Protobuf;
using NATS.Client.Core;

namespace Engine.Coordinator;

/// <summary>
/// The fixed-timestep tick loop: apply structural commands, schedule each stage under
/// tick-scoped leases, check what comes back, push watch data.
/// </summary>
public sealed class TickLoop
{
    private static readonly TimeSpan StageDeadline = TimeSpan.FromSeconds(5);

    private readonly NatsConnection _nats;
    private readonly SchemaRegistry _schemas;
    private readonly SystemRegistry _systems;
    private readonly WorldState _world;
    private readonly WatchManager _watches;
    private readonly NatsHandlers _handlers;
    private readonly CommandApplier _commands;
    private readonly LeaseManager _leases = new();
    private readonly int _tickRate;

    public TickLoop(
        NatsConnection nats,
        SchemaRegistry schemas,
        SystemRegistry systems,
        WorldState world,
        WatchManager watches,
        NatsHandlers handlers,
        int tickRate)
    {
        _nats = nats;
        _schemas = schemas;
        _systems = systems;
        _world = world;
        _watches = watches;
        _handlers = handlers;
        _commands = new CommandApplier(world, schemas);
        _tickRate = tickRate;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromMilliseconds(1000.0 / _tickRate);
        var delta = (float)interval.TotalSeconds;
        ulong tick = 0;

        Console.WriteLine(
            $"[Coordinator] Tick loop running at {_tickRate} Hz ({interval.TotalMilliseconds:F1} ms).");

        while (!cancellationToken.IsCancellationRequested)
        {
            var startedAt = DateTime.UtcNow;
            tick++;

            Synchronise();

            var tags = _world.ResolveTags(_systems.UniqueSystems().SelectMany(SystemRegistry.TagsOf));
            foreach (var stage in _systems.ComputeStages(tags))
                await ExecuteStage(stage, tick, delta, tags, cancellationToken);

            await PushWatchData(tick, cancellationToken);

            if (tick % 100 == 0)
            {
                Console.WriteLine(
                    $"[Coordinator] Tick {tick}: {_world.EntityCount} entities, " +
                    $"{_systems.SystemNames().Count} systems, {_schemas.All().Count} types.");
            }

            var remaining = interval - (DateTime.UtcNow - startedAt);
            if (remaining <= TimeSpan.Zero) continue;

            try { await Task.Delay(remaining, cancellationToken); }
            catch (OperationCanceledException) { break; }
        }

        Console.WriteLine($"[Coordinator] Stopped after {tick} ticks.");
    }

    /// <summary>
    /// The deterministic point at which the world's topology may change. Everything
    /// buffered since the last tick lands here, together, before any system runs.
    /// </summary>
    private void Synchronise()
    {
        // Every registered type gets an entity to hang its description off, so a type
        // with no description still shows up in tag joins and in the editor.
        foreach (var type in _schemas.All())
            _world.GetOrCreateTypeEntity(type);

        _commands.Apply(_handlers.DrainCommands());
    }

    private async Task ExecuteStage(
        List<SystemRegistration> stage,
        ulong tick,
        float delta,
        IReadOnlyDictionary<uint, uint[]> tags,
        CancellationToken cancellationToken)
    {
        var outstanding = new Dictionary<string, Lease>(StringComparer.Ordinal);

        foreach (var system in stage)
        {
            if (Invoke(system, tick, delta, tags) is not { } invocation) continue;

            outstanding[invocation.Lease.Id] = invocation.Lease;
            await _nats.PublishAsync(
                Subjects.SystemInvoke(system.Name),
                invocation.Message.ToByteArray(),
                cancellationToken: cancellationToken);
        }

        if (outstanding.Count == 0) return;

        await CollectResults(outstanding, tick, cancellationToken);

        // Whatever did not come back in time loses its claim. A worker that returns tick
        // N's writes during tick N+2 must not be able to land them.
        var abandoned = _leases.RetireThrough(tick);
        if (abandoned > 0)
        {
            Console.WriteLine(
                $"[Coordinator] Tick {tick}: {abandoned} lease(s) expired without a result.");
        }
    }

    private sealed record Invocation(Lease Lease, SystemInvocation Message);

    private Invocation? Invoke(
        SystemRegistration system,
        ulong tick,
        float delta,
        IReadOnlyDictionary<uint, uint[]> tags)
    {
        var entities = _world.MatchQueries(system.Queries, tags);
        if (entities.Count == 0) return null;

        var writable = SystemRegistry.WritesOf(system);
        var lease = _leases.Issue(tick, system.Name, entities, writable);

        var message = new SystemInvocation
        {
            Tick = tick,
            LeaseId = lease.Id,
            DeltaSeconds = delta,
        };
        message.Entities.AddRange(entities);
        message.Writable.AddRange(writable);

        foreach (var (tagTypeId, typeIds) in TagsFor(system, tags))
        {
            var resolution = new TagResolution { TagTypeId = tagTypeId };
            resolution.TypeIds.AddRange(typeIds);
            message.Tags.Add(resolution);
        }

        foreach (var typeId in ColumnsFor(system, tags))
        {
            var rows = new byte[entities.Count][];
            for (var i = 0; i < entities.Count; i++)
                rows[i] = _world.GetComponent(entities[i], typeId)!;

            message.Components.Add(
                ComponentBatchCodecs.Encode(new ComponentColumn(typeId, entities, rows)));
        }

        return new Invocation(lease, message);
    }

    /// <summary>
    /// Every type the system needs to evaluate its queries locally — including excluded
    /// types, which it must be able to see in order to filter them out.
    /// </summary>
    private static HashSet<uint> ColumnsFor(
        SystemRegistration system,
        IReadOnlyDictionary<uint, uint[]> tags)
    {
        var types = new HashSet<uint>();
        foreach (var query in system.Queries)
        {
            foreach (var access in query.Required) types.Add(access.TypeId);
            foreach (var access in query.Optional) types.Add(access.TypeId);
            foreach (var excluded in query.Excluded) types.Add(excluded);
            foreach (var tagged in query.Tagged)
            {
                if (tags.TryGetValue(tagged.TagTypeId, out var resolved))
                    types.UnionWith(resolved);
            }
        }
        return types;
    }

    private static Dictionary<uint, uint[]> TagsFor(
        SystemRegistration system,
        IReadOnlyDictionary<uint, uint[]> tags)
    {
        var result = new Dictionary<uint, uint[]>();
        foreach (var tag in SystemRegistry.TagsOf(system))
            result[tag] = tags.TryGetValue(tag, out var resolved) ? resolved : [];
        return result;
    }

    private async Task CollectResults(
        Dictionary<string, Lease> outstanding,
        ulong tick,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(StageDeadline);

        try
        {
            while (outstanding.Count > 0)
            {
                var result = await _handlers.Results.ReadAsync(deadline.Token);
                if (outstanding.Remove(result.LeaseId))
                    await ApplyResult(result, cancellationToken);
                else
                    await Reject(result, ResultRejectionReason.UnknownLease,
                        "Lease is not outstanding for the current stage.", cancellationToken);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine(
                $"[Coordinator] Tick {tick}: {outstanding.Count} system(s) missed the deadline " +
                $"({string.Join(", ", outstanding.Values.Select(l => l.SystemName))}).");
        }
    }

    /// <summary>
    /// Checks a result against its lease, then applies it.
    /// </summary>
    /// <remarks>
    /// Every condition the plan asks for is enforced here: the lease is live and
    /// single-use, the tick matches, the system only writes types it holds a write lease
    /// for, the entities are inside its assigned slice, the type is registered, and the
    /// payload actually parses as that schema. A result that fails any of them is
    /// refused whole rather than partially applied.
    /// </remarks>
    private async Task ApplyResult(SystemResult result, CancellationToken cancellationToken)
    {
        if (!_leases.TryClaim(result.LeaseId, result.Tick, out var lease, out var reason))
        {
            await Reject(result, reason, "Lease is no longer valid for this tick.", cancellationToken);
            return;
        }

        var columns = new List<ComponentColumn>(result.Writes.Count);

        foreach (var batch in result.Writes)
        {
            if (!lease.Writable.Contains(batch.TypeId))
            {
                await Reject(result, ResultRejectionReason.NotWritable,
                    $"No write lease for type id {batch.TypeId} ({_schemas.NameOf(batch.TypeId)}).",
                    cancellationToken);
                return;
            }

            if (!_schemas.TryGet(batch.TypeId, out var type))
            {
                await Reject(result, ResultRejectionReason.UnknownType,
                    $"Type id {batch.TypeId} is not registered.", cancellationToken);
                return;
            }

            ComponentColumn column;
            try
            {
                column = ComponentBatchCodecs.Decode(batch);
            }
            catch (NotSupportedException ex)
            {
                await Reject(result, ResultRejectionReason.PayloadInvalid, ex.Message, cancellationToken);
                return;
            }

            for (var i = 0; i < column.Count; i++)
            {
                var entity = column.Entities[i];
                if (!lease.Entities.Contains(entity))
                {
                    await Reject(result, ResultRejectionReason.EntityOutOfSlice,
                        $"Entity {entity} is not in the slice leased to '{lease.SystemName}'.",
                        cancellationToken);
                    return;
                }

                var payload = column.Rows[i];
                if (payload is null) continue;

                if (!PayloadValidator.IsValid(type.Descriptor, payload, out var error))
                {
                    await Reject(result, ResultRejectionReason.PayloadInvalid, error, cancellationToken);
                    return;
                }
            }

            columns.Add(column);
        }

        foreach (var column in columns)
        {
            for (var i = 0; i < column.Count; i++)
            {
                var payload = column.Rows[i];
                if (payload is null) continue;

                // A destroy from an earlier tick can have removed the entity in between.
                if (_world.IsAlive(column.Entities[i]))
                    _world.SetComponent(column.Entities[i], column.TypeId, payload);
            }
        }

        // Structural changes wait for the next synchronisation point.
        _handlers.EnqueueCommands(result.Commands);
    }

    private async Task Reject(
        SystemResult result,
        ResultRejectionReason reason,
        string detail,
        CancellationToken cancellationToken)
    {
        Console.WriteLine($"[Coordinator] Refused result from '{result.InstanceId}': {reason} — {detail}");

        if (string.IsNullOrEmpty(result.InstanceId)) return;

        var message = new ResultRejected
        {
            Tick = result.Tick,
            LeaseId = result.LeaseId,
            Reason = reason,
            Detail = detail,
        };

        await _nats.PublishAsync(
            Subjects.SystemRejected(result.InstanceId),
            message.ToByteArray(),
            cancellationToken: cancellationToken);
    }

    private async Task PushWatchData(ulong tick, CancellationToken cancellationToken)
    {
        var watches = _watches.ActiveWatches();
        if (watches.Count == 0) return;

        var schemaVersion = _schemas.Version;

        foreach (var watch in watches)
        {
            var data = new WatchData { WatchId = watch.WatchId, Tick = tick };

            if (_watches.ClaimSystems(watch))
            {
                var systems = _handlers.BuildSystemsResponse();
                data.Systems.AddRange(systems.Systems);
                data.Stages.AddRange(systems.Stages);
            }

            if (_watches.ClaimSchemas(watch, schemaVersion))
                data.ComponentTypes.AddRange(_handlers.DescribeTypes());

            if (watch.IncludeEntities)
            {
                var entities = _handlers.BuildEntitiesResponse(watch.Filter, includeTypes: false);
                data.Entities.AddRange(entities.Entities);
            }

            await _nats.PublishAsync(
                watch.DataSubject, data.ToByteArray(), cancellationToken: cancellationToken);
        }
    }
}
