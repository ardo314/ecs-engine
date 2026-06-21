using Engine.Core;
using Engine.Core.Messages;
using MessagePack;

namespace Client;

/// <summary>
/// Provides typed access to component data received from the coordinator,
/// and buffers mutations to publish back.
/// </summary>
public class ComponentBuffer
{
    private readonly Dictionary<string, (ulong[] Entities, byte[][] Data)> _received = new();
    private readonly Dictionary<string, Dictionary<ulong, byte[]>> _mutations = new();
    private ulong _tickId;

    public ulong TickId => _tickId;

    internal void Clear()
    {
        _received.Clear();
        _mutations.Clear();
        _tickId = 0;
    }

    internal void SetTickId(ulong tickId) => _tickId = tickId;

    internal void AddShard(ComponentShard shard)
    {
        _tickId = shard.TickId;
        var entityData = MessagePackSerializer.Deserialize<byte[][]>(shard.Data);
        _received[shard.ComponentType] = (shard.Entities, entityData);
    }

    /// <summary>
    /// Gets all entities and their deserialized component data for the given type.
    /// </summary>
    public (Entity Entity, T Component)[] GetComponents<T>() where T : IComponent
    {
        var typeName = ComponentTypeId.Of<T>().TypeName;
        if (!_received.TryGetValue(typeName, out var pair))
            return [];

        var (entities, data) = pair;
        var result = new (Entity, T)[entities.Length];
        for (var i = 0; i < entities.Length; i++)
        {
            result[i] = (new Entity(entities[i]), MessagePackSerializer.Deserialize<T>(data[i]));
        }
        return result;
    }

    /// <summary>
    /// Gets raw deserialized component data by type name string.
    /// </summary>
    public (ulong[] Entities, byte[][] Data)? GetRaw(string componentType)
    {
        return _received.TryGetValue(componentType, out var pair) ? pair : null;
    }

    /// <summary>
    /// Queues a component mutation for the given entity.
    /// </summary>
    public void SetComponent<T>(Entity entity, T component) where T : IComponent
    {
        var typeName = ComponentTypeId.Of<T>().TypeName;
        if (!_mutations.TryGetValue(typeName, out var dict))
        {
            dict = new Dictionary<ulong, byte[]>();
            _mutations[typeName] = dict;
        }
        dict[entity.Id] = MessagePackSerializer.Serialize(component);
    }

    /// <summary>
    /// Returns all pending mutations grouped by component type, then clears the mutation buffer.
    /// </summary>
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
}
