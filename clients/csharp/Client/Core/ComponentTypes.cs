namespace Engine.Core;

/// <summary>
/// Identifies an entity as the representation of a component type. Component types
/// are ordinary entities, so the type system is queryable through the same endpoints
/// as any other world data.
/// </summary>
public readonly record struct ComponentInfo(string TypeName) : IComponent
{
    /// <summary>The component type name this component is stored under.</summary>
    public static string Type { get; } = ComponentTypeId.Of<ComponentInfo>().TypeName;
}
