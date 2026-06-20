namespace Engine.Core;

/// <summary>
/// Marker interface for ECS components. All components must also be annotated
/// with [MessagePackObject] and have [Key] attributes on their properties.
/// </summary>
public interface IComponent { }
