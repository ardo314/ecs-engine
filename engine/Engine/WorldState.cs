using Engine.Core;
using Engine.Core.Messages;
using MessagePack;

namespace Engine.Coordinator;

/// <summary>
/// Stores all entity and component data. Entities are allocated monotonically.
/// Components are stored as raw byte[] keyed by (entityId, componentType).
/// Component types are themselves entities, carrying their description as components.
/// </summary>
public class WorldState
{
    private ulong _nextEntityId = 1;
    private readonly HashSet<ulong> _alive = new();
    private readonly Dictionary<ulong, Dictionary<string, byte[]>> _components = new();
    private readonly Dictionary<string, ulong> _typeEntities = new();

    public ulong AllocateEntity()
    {
        var id = _nextEntityId++;
        _alive.Add(id);
        _components[id] = new Dictionary<string, byte[]>();
        return id;
    }

    public void DestroyEntity(ulong entityId)
    {
        if (_components.TryGetValue(entityId, out var bag) &&
            bag.TryGetValue(ComponentInfo.Type, out var info))
        {
            var typeName = MessagePackSerializer
                .Deserialize<ComponentInfo>(info, Serialization.Options).TypeName;
            _typeEntities.Remove(typeName);
        }

        _alive.Remove(entityId);
        _components.Remove(entityId);
    }

    public bool IsAlive(ulong entityId) => _alive.Contains(entityId);

    public void SetComponent(ulong entityId, string componentType, byte[] data)
    {
        if (!_components.TryGetValue(entityId, out var bag))
        {
            bag = new Dictionary<string, byte[]>();
            _components[entityId] = bag;
        }
        bag[componentType] = data;
    }

    public void RemoveComponent(ulong entityId, string componentType)
    {
        if (_components.TryGetValue(entityId, out var bag))
        {
            bag.Remove(componentType);
        }
    }

    public byte[]? GetComponent(ulong entityId, string componentType)
    {
        if (_components.TryGetValue(entityId, out var bag) &&
            bag.TryGetValue(componentType, out var data))
        {
            return data;
        }
        return null;
    }

    public IReadOnlySet<string> GetComponentTypes(ulong entityId)
    {
        if (_components.TryGetValue(entityId, out var bag))
        {
            return bag.Keys.ToHashSet();
        }
        return new HashSet<string>();
    }

    /// <summary>
    /// Returns all alive entity IDs that have ALL of the specified component types.
    /// </summary>
    public List<ulong> GetEntitiesWith(IReadOnlyList<string> componentTypes)
    {
        var result = new List<ulong>();
        foreach (var entityId in _alive)
        {
            if (!_components.TryGetValue(entityId, out var bag))
                continue;

            var match = true;
            foreach (var type in componentTypes)
            {
                if (!bag.ContainsKey(type))
                {
                    match = false;
                    break;
                }
            }

            if (match)
                result.Add(entityId);
        }
        return result;
    }

    /// <summary>
    /// Returns all alive entity IDs that have ANY of the specified component types.
    /// </summary>
    public List<ulong> GetEntitiesWithAny(IReadOnlyList<string> componentTypes)
    {
        var result = new List<ulong>();
        foreach (var entityId in _alive)
        {
            if (!_components.TryGetValue(entityId, out var bag))
                continue;

            foreach (var type in componentTypes)
            {
                if (bag.ContainsKey(type))
                {
                    result.Add(entityId);
                    break;
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Returns the entity representing a component type, creating it on first use.
    /// </summary>
    public ulong GetOrCreateTypeEntity(string typeName)
    {
        if (_typeEntities.TryGetValue(typeName, out var existing))
            return existing;

        var id = AllocateEntity();
        _typeEntities[typeName] = id;
        SetComponent(id, ComponentInfo.Type, MessagePackSerializer.Serialize(
            new ComponentInfo(typeName), Serialization.Options));
        return id;
    }

    public ulong? FindTypeEntity(string typeName) =>
        _typeEntities.TryGetValue(typeName, out var id) ? id : null;

    /// <summary>
    /// Returns the component type names whose type entity carries <paramref name="tagType"/>.
    /// </summary>
    public List<string> GetTypesTaggedWith(string tagType)
    {
        var result = new List<string>();
        foreach (var (typeName, entityId) in _typeEntities)
        {
            if (_components.TryGetValue(entityId, out var bag) && bag.ContainsKey(tagType))
                result.Add(typeName);
        }
        return result;
    }

    /// <summary>
    /// Resolves each tag type name to the component type names currently carrying it.
    /// </summary>
    public Dictionary<string, string[]> ResolveTaggedTypes(IEnumerable<string> tagTypes)
    {
        var result = new Dictionary<string, string[]>();
        foreach (var tag in tagTypes)
        {
            if (!result.ContainsKey(tag))
                result[tag] = GetTypesTaggedWith(tag).ToArray();
        }
        return result;
    }

    public int EntityCount => _alive.Count;

    public IReadOnlyCollection<ulong> GetAllEntities() => _alive;

    /// <summary>
    /// Returns all alive entity IDs that match ANY of the given query descriptors.
    /// A query matches if the entity has ALL required types, at least one optional type
    /// (if any are specified), at least one component per tag type, and NONE of the
    /// excluded types. <paramref name="taggedResolution"/> maps tag type names to the
    /// component type names carrying them; it is resolved from the world when omitted.
    /// </summary>
    public List<ulong> GetEntitiesMatchingQueries(
        QueryDescriptor[] queries,
        IReadOnlyDictionary<string, string[]>? taggedResolution = null)
    {
        taggedResolution ??= ResolveTaggedTypes(queries.SelectMany(q => q.TaggedTypes));

        var result = new HashSet<ulong>();
        foreach (var query in queries)
        {
            foreach (var entityId in _alive)
            {
                if (!_components.TryGetValue(entityId, out var bag))
                    continue;

                // Must have ALL required types
                var match = true;
                foreach (var type in query.RequiredTypes)
                {
                    if (!bag.ContainsKey(type))
                    {
                        match = false;
                        break;
                    }
                }
                if (!match) continue;

                // Must have at least one optional type (if any specified)
                if (query.OptionalTypes.Length > 0)
                {
                    var hasAny = false;
                    foreach (var type in query.OptionalTypes)
                    {
                        if (bag.ContainsKey(type))
                        {
                            hasAny = true;
                            break;
                        }
                    }
                    if (!hasAny) continue;
                }

                // Must have at least one component carrying each tag
                var taggedOk = true;
                foreach (var tag in query.TaggedTypes)
                {
                    var hasTagged = false;
                    if (taggedResolution.TryGetValue(tag, out var taggedTypes))
                    {
                        foreach (var type in taggedTypes)
                        {
                            if (bag.ContainsKey(type))
                            {
                                hasTagged = true;
                                break;
                            }
                        }
                    }
                    if (!hasTagged)
                    {
                        taggedOk = false;
                        break;
                    }
                }
                if (!taggedOk) continue;

                // Must have NONE of the excluded types
                var excluded = false;
                foreach (var type in query.ExcludedTypes)
                {
                    if (bag.ContainsKey(type))
                    {
                        excluded = true;
                        break;
                    }
                }
                if (excluded) continue;

                result.Add(entityId);
            }
        }
        return result.ToList();
    }

    public IReadOnlyDictionary<string, byte[]>? GetAllComponents(ulong entityId)
    {
        if (_components.TryGetValue(entityId, out var bag))
            return bag;
        return null;
    }
}
