using Ecs.V1;

namespace Engine.Core;

/// <summary>
/// Type names of the components the engine itself knows about. Everything else on a
/// component type entity is ordinary user-defined data.
/// </summary>
public static class ComponentTypes
{
    /// <summary>Identifies an entity as the representation of a component type.</summary>
    public static string Info { get; } = ComponentTypeId.Of<ComponentInfo>().TypeName;

    /// <summary>Carries a component type's own protobuf descriptors.</summary>
    public static string Schema { get; } = ComponentTypeId.Of<ComponentSchema>().TypeName;
}
