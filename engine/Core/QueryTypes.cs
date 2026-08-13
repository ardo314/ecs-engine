namespace Engine.Core;

/// <summary>
/// Describes access to a single component type — read-only or read-write.
/// <see cref="Describe"/> replays the component type's own description commands.
/// </summary>
public readonly record struct ComponentAccess(
    string TypeName,
    bool IsReadWrite,
    Action<EntityCommandBuffer>? Describe = null);

/// <summary>
/// Static helpers for declaring component access in query builders.
/// </summary>
public static class Query
{
    public static ComponentAccess ReadOnly<T>() where T : IComponent =>
        new(ComponentTypeId.Of<T>().TypeName, IsReadWrite: false, Description<T>.Apply);

    public static ComponentAccess ReadWrite<T>() where T : IComponent =>
        new(ComponentTypeId.Of<T>().TypeName, IsReadWrite: true, Description<T>.Apply);
}

internal static class Description<T> where T : IComponent
{
    public static readonly Action<EntityCommandBuffer> Apply = commands =>
    {
        var self = EntityRef.OfComponentType<T>();
        commands.AddComponent(self, new ComponentInfo(ComponentTypeId.Of<T>().TypeName));
        T.Describe(commands, self);
    };
}
