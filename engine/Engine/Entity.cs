namespace Engine.Core;

/// <summary>
/// A lightweight entity identifier. Allocated monotonically by the coordinator.
/// </summary>
public readonly record struct Entity(ulong Id);

/// <summary>
/// The target of a command. Either a concrete entity, or the entity representing
/// a component type — which the coordinator resolves by name and creates on first use.
/// </summary>
public readonly record struct CommandTarget(ulong EntityId, string? ComponentType = null)
{
    public static CommandTarget OfComponentType(string typeName) => new(0, typeName);

    public static CommandTarget OfComponentType<T>() where T : IComponent =>
        OfComponentType(ComponentTypeId.Of<T>().TypeName);

    public static implicit operator CommandTarget(Entity entity) => new(entity.Id);
}
