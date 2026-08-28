namespace Engine.Core;

/// <summary>
/// Marker interface for ECS components. Components are serialized using the
/// contractless MessagePack resolver — no attributes are required.
/// </summary>
/// <remarks>
/// The coordinator stores component data as opaque bytes, so it only needs the marker.
/// The authoring side of this interface lives in the client SDK.
/// </remarks>
public interface IComponent;
