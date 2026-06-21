using Engine.Core;

namespace Examples.Components;

public record struct Position(float X, float Y, float Z) : IComponent;

public record struct Velocity(float X, float Y, float Z) : IComponent;
