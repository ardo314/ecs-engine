using Engine.Core;

namespace Nova.Components;

/// <summary>
/// Identifies which Nova cell and controller an entity belongs to.
/// Maps to the Nova API path: /cells/{cell}/controllers/{controller}
/// </summary>
/// <remarks>
/// Deliberately denormalised onto every IO entity so systems can build Nova requests
/// without dereferencing <see cref="ControllerRef"/>.
/// </remarks>
public record struct NovaControllerId(string Cell, string Controller) : IComponent;

/// <summary>
/// References the cell entity this entity belongs to.
/// </summary>
public record struct CellRef(Entity Cell) : IComponent;

/// <summary>
/// References the controller entity this entity belongs to.
/// </summary>
public record struct ControllerRef(Entity Controller) : IComponent;

/// <summary>
/// Represents a desired digital (boolean) output value to set on a controller.
/// Maps to the Nova API IOBooleanValue schema.
/// </summary>
public record struct DigitalOutputRequest(string Io, bool Value) : IComponent;

/// <summary>
/// Represents a desired analog integer output value to set on a controller.
/// Maps to the Nova API IOIntegerValue schema.
/// </summary>
public record struct AnalogIntOutputRequest(string Io, long Value) : IComponent;

/// <summary>
/// Represents a desired analog float output value to set on a controller.
/// Maps to the Nova API IOFloatValue schema.
/// </summary>
public record struct AnalogFloatOutputRequest(string Io, double Value) : IComponent;

/// <summary>
/// Stores the last confirmed state of an IO after a successful set operation.
/// Attached to the entity after the system confirms the write.
/// </summary>
public record struct IoOutputState(string Io, string ValueType, string Value, bool Confirmed) : IComponent;
