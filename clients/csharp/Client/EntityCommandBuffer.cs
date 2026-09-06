using Ecs.Protocol.V1;
using Google.Protobuf;

namespace Engine.Core;

/// <summary>
/// Buffers structural changes for the coordinator to apply at its next synchronisation
/// point.
/// </summary>
/// <remarks>
/// Systems never mutate the world's topology while iterating it. Spawns, despawns and
/// component add/remove go in here and are returned with the tick's result; the world
/// plays them all at one deterministic point. That is what keeps archetype migration,
/// iteration stability and distributed synchronisation from becoming a problem.
///
/// The buffer also collects the schemas it touches, so a type introduced by a command
/// is registered before the command that needs it is sent.
/// </remarks>
public sealed class EntityCommandBuffer
{
    private readonly List<StructuralCommand> _commands = new();
    private readonly Dictionary<string, ComponentTypeDeclaration> _schemas = new(StringComparer.Ordinal);

    public IReadOnlyList<StructuralCommand> Commands => _commands;

    /// <summary>Schemas referenced by the buffered commands, for the registration handshake.</summary>
    public IReadOnlyCollection<ComponentTypeDeclaration> Schemas => _schemas.Values;

    public bool HasPendingCommands => _commands.Count > 0;

    /// <summary>Buffers creation of a new entity carrying the given components.</summary>
    public void CreateEntity(params IMessage[] components)
    {
        var spawn = new SpawnEntity();
        foreach (var component in components)
        {
            var declaration = DeclarationOf(component.Descriptor);
            Declare(declaration);
            spawn.Components.Add(new ComponentValue
            {
                Type = declaration.Type,
                Payload = component.ToByteString(),
            });
        }

        _commands.Add(new StructuralCommand { Spawn = spawn });
    }

    public void DestroyEntity(Entity entity) =>
        _commands.Add(new StructuralCommand { Despawn = new DespawnEntity { Entity = entity.Id } });

    public void AddComponent<T>(CommandTarget target, T component) where T : IMessage<T>, new()
    {
        Declare(ComponentType<T>.Declaration);
        _commands.Add(new StructuralCommand
        {
            Add = new AddComponent
            {
                Target = target,
                Component = ComponentType<T>.Value(component),
            },
        });
    }

    public void RemoveComponent<T>(CommandTarget target) where T : IMessage<T>, new()
    {
        Declare(ComponentType<T>.Declaration);
        _commands.Add(new StructuralCommand
        {
            Remove = new RemoveComponent { Target = target, Type = ComponentType<T>.Ref },
        });
    }

    public void AddComponent<T>(Entity entity, T component) where T : IMessage<T>, new() =>
        AddComponent(Target.Of(entity), component);

    public void RemoveComponent<T>(Entity entity) where T : IMessage<T>, new() =>
        RemoveComponent<T>(Target.Of(entity));

    /// <summary>
    /// Records a type's schema without buffering any command, so a type used only in a
    /// query still reaches the coordinator's registry.
    /// </summary>
    public void Declare(ComponentTypeDeclaration declaration) =>
        _schemas.TryAdd(declaration.Type.LogicalName, declaration);

    public void Declare<T>() where T : IMessage<T>, new() => Declare(ComponentType<T>.Declaration);

    public void Clear() => _commands.Clear();

    /// <summary>Takes the buffered commands, leaving the buffer empty.</summary>
    internal List<StructuralCommand> Drain()
    {
        var drained = new List<StructuralCommand>(_commands);
        _commands.Clear();
        return drained;
    }

    private static ComponentTypeDeclaration DeclarationOf(Google.Protobuf.Reflection.MessageDescriptor descriptor)
    {
        // CreateEntity takes IMessage rather than a generic parameter, so the cached
        // per-type declaration is reached reflectively, once per type.
        return DeclarationCache.GetOrAdd(descriptor);
    }
}

/// <summary>
/// Caches declarations for component types only known through <see cref="IMessage"/>.
/// </summary>
internal static class DeclarationCache
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ComponentTypeDeclaration> Cache = new();

    public static ComponentTypeDeclaration GetOrAdd(Google.Protobuf.Reflection.MessageDescriptor descriptor) =>
        Cache.GetOrAdd(descriptor.FullName, _ =>
        {
            var declaration = new ComponentTypeDeclaration
            {
                Type = new ComponentTypeRef
                {
                    LogicalName = descriptor.FullName,
                    SchemaHash = Engine.Core.SchemaHash.Of(descriptor),
                },
                FileDescriptorSet = Descriptors.FileDescriptorSetFor(descriptor),
            };
            declaration.Description.AddRange(ComponentDescription.Of(descriptor));
            return declaration;
        });
}
