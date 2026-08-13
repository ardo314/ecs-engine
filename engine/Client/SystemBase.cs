using Engine.Core;
using Engine.Core.Messages;

namespace Client;

/// <summary>
/// Base class for ECS systems. Subclass this, create queries in OnCreate,
/// and implement OnUpdateAsync to process entities each tick.
/// </summary>
public abstract class SystemBase
{
    private readonly List<EntityQuery> _queries = new();

    /// <summary>
    /// The system name used for registration. Derived from the class name
    /// with a trailing "System" suffix stripped (e.g. MovementSystem → Movement).
    /// </summary>
    public string SystemName { get; } = null!;

    protected SystemBase()
    {
        var name = GetType().Name;
        SystemName = name.EndsWith("System", StringComparison.Ordinal) && name.Length > "System".Length
            ? name[..^"System".Length]
            : name;
    }

    /// <summary>
    /// Delta time for the current tick (seconds).
    /// </summary>
    protected internal float DeltaTime { get; internal set; }

    /// <summary>
    /// The current tick identifier.
    /// </summary>
    protected internal ulong TickId { get; internal set; }

    /// <summary>
    /// Command buffer for structural changes (create/destroy entities, add/remove components).
    /// Available in both OnCreate and OnUpdateAsync.
    /// </summary>
    protected internal EntityCommandBuffer Commands { get; } = new();

    /// <summary>
    /// Creates a new EntityQuery and registers it with this system.
    /// Call in OnCreate to define queries; chain With/WithAny/Without to configure.
    /// </summary>
    protected EntityQuery NewQuery()
    {
        var query = new EntityQuery();
        _queries.Add(query);
        return query;
    }

    /// <summary>
    /// Called once after the system is instantiated, before the tick loop starts.
    /// Create queries and perform initialization here.
    /// </summary>
    protected virtual void OnCreate() { }

    /// <summary>
    /// Called every tick. Process entities via your queries here.
    /// </summary>
    protected abstract Task OnUpdateAsync();

    /// <summary>
    /// Called when the system is shutting down. Clean up resources here.
    /// </summary>
    protected virtual void OnDestroy() { }

    // ── Internal plumbing (used by SystemRunner) ────────────

    internal void InvokeOnCreate()
    {
        OnCreate();
        var described = new HashSet<string>();
        foreach (var q in _queries)
        {
            q.Freeze();
            q.Describe(Commands, described);
        }
    }

    internal Task InvokeOnUpdateAsync() => OnUpdateAsync();

    internal void InvokeOnDestroy() => OnDestroy();

    internal IReadOnlyList<EntityQuery> GetQueries() => _queries;

    internal QueryDescriptor[] GetQueryDescriptors() =>
        _queries.Select(q => q.ToDescriptor()).ToArray();

    /// <summary>
    /// Union of all read types across all queries (for conflict detection / staging).
    /// </summary>
    internal string[] GetAllReadTypes() =>
        _queries.SelectMany(q => q.ToDescriptor().ReadTypes).Distinct().ToArray();

    /// <summary>
    /// Union of all write types across all queries (for conflict detection / staging).
    /// </summary>
    internal string[] GetAllWriteTypes() =>
        _queries.SelectMany(q => q.ToDescriptor().WriteTypes).Distinct().ToArray();

    /// <summary>
    /// Union of all component type names needed by any query (for shard subscription).
    /// </summary>
    internal HashSet<string> GetAllTypeNames()
    {
        var set = new HashSet<string>();
        foreach (var q in _queries)
            set.UnionWith(q.GetAllTypeNames());
        return set;
    }
}
