namespace Engine.Core;

/// <summary>
/// Marker interface for ECS components. Components are serialized using the
/// contractless MessagePack resolver — no attributes are required.
/// </summary>
public interface IComponent { }
