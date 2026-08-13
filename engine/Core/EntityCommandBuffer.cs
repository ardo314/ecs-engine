using Engine.Core.Messages;
using MessagePack;

namespace Engine.Core;

/// <summary>
/// Buffers structural changes (create, destroy, add/remove component) to be
/// played onto the world by the coordinator. Commands target an <see cref="EntityRef"/>,
/// so the same API addresses both entities and component type entities.
/// </summary>
public class EntityCommandBuffer
{
    private readonly List<EntitySpawnRequest> _spawns = new();
    private readonly List<ulong> _destroys = new();
    private readonly List<ComponentAddRequest> _adds = new();
    private readonly List<ComponentRemoveRequest> _removes = new();

    public IReadOnlyList<EntitySpawnRequest> Spawns => _spawns;
    public IReadOnlyList<ulong> Destroys => _destroys;
    public IReadOnlyList<ComponentAddRequest> Adds => _adds;
    public IReadOnlyList<ComponentRemoveRequest> Removes => _removes;

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
        _spawns.Add(new EntitySpawnRequest { ComponentTypes = types, ComponentData = data });
    }

    /// <summary>
    /// Buffers a request to destroy an entity.
    /// </summary>
    public void DestroyEntity(Entity entity)
    {
        _destroys.Add(entity.Id);
    }

    /// <summary>
    /// Buffers a request to add a component to the target.
    /// </summary>
    public void AddComponent<T>(EntityRef target, T component) where T : IComponent
    {
        _adds.Add(new ComponentAddRequest
        {
            Target = target,
            ComponentType = ComponentTypeId.Of<T>().TypeName,
            Data = MessagePackSerializer.Serialize(component)
        });
    }

    /// <summary>
    /// Buffers a request to remove a component from the target.
    /// </summary>
    public void RemoveComponent<T>(EntityRef target) where T : IComponent
    {
        _removes.Add(new ComponentRemoveRequest
        {
            Target = target,
            ComponentType = ComponentTypeId.Of<T>().TypeName
        });
    }

    public bool HasPendingCommands =>
        _spawns.Count > 0 || _destroys.Count > 0 || _adds.Count > 0 || _removes.Count > 0;

    public void Clear()
    {
        _spawns.Clear();
        _destroys.Clear();
        _adds.Clear();
        _removes.Clear();
    }
}
