namespace Engine.Core;

/// <summary>
/// References this entity's parent, forming the generic entity hierarchy.
/// </summary>
public readonly record struct ParentRef(Entity Parent) : IComponent;
