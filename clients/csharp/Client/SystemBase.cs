using Ecs.Protocol.V1;
using Engine.Core;

namespace Client;

/// <summary>
/// Base class for ECS systems. Subclass it, declare queries in the constructor, and
/// implement <see cref="OnUpdateAsync"/>.
/// </summary>
/// <remarks>
/// A system declares its access up front and gets nothing it did not ask for. That is
/// what lets the coordinator schedule it: two systems that only read the same types run
/// in parallel, anything else is serialised, and neither system had to know the other
/// exists.
/// </remarks>
public abstract class SystemBase
{
    private readonly List<EntityQuery> _queries = new();
    private bool _queriesFrozen;

    /// <summary>
    /// The registration name, derived from the class name with a trailing "System"
    /// stripped — <c>MovementSystem</c> becomes <c>Movement</c>.
    /// </summary>
    public string SystemName { get; }

    protected SystemBase()
    {
        var name = GetType().Name;
        SystemName = name.EndsWith("System", StringComparison.Ordinal) && name.Length > "System".Length
            ? name[..^"System".Length]
            : name;
    }

    /// <summary>Seconds covered by the current tick, as told by the invocation.</summary>
    protected internal float DeltaTime { get; internal set; }

    /// <summary>The tick this invocation's lease is scoped to.</summary>
    protected internal ulong TickId { get; internal set; }

    /// <summary>
    /// Structural changes. Buffered here and applied by the coordinator at its next
    /// synchronisation point, never while a system is iterating.
    /// </summary>
    protected internal EntityCommandBuffer Commands { get; } = new();

    /// <summary>
    /// Declares a query. Call this in the constructor so query fields can be
    /// <c>readonly</c>; it throws once the system has joined a world.
    /// </summary>
    protected EntityQuery NewQuery()
    {
        if (_queriesFrozen)
            throw new InvalidOperationException(
                $"System '{SystemName}' cannot declare queries after joining a world. " +
                "Declare them in the constructor.");

        var query = new EntityQuery();
        _queries.Add(query);
        return query;
    }

    /// <summary>
    /// Called when the system joins a world, before its tick loop starts. Buffer seed
    /// commands and acquire world-scoped resources here. May fire more than once on the
    /// same instance, so keep it idempotent.
    /// </summary>
    protected virtual void OnAdd() { }

    /// <summary>Called once per tick, over the slice the lease covers.</summary>
    protected abstract Task OnUpdateAsync();

    /// <summary>Called when the system leaves a world.</summary>
    protected virtual void OnRemove() { }

    // ── Internal plumbing ───────────────────────────────────────

    internal void InvokeOnAdd()
    {
        // Queries are declared once in the constructor, so freezing is idempotent and the
        // same instance can leave a world and rejoin it.
        _queriesFrozen = true;
        foreach (var query in _queries) query.Freeze();

        OnAdd();
    }

    internal Task InvokeOnUpdateAsync() => OnUpdateAsync();

    internal void InvokeOnRemove() => OnRemove();

    internal IReadOnlyList<EntityQuery> GetQueries() => _queries;

    /// <summary>Every schema this system needs the coordinator to bind before it runs.</summary>
    internal IEnumerable<ComponentTypeDeclaration> Declarations() =>
        _queries.SelectMany(q => q.Declarations()).Concat(Commands.Schemas);

    internal void BindQueries(SchemaBindings bindings)
    {
        foreach (var query in _queries) query.Bind(bindings);
    }

    internal QueryDescriptor[] QueryDescriptors() => [.. _queries.Select(q => q.ToDescriptor())];

    internal IEnumerable<string> ReadNames() =>
        _queries.SelectMany(q => q.ReadNames()).Distinct(StringComparer.Ordinal);

    internal IEnumerable<string> WriteNames() =>
        _queries.SelectMany(q => q.WriteNames()).Distinct(StringComparer.Ordinal);
}
