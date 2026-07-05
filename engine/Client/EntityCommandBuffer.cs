using Engine.Core;
using Engine.Core.Messages;
using MessagePack;

namespace Client;

/// <summary>
/// Buffers structural entity changes (create, destroy, add/remove component)
/// to be published to the coordinator at the end of each tick.
/// </summary>
public class EntityCommandBuffer
{
    internal readonly List<EntitySpawnRequest> Spawns = new();
    internal readonly List<ulong> Destroys = new();
    internal readonly List<ComponentAddRequest> Adds = new();
    internal readonly List<ComponentRemoveRequest> Removes = new();

    /// <summary>
    /// Buffers a request to create a new entity with the given components.
    /// </summary>
    public void CreateEntity(params IComponent[] components)
    {
        var types = new string[components.Length];
        var data = new byte[components.Length][];
        for (var i = 0; i < components.Length; i++)
        {
            var type = components[i].GetType();
            types[i] = type.FullName ?? type.Name;
            data[i] = MessagePackSerializer.Serialize(type, components[i]);
        }
        Spawns.Add(new EntitySpawnRequest { ComponentTypes = types, ComponentData = data });
    }

    /// <summary>
    /// Buffers a request to destroy an entity.
    /// </summary>
    public void DestroyEntity(Entity entity)
    {
        Destroys.Add(entity.Id);
    }

    /// <summary>
    /// Buffers a request to add a component to an existing entity.
    /// </summary>
    public void AddComponent<T>(Entity entity, T component) where T : IComponent
    {
        Adds.Add(new ComponentAddRequest
        {
            EntityId = entity.Id,
            ComponentType = ComponentTypeId.Of<T>().TypeName,
            Data = MessagePackSerializer.Serialize(component)
        });
    }

    /// <summary>
    /// Buffers a request to remove a component from an existing entity.
    /// </summary>
    public void RemoveComponent<T>(Entity entity) where T : IComponent
    {
        Removes.Add(new ComponentRemoveRequest
        {
            EntityId = entity.Id,
            ComponentType = ComponentTypeId.Of<T>().TypeName
        });
    }

    internal bool HasPendingCommands =>
        Spawns.Count > 0 || Destroys.Count > 0 || Adds.Count > 0 || Removes.Count > 0;

    internal void Clear()
    {
        Spawns.Clear();
        Destroys.Clear();
        Adds.Clear();
        Removes.Clear();
    }
}
