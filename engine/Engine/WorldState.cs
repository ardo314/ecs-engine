using Ecs.Protocol.V1;
using Google.Protobuf;

namespace Engine.Coordinator;

/// <summary>
/// The canonical entity and component store.
/// </summary>
/// <remarks>
/// Components are opaque bytes keyed by (entity, type id). The world never decodes a
/// payload — that is the whole point, and it is why introducing a component type costs
/// nothing here. The only structure it understands is that some entities represent
/// component types, which it tracks so that tag joins and observers have something to
/// hang off.
/// </remarks>
public sealed class WorldState
{
    private readonly SchemaRegistry _schemas;
    private ulong _nextEntityId = 1;
    private readonly HashSet<ulong> _alive = new();
    private readonly Dictionary<ulong, Dictionary<uint, byte[]>> _components = new();
    private readonly Dictionary<uint, ulong> _typeEntities = new();

    public WorldState(SchemaRegistry schemas) => _schemas = schemas;

    public int EntityCount => _alive.Count;

    public IReadOnlyCollection<ulong> AllEntities => _alive;

    public ulong AllocateEntity()
    {
        var id = _nextEntityId++;
        _alive.Add(id);
        _components[id] = new Dictionary<uint, byte[]>();
        return id;
    }

    public void DestroyEntity(ulong entityId)
    {
        foreach (var typeId in _typeEntities.Where(kv => kv.Value == entityId).Select(kv => kv.Key).ToList())
            _typeEntities.Remove(typeId);

        _alive.Remove(entityId);
        _components.Remove(entityId);
    }

    public bool IsAlive(ulong entityId) => _alive.Contains(entityId);

    public void SetComponent(ulong entityId, uint typeId, byte[] data)
    {
        if (!_components.TryGetValue(entityId, out var bag))
        {
            bag = new Dictionary<uint, byte[]>();
            _components[entityId] = bag;
        }
        bag[typeId] = data;
    }

    public void RemoveComponent(ulong entityId, uint typeId)
    {
        if (_components.TryGetValue(entityId, out var bag))
            bag.Remove(typeId);
    }

    public byte[]? GetComponent(ulong entityId, uint typeId) =>
        _components.TryGetValue(entityId, out var bag) && bag.TryGetValue(typeId, out var data)
            ? data
            : null;

    public IReadOnlyDictionary<uint, byte[]> ComponentsOf(ulong entityId) =>
        _components.TryGetValue(entityId, out var bag) ? bag : EmptyBag;

    public bool Has(ulong entityId, uint typeId) =>
        _components.TryGetValue(entityId, out var bag) && bag.ContainsKey(typeId);

    private static readonly Dictionary<uint, byte[]> EmptyBag = new();

    // ── Type entities ───────────────────────────────────────────

    /// <summary>
    /// The entity representing a component type, created on first use. It carries the
    /// type's <c>ComponentInfo</c> and <c>ComponentSchema</c>; anything else attached to
    /// it is an ordinary user-defined component, which is how a type describes itself
    /// without the engine needing to know what the description means.
    /// </summary>
    public ulong GetOrCreateTypeEntity(RegisteredType type)
    {
        if (_typeEntities.TryGetValue(type.TypeId, out var existing))
            return existing;

        var id = AllocateEntity();
        _typeEntities[type.TypeId] = id;

        SetComponent(id, _schemas.ComponentInfoTypeId,
            new Ecs.V1.ComponentInfo { TypeName = type.LogicalName }.ToByteArray());

        SetComponent(id, _schemas.ComponentSchemaTypeId,
            new Ecs.V1.ComponentSchema
            {
                FileDescriptorSet = type.FileDescriptorSet,
                SchemaHash = type.SchemaHash,
                TypeId = type.TypeId,
            }.ToByteArray());

        return id;
    }

    public ulong? FindTypeEntity(uint typeId) =>
        _typeEntities.TryGetValue(typeId, out var id) ? id : null;

    /// <summary>
    /// The component type ids whose type entity carries <paramref name="tagTypeId"/>.
    /// Resolved fresh each tick, so a type registered later is picked up without any
    /// system changing its query.
    /// </summary>
    public List<uint> TypesTaggedWith(uint tagTypeId)
    {
        var result = new List<uint>();
        foreach (var (typeId, entityId) in _typeEntities)
        {
            if (Has(entityId, tagTypeId))
                result.Add(typeId);
        }
        return result;
    }

    public Dictionary<uint, uint[]> ResolveTags(IEnumerable<uint> tagTypeIds)
    {
        var result = new Dictionary<uint, uint[]>();
        foreach (var tag in tagTypeIds)
        {
            if (!result.ContainsKey(tag))
                result[tag] = [.. TypesTaggedWith(tag)];
        }
        return result;
    }

    // ── Query matching ──────────────────────────────────────────

    /// <summary>
    /// Every alive entity matching any of <paramref name="queries"/>. A query matches an
    /// entity that has all required types, at least one optional type when any are
    /// given, at least one component per tag, and none of the excluded types.
    /// </summary>
    public List<ulong> MatchQueries(
        IReadOnlyList<QueryDescriptor> queries,
        IReadOnlyDictionary<uint, uint[]> tagResolution)
    {
        var result = new HashSet<ulong>();

        foreach (var query in queries)
        {
            foreach (var entityId in _alive)
            {
                if (_components.TryGetValue(entityId, out var bag) &&
                    Matches(query, bag, tagResolution))
                {
                    result.Add(entityId);
                }
            }
        }

        return [.. result];
    }

    private static bool Matches(
        QueryDescriptor query,
        Dictionary<uint, byte[]> bag,
        IReadOnlyDictionary<uint, uint[]> tagResolution)
    {
        foreach (var access in query.Required)
        {
            if (!bag.ContainsKey(access.TypeId)) return false;
        }

        if (query.Optional.Count > 0 && !query.Optional.Any(a => bag.ContainsKey(a.TypeId)))
            return false;

        foreach (var tagged in query.Tagged)
        {
            if (!tagResolution.TryGetValue(tagged.TagTypeId, out var types)) return false;
            if (!types.Any(bag.ContainsKey)) return false;
        }

        foreach (var excluded in query.Excluded)
        {
            if (bag.ContainsKey(excluded)) return false;
        }

        return true;
    }

    /// <summary>
    /// Entities matching a name-based filter. Observers express interest in logical
    /// names because they may not have seen the schema registry yet.
    /// </summary>
    public List<ulong> Filter(EntityFilter? filter)
    {
        if (filter is null || (filter.AllOf.Count == 0 && filter.AnyOf.Count == 0))
            return [.. _alive];

        var allOf = _schemas.ResolveIds(filter.AllOf);
        var anyOf = _schemas.ResolveIds(filter.AnyOf);

        // A required name that resolves to nothing cannot be satisfied by anything.
        if (allOf.Count != filter.AllOf.Distinct(StringComparer.Ordinal).Count()) return [];

        var result = new List<ulong>();
        foreach (var entityId in _alive)
        {
            if (!_components.TryGetValue(entityId, out var bag)) continue;
            if (!allOf.All(bag.ContainsKey)) continue;
            if (anyOf.Count > 0 && !anyOf.Any(bag.ContainsKey)) continue;
            result.Add(entityId);
        }
        return result;
    }
}
