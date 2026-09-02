using Google.Protobuf;

namespace Engine.Core;

/// <summary>
/// A lightweight entity identifier. Allocated monotonically by the coordinator.
/// </summary>
/// <remarks>
/// A struct so query iteration stays allocation-free; converts implicitly to and from
/// the <c>ecs.v1.Entity</c> message that reference components hold on the wire.
/// </remarks>
public readonly record struct Entity(ulong Id)
{
    public static implicit operator Ecs.V1.EntityId(Entity entity) => new() { Id = entity.Id };

    // Message fields are always presence-tracked in proto3, so an unset reference reads as entity 0.
    public static implicit operator Entity(Ecs.V1.EntityId? entity) => new(entity?.Id ?? 0);
}

/// <summary>
/// The target of a command. Either a concrete entity, or the entity representing
/// a component type — which the coordinator resolves by name and creates on first use.
/// </summary>
public readonly record struct CommandTarget(ulong EntityId, string? ComponentType = null)
{
    public static CommandTarget OfComponentType(string typeName) => new(0, typeName);

    public static CommandTarget OfComponentType<T>() where T : IMessage<T>, new() =>
        OfComponentType(ComponentTypeId.Of<T>().TypeName);

    public static implicit operator CommandTarget(Entity entity) => new(entity.Id);
}
