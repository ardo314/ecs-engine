using Google.Protobuf;

namespace Engine.Core;

/// <summary>
/// A unique identifier for a component type — its protobuf full name, e.g. "nova.v1.CellRef".
/// </summary>
public readonly record struct ComponentTypeId(string TypeName)
{
    public static ComponentTypeId Of<T>() where T : IMessage<T>, new() => new(ProtoType<T>.FullName);
}
