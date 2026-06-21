namespace Engine.Core;

/// <summary>
/// A lightweight entity identifier. Allocated monotonically by the coordinator.
/// </summary>
public readonly record struct Entity(ulong Id);
