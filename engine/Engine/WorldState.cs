using Engine.Core.Messages;

namespace Engine.Coordinator;

/// <summary>
/// Stores all entity and component data. Entities are allocated monotonically.
/// Components are stored as raw byte[] keyed by (entityId, componentType).
/// </summary>
public class WorldState
{
    private ulong _nextEntityId = 1;
    private readonly HashSet<ulong> _alive = new();
    private readonly Dictionary<ulong, Dictionary<string, byte[]>> _components = new();

    public ulong AllocateEntity()
    {
        var id = _nextEntityId++;
        _alive.Add(id);
        _components[id] = new Dictionary<string, byte[]>();
        return id;
    }

    public void DestroyEntity(ulong entityId)
    {
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

    public int EntityCount => _alive.Count;

    public IReadOnlyCollection<ulong> GetAllEntities() => _alive;

    /// <summary>
    /// Returns all alive entity IDs that match ANY of the given query descriptors.
    /// A query matches if the entity has ALL required types, at least one optional type
    /// (if any are specified), and NONE of the excluded types.
    /// </summary>
    public List<ulong> GetEntitiesMatchingQueries(QueryDescriptor[] queries)
    {
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
