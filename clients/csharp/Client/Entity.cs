using Ecs.Protocol.V1;
using Google.Protobuf;

namespace Engine.Core;

/// <summary>
/// A lightweight entity identifier. Allocated monotonically by the coordinator.
/// </summary>
/// <remarks>
/// A struct so query iteration stays allocation-free; converts implicitly to and from
/// the <c>ecs.v1.EntityId</c> message that reference components hold on the wire.
/// </remarks>
public readonly record struct Entity(ulong Id)
{
    public static implicit operator Ecs.V1.EntityId(Entity entity) => new() { Id = entity.Id };

    // Message fields are always presence-tracked in proto3, so an unset reference reads as entity 0.
    public static implicit operator Entity(Ecs.V1.EntityId? entity) => new(entity?.Id ?? 0);
}

/// <summary>
/// Builds the target of a structural command: either a concrete entity, or the entity
/// that represents a component type.
/// </summary>
public static class Target
{
    public static CommandTarget Of(Entity entity) => new() { Entity = entity.Id };

    /// <summary>
    /// The entity representing a component type. Addressed by name rather than by id
    /// because a system may describe a type in the same breath as it registers it.
    /// </summary>
    public static CommandTarget OfComponentType(string logicalName) =>
        new() { ComponentType = logicalName };

    public static CommandTarget OfComponentType<T>() where T : IMessage<T>, new() =>
        OfComponentType(ComponentType<T>.Name);
}
