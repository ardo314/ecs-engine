using Engine.Core;
using Engine.Core.Messages;
using MessagePack;

namespace Client;

/// <summary>
/// A type-safe query over entities and their components.
/// Built via fluent methods in the system constructor, then populated each tick with shard data.
/// Provides per-entity Get/Set/TryGet and bulk iteration via Each.
/// </summary>
public class EntityQuery
{
    // ── Builder state ──────────────────────────────────────────────

    private readonly List<ComponentAccess> _required = new();
    private readonly List<ComponentAccess> _optional = new();
    private readonly List<string> _excluded = new();
    private readonly List<ComponentAccess> _described = new();
    private readonly List<string> _tags = new();
    private bool _frozen;

    // All component type names this query knows about (required + optional),
    // used to populate data from shards.
    private HashSet<string> _allTypes = new();

    // Read-only type names (for write enforcement)
    private HashSet<string> _readOnlyTypes = new();

    // ── Runtime state (populated each tick) ─────────────────────

    // Tag type name → component type names carrying it, as resolved for the current tick
    private Dictionary<string, string[]> _resolvedTags = new();

    // Entity-indexed component data: typeName → (entityId → deserialized bytes)
    private readonly Dictionary<string, Dictionary<ulong, byte[]>> _data = new();

    // Entities matching this query's filter for the current tick
    private readonly List<Entity> _matchedEntities = new();

    // Buffered mutations from Set<T>()
    private readonly Dictionary<string, Dictionary<ulong, byte[]>> _mutations = new();

    private ulong _tickId;

    // ── Builder methods (called in the system constructor) ──────

    /// <summary>
    /// Adds a required component. Entities must have this component to match.
    /// </summary>
    public EntityQuery With(ComponentAccess access)
    {
        ThrowIfFrozen();
        _required.Add(access);
        _described.Add(access);
        return this;
    }

    /// <summary>
    /// Adds optional components. Entities must have at least one to match.
    /// Use TryGet to access these at runtime.
    /// </summary>
    public EntityQuery WithAny(params ComponentAccess[] accesses)
    {
        ThrowIfFrozen();
        _optional.AddRange(accesses);
        _described.AddRange(accesses);
        return this;
    }

    /// <summary>
    /// Excludes entities that have the specified component.
    /// </summary>
    public EntityQuery Without<T>() where T : IComponent
    {
        ThrowIfFrozen();
        var access = Query.ReadOnly<T>();
        _excluded.Add(access.TypeName);
        _described.Add(access);
        return this;
    }

    /// <summary>
    /// Joins through the type system: entities must carry at least one component whose
    /// component type entity has <typeparamref name="TTag"/>. The matching component types
    /// are resolved by the coordinator every tick, so types added later are picked up
    /// without changing this query. Tagged components are read-only — access them via
    /// <see cref="GetTagged{TTag}"/> or, when the concrete type is known, <see cref="TryGet{T}"/>.
    /// </summary>
    public EntityQuery WithAnyTagged<TTag>() where TTag : IComponent
    {
        ThrowIfFrozen();
        var access = Query.ReadOnly<TTag>();
        _tags.Add(access.TypeName);
        _described.Add(access);
        return this;
    }

    // ── Internal lifecycle ──────────────────────────────────────

    /// <summary>
    /// Buffers each queried component type's own description.
    /// </summary>
    internal void Describe(EntityCommandBuffer commands)
    {
        foreach (var access in _described)
            access.Describe?.Invoke(commands);
    }

    internal void Freeze()
    {
        _frozen = true;
        _allTypes = new HashSet<string>(
            _required.Select(a => a.TypeName)
                .Concat(_optional.Select(a => a.TypeName)));
        _readOnlyTypes = new HashSet<string>(
            _required.Where(a => !a.IsReadWrite).Select(a => a.TypeName)
                .Concat(_optional.Where(a => !a.IsReadWrite).Select(a => a.TypeName)));
    }

    /// <summary>
    /// Populates the query from component shards received for this tick.
    /// Builds entity-indexed lookups and filters entities to those matching requirements.
    /// <paramref name="resolvedTags"/> maps each tag type name to the component type
    /// names carrying it for this tick.
    /// </summary>
    internal void Populate(
        Dictionary<string, (ulong[] Entities, byte[][] Data)> shards,
        ulong tickId,
        IReadOnlyDictionary<string, string[]>? resolvedTags = null)
    {
        _tickId = tickId;
        _data.Clear();
        _matchedEntities.Clear();
        _mutations.Clear();

        _resolvedTags = new Dictionary<string, string[]>();
        var taggedTypes = new HashSet<string>();
        foreach (var tag in _tags)
        {
            var types = resolvedTags is not null && resolvedTags.TryGetValue(tag, out var t) ? t : [];
            _resolvedTags[tag] = types;
            taggedTypes.UnionWith(types);
        }

        // Build entity-indexed lookups for all types this query cares about
        foreach (var (typeName, (entities, data)) in shards)
        {
            if (!_allTypes.Contains(typeName) && !_excluded.Contains(typeName)
                && !taggedTypes.Contains(typeName))
                continue;

            var dict = new Dictionary<ulong, byte[]>();
            for (var i = 0; i < entities.Length && i < data.Length; i++)
            {
                if (data[i].Length > 0)
                    dict[entities[i]] = data[i];
            }
            _data[typeName] = dict;
        }

        // Determine the candidate entity set from the first required type's entities
        HashSet<ulong>? candidates = null;

        foreach (var req in _required)
        {
            if (!_data.TryGetValue(req.TypeName, out var dict))
            {
                // Required type has no data at all → no entities match
                return;
            }

            if (candidates is null)
                candidates = new HashSet<ulong>(dict.Keys);
            else
                candidates.IntersectWith(dict.Keys);
        }

        // Each tag requires at least one component of a type carrying it
        foreach (var tag in _tags)
        {
            var withTag = new HashSet<ulong>();
            foreach (var typeName in _resolvedTags[tag])
            {
                if (_data.TryGetValue(typeName, out var dict))
                    withTag.UnionWith(dict.Keys);
            }

            if (candidates is null)
                candidates = withTag;
            else
                candidates.IntersectWith(withTag);
        }

        if (candidates is null)
        {
            // No required types — shouldn't happen, but bail
            return;
        }

        // Filter by WithAny: at least one optional type must be present
        if (_optional.Count > 0)
        {
            candidates.RemoveWhere(entityId =>
            {
                foreach (var opt in _optional)
                {
                    if (_data.TryGetValue(opt.TypeName, out var dict) && dict.ContainsKey(entityId))
                        return false; // keep — has at least one
                }
                return true; // remove — has none
            });
        }

        // Filter by Without: exclude entities that have any excluded type
        foreach (var excludedType in _excluded)
        {
            if (_data.TryGetValue(excludedType, out var dict))
            {
                candidates.ExceptWith(dict.Keys);
            }
        }

        foreach (var entityId in candidates)
        {
            _matchedEntities.Add(new Entity(entityId));
        }
    }

    // ── Data access (called in OnUpdate) ─────────────────────

    /// <summary>
    /// All entities matching this query for the current tick.
    /// </summary>
    public IReadOnlyList<Entity> Entities => _matchedEntities;

    /// <summary>
    /// Gets a component value for the given entity. Throws if not found.
    /// </summary>
    public T Get<T>(Entity entity) where T : IComponent
    {
        var typeName = ComponentTypeId.Of<T>().TypeName;
        if (_data.TryGetValue(typeName, out var dict) && dict.TryGetValue(entity.Id, out var bytes))
            return MessagePackSerializer.Deserialize<T>(bytes);

        throw new KeyNotFoundException(
            $"Entity {entity.Id} does not have component {typeof(T).Name} in this query.");
    }

    /// <summary>
    /// Tries to get a component value for the given entity.
    /// </summary>
    public bool TryGet<T>(Entity entity, out T component) where T : IComponent
    {
        var typeName = ComponentTypeId.Of<T>().TypeName;
        if (_data.TryGetValue(typeName, out var dict) && dict.TryGetValue(entity.Id, out var bytes))
        {
            component = MessagePackSerializer.Deserialize<T>(bytes);
            return true;
        }
        component = default!;
        return false;
    }

    /// <summary>
    /// Checks if the given entity has the specified component in this query's data.
    /// </summary>
    public bool Has<T>(Entity entity) where T : IComponent
    {
        var typeName = ComponentTypeId.Of<T>().TypeName;
        return _data.TryGetValue(typeName, out var dict) && dict.ContainsKey(entity.Id);
    }

    /// <summary>
    /// Buffers a component mutation. Throws if the component was declared ReadOnly.
    /// </summary>
    public void Set<T>(Entity entity, T component) where T : IComponent
    {
        var typeName = ComponentTypeId.Of<T>().TypeName;

        if (_readOnlyTypes.Contains(typeName))
            throw new InvalidOperationException(
                $"Cannot write to component {typeof(T).Name} — it was declared as ReadOnly in this query.");

        if (IsTagged(typeName))
            throw new InvalidOperationException(
                $"Cannot write to component {typeof(T).Name} — it is only in this query through a tag join, which is read-only.");

        if (!_mutations.TryGetValue(typeName, out var dict))
        {
            dict = new Dictionary<ulong, byte[]>();
            _mutations[typeName] = dict;
        }
        dict[entity.Id] = MessagePackSerializer.Serialize(component);
    }

    // ── Tag joins ───────────────────────────────────────────

    /// <summary>
    /// The component type names carrying <typeparamref name="TTag"/> this tick.
    /// </summary>
    public IReadOnlyList<string> TaggedTypeNames<TTag>() where TTag : IComponent =>
        _resolvedTags.TryGetValue(ComponentTypeId.Of<TTag>().TypeName, out var types) ? types : [];

    /// <summary>
    /// The components on <paramref name="entity"/> whose type carries <typeparamref name="TTag"/>.
    /// Values are raw because the concrete types are only known at runtime — use
    /// <see cref="TaggedComponent.As{T}"/> once the type name identifies one you know.
    /// </summary>
    public IEnumerable<TaggedComponent> GetTagged<TTag>(Entity entity) where TTag : IComponent
    {
        foreach (var typeName in TaggedTypeNames<TTag>())
        {
            if (_data.TryGetValue(typeName, out var dict) && dict.TryGetValue(entity.Id, out var bytes))
                yield return new TaggedComponent(typeName, bytes);
        }
    }

    private bool IsTagged(string typeName)
    {
        foreach (var (_, types) in _resolvedTags)
        {
            if (Array.IndexOf(types, typeName) >= 0 && !_allTypes.Contains(typeName))
                return true;
        }
        return false;
    }

    // ── Each — tuple iteration ──────────────────────────────

    public IEnumerable<(Entity Entity, T1 C1)> Each<T1>()
        where T1 : IComponent
    {
        foreach (var entity in _matchedEntities)
        {
            if (TryGet<T1>(entity, out var c1))
                yield return (entity, c1);
        }
    }

    public IEnumerable<(Entity Entity, T1 C1, T2 C2)> Each<T1, T2>()
        where T1 : IComponent
        where T2 : IComponent
    {
        foreach (var entity in _matchedEntities)
        {
            if (TryGet<T1>(entity, out var c1) && TryGet<T2>(entity, out var c2))
                yield return (entity, c1, c2);
        }
    }

    public IEnumerable<(Entity Entity, T1 C1, T2 C2, T3 C3)> Each<T1, T2, T3>()
        where T1 : IComponent
        where T2 : IComponent
        where T3 : IComponent
    {
        foreach (var entity in _matchedEntities)
        {
            if (TryGet<T1>(entity, out var c1) && TryGet<T2>(entity, out var c2) && TryGet<T3>(entity, out var c3))
                yield return (entity, c1, c2, c3);
        }
    }

    public IEnumerable<(Entity Entity, T1 C1, T2 C2, T3 C3, T4 C4)> Each<T1, T2, T3, T4>()
        where T1 : IComponent
        where T2 : IComponent
        where T3 : IComponent
        where T4 : IComponent
    {
        foreach (var entity in _matchedEntities)
        {
            if (TryGet<T1>(entity, out var c1) && TryGet<T2>(entity, out var c2) &&
                TryGet<T3>(entity, out var c3) && TryGet<T4>(entity, out var c4))
                yield return (entity, c1, c2, c3, c4);
        }
    }

    // ── Internal: flush mutations + descriptor ──────────────

    internal List<ComponentChanges> FlushMutations()
    {
        var result = new List<ComponentChanges>();
        foreach (var (compType, dict) in _mutations)
        {
            if (dict.Count == 0) continue;
            result.Add(new ComponentChanges
            {
                TickId = _tickId,
                ComponentType = compType,
                Entities = dict.Keys.ToArray(),
                Data = MessagePackSerializer.Serialize(dict.Values.ToArray())
            });
        }
        _mutations.Clear();
        return result;
    }

    /// <summary>
    /// Converts the builder state to a wire-format QueryDescriptor for registration.
    /// </summary>
    internal QueryDescriptor ToDescriptor()
    {
        return new QueryDescriptor
        {
            RequiredTypes = _required.Select(a => a.TypeName).ToArray(),
            OptionalTypes = _optional.Select(a => a.TypeName).ToArray(),
            ExcludedTypes = _excluded.ToArray(),
            ReadTypes = _required.Where(a => !a.IsReadWrite).Select(a => a.TypeName)
                .Concat(_optional.Where(a => !a.IsReadWrite).Select(a => a.TypeName))
                .Distinct().ToArray(),
            WriteTypes = _required.Where(a => a.IsReadWrite).Select(a => a.TypeName)
                .Concat(_optional.Where(a => a.IsReadWrite).Select(a => a.TypeName))
                .Distinct().ToArray(),
            TaggedTypes = _tags.Distinct().ToArray()
        };
    }

    /// <summary>
    /// All component type names this query needs data for (required + optional + excluded).
    /// Used by the runner to know which shard types to pass in.
    /// </summary>
    internal HashSet<string> GetAllTypeNames()
    {
        var set = new HashSet<string>(_allTypes);
        foreach (var e in _excluded)
            set.Add(e);
        return set;
    }

    private void ThrowIfFrozen()
    {
        if (_frozen)
            throw new InvalidOperationException(
                "Cannot modify a query after its system has been added to a world.");
    }
}

/// <summary>
/// A component matched through a tag join, identified by its type name because the
/// concrete type is not known until the tick resolves.
/// </summary>
public readonly record struct TaggedComponent(string TypeName, byte[] Data)
{
    public T As<T>() where T : IComponent => MessagePackSerializer.Deserialize<T>(Data);

    public bool Is<T>() where T : IComponent => TypeName == ComponentTypeId.Of<T>().TypeName;
}
