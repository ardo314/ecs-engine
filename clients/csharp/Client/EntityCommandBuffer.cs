using Ecs.V1;
using Engine.Core.Messages;
using Google.Protobuf;

namespace Engine.Core;

/// <summary>
/// Buffers structural changes (create, destroy, add/remove component) to be
/// played onto the world by the coordinator. Commands target an <see cref="CommandTarget"/>,
/// so the same API addresses both entities and component type entities.
/// </summary>
public class EntityCommandBuffer
{
    private readonly List<EntitySpawnRequest> _spawns = new();
    private readonly List<ulong> _destroys = new();
    private readonly List<ComponentAddRequest> _adds = new();
    private readonly List<ComponentRemoveRequest> _removes = new();

    // Types already described to the coordinator; survives Clear() for the buffer's lifetime.
    private readonly HashSet<string> _described = new();
    private readonly HashSet<string> _announced = new();

    public IReadOnlyList<EntitySpawnRequest> Spawns => _spawns;
    public IReadOnlyList<ulong> Destroys => _destroys;
    public IReadOnlyList<ComponentAddRequest> Adds => _adds;
    public IReadOnlyList<ComponentRemoveRequest> Removes => _removes;

    /// <summary>
    /// Buffers a request to create a new entity with the given components.
    /// </summary>
    public void CreateEntity(params IMessage[] components)
    {
        var types = new string[components.Length];
        var data = new byte[components.Length][];
        for (var i = 0; i < components.Length; i++)
        {
            types[i] = components[i].Descriptor.FullName;
            data[i] = components[i].ToByteArray();
            Announce(types[i]);
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
    public void AddComponent<T>(CommandTarget target, T component) where T : IMessage<T>, new()
    {
        Describe<T>();
        _adds.Add(new ComponentAddRequest
        {
            Target = target,
            ComponentType = ComponentTypeId.Of<T>().TypeName,
            Data = component.ToByteArray()
        });
    }

    /// <summary>
    /// Buffers a request to add a component known only by name — the form a component
    /// type's own <c>ecs.v1.description</c> attachments arrive in.
    /// </summary>
    internal void AddComponentRaw(CommandTarget target, string componentType, byte[] data)
    {
        Announce(componentType);
        _adds.Add(new ComponentAddRequest
        {
            Target = target,
            ComponentType = componentType,
            Data = data
        });
    }

    /// <summary>
    /// Buffers a request to remove a component from the target.
    /// </summary>
    public void RemoveComponent<T>(CommandTarget target) where T : IMessage<T>, new()
    {
        Describe<T>();
        _removes.Add(new ComponentRemoveRequest
        {
            Target = target,
            ComponentType = ComponentTypeId.Of<T>().TypeName
        });
    }

    /// <summary>
    /// Buffers the component type's own description, once per buffer.
    /// </summary>
    internal void Describe<T>() where T : IMessage<T>, new()
    {
        if (_described.Add(ComponentTypeId.Of<T>().TypeName))
            Description<T>.Apply(this);
    }

    /// <summary>
    /// Buffers a bare type entity for a component only known by name, once per buffer.
    /// </summary>
    private void Announce(string typeName)
    {
        if (!_announced.Add(typeName)) return;

        _adds.Add(new ComponentAddRequest
        {
            Target = CommandTarget.OfComponentType(typeName),
            ComponentType = ComponentTypes.Info,
            Data = new ComponentInfo { TypeName = typeName }.ToByteArray()
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
