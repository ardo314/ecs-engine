using Ecs.V1;
using Google.Protobuf;

namespace Engine.Core;

/// <summary>
/// Describes access to a single component type — read-only or read-write.
/// <see cref="Describe"/> replays the component type's own description commands.
/// </summary>
public readonly record struct ComponentAccess(
    string TypeName,
    bool IsReadWrite,
    Action<EntityCommandBuffer>? Describe = null);

/// <summary>
/// Static helpers for declaring component access in query builders.
/// </summary>
public static class Query
{
    public static ComponentAccess ReadOnly<T>() where T : IMessage<T>, new() =>
        new(ComponentTypeId.Of<T>().TypeName, IsReadWrite: false, Description<T>.Use);

    public static ComponentAccess ReadWrite<T>() where T : IMessage<T>, new() =>
        new(ComponentTypeId.Of<T>().TypeName, IsReadWrite: true, Description<T>.Use);
}

internal static class Description<T> where T : IMessage<T>, new()
{
    /// <summary>Emits the type's own description. Use <see cref="Use"/> to avoid repeats.</summary>
    public static readonly Action<EntityCommandBuffer> Apply = commands =>
    {
        var descriptor = ProtoType<T>.Descriptor;
        var self = CommandTarget.OfComponentType(descriptor.FullName);

        commands.AddComponent(self, new ComponentInfo { TypeName = descriptor.FullName });
        commands.AddComponent(self, new ComponentSchema
        {
            FileDescriptorSet = ByteString.CopyFrom(ProtoCodec.FileDescriptorSetFor(descriptor))
        });

        foreach (var (typeName, data) in ProtoCodec.DescriptionOf(descriptor))
            commands.AddComponentRaw(self, typeName, data);
    };

    public static readonly Action<EntityCommandBuffer> Use = commands => commands.Describe<T>();
}
