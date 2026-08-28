namespace Engine.Core;

/// <summary>
/// A unique identifier for a component type, derived from its fully qualified type name.
/// </summary>
public readonly record struct ComponentTypeId(string TypeName)
{
    public static ComponentTypeId Of<T>() where T : IComponent =>
        new(typeof(T).FullName ?? typeof(T).Name);
}
