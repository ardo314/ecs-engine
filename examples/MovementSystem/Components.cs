using Engine.Core;
using MessagePack;

namespace Examples.Components;

[MessagePackObject]
public record struct Position(
    [property: Key(0)] float X,
    [property: Key(1)] float Y,
    [property: Key(2)] float Z) : IComponent;

[MessagePackObject]
public record struct Velocity(
    [property: Key(0)] float X,
    [property: Key(1)] float Y,
    [property: Key(2)] float Z) : IComponent;
