using Engine.Core;

namespace Nova.Components;

/// <summary>
/// Identifies which Nova cell and controller an entity belongs to.
/// Maps to the Nova API path: /cells/{cell}/controllers/{controller}
/// </summary>
public record struct ControllerRef(string Cell, string Controller) : IComponent;

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
