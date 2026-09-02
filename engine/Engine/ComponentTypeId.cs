using Ecs.V1;
using Google.Protobuf;

namespace Engine.Core;

/// <summary>
/// A unique identifier for a component type — its protobuf full name, e.g. "nova.v1.CellRef".
/// </summary>
public readonly record struct ComponentTypeId(string TypeName)
{
    public static ComponentTypeId Of<T>() where T : IMessage<T>, new() => new(new T().Descriptor.FullName);
}

/// <summary>
/// Type names of the components the coordinator itself reads. Every other component
/// is opaque bytes to it.
/// </summary>
public static class ComponentTypes
{
    /// <summary>Identifies an entity as the representation of a component type.</summary>
    public static string Info { get; } = ComponentTypeId.Of<ComponentInfo>().TypeName;
}
