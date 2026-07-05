namespace Engine.Core;

/// <summary>
/// Describes access to a single component type — read-only or read-write.
/// </summary>
public readonly record struct ComponentAccess(string TypeName, bool IsReadWrite);

/// <summary>
/// Static helpers for declaring component access in query builders.
/// </summary>
public static class Query
{
    public static ComponentAccess ReadOnly<T>() where T : IComponent =>
        new(ComponentTypeId.Of<T>().TypeName, IsReadWrite: false);

    public static ComponentAccess ReadWrite<T>() where T : IComponent =>
        new(ComponentTypeId.Of<T>().TypeName, IsReadWrite: true);
}
