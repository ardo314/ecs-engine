# Examples

This directory will contain example components and systems built with the Client SDK.

## Planned Examples

- **BasicComponent** — Define a simple component (e.g., `Position`, `Velocity`)
- **MovementSystem** — A system that reads `Velocity` and writes `Transform` each tick
- **SpawnSystem** — A system that requests entity creation from the coordinator

## Running Examples

Examples are C# console applications that use the `Client` library to connect to the coordinator via NATS.

```bash
# From devcontainer:
cd examples/<example-name>
dotnet run
```

Requires a running NATS server and the engine coordinator.
