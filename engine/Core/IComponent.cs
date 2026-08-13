namespace Engine.Core;

/// <summary>
/// Marker interface for ECS components. Components are serialized using the
/// contractless MessagePack resolver — no attributes are required.
/// </summary>
public interface IComponent
{
    /// <summary>
    /// Optional. Buffers commands against <paramref name="self"/> — the entity representing
    /// this component type — to attach components describing the type. Called once by the
    /// SDK for every component type a system uses.
    /// </summary>
    static virtual void Describe(EntityCommandBuffer commands, EntityRef self) { }
}
