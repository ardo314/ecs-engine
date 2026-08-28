namespace Engine.Core;

/// <summary>
/// The target of a command. Either a concrete entity, or the entity representing
/// a component type — which the coordinator resolves by name and creates on first use.
/// </summary>
/// <remarks>
/// The coordinator addresses entities by raw id; the <c>Entity</c> wrapper is a
/// client-side authoring type.
/// </remarks>
public readonly record struct CommandTarget(ulong EntityId, string? ComponentType = null)
{
    public static CommandTarget OfComponentType(string typeName) => new(0, typeName);
}
