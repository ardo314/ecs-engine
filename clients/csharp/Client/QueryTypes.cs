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
        new(ComponentTypeId.Of<T>().TypeName, IsReadWrite: false, Description<T>.Use);

    public static ComponentAccess ReadWrite<T>() where T : IComponent =>
        new(ComponentTypeId.Of<T>().TypeName, IsReadWrite: true, Description<T>.Use);
}

internal static class Description<T> where T : IComponent
{
    /// <summary>Emits the type's own description. Use <see cref="Use"/> to avoid repeats.</summary>
    public static readonly Action<EntityCommandBuffer> Apply = commands =>
    {
        var self = CommandTarget.OfComponentType<T>();
        commands.AddComponent(self, new ComponentInfo(ComponentTypeId.Of<T>().TypeName));
        T.Describe(commands, self);
    };

    public static readonly Action<EntityCommandBuffer> Use = commands => commands.Describe<T>();
}
