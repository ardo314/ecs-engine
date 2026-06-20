# ECS Engine

A distributed Entity Component System engine built with C# (.NET 9) and NATS.

## Architecture

See [ARCHITECTURE.md](ARCHITECTURE.md) for the full design.

- **Engine** — Coordinator that owns world state, schedules systems, and brokers data via NATS.
- **EngineClient** — C# SDK for writing system processes that connect to the coordinator.
- **Editor** — React web app + ASP.NET Core backend for real-time entity/component inspection.

## Prerequisites

- [Docker](https://www.docker.com/) (for the dev container)
- [VS Code](https://code.visualstudio.com/) with the Dev Containers extension

All tooling (.NET 9 SDK, Node.js 22, NATS server) is provided by the dev container.

## Getting Started

1. Open the repository in VS Code.
2. Reopen in the dev container when prompted (or use `Dev Containers: Reopen in Container`).
3. Build:

```bash
dotnet build engine/Engine.sln
dotnet build clients/csharp/EngineClient.sln
dotnet build editor/backend/EditorBackend.sln
cd editor/frontend && npm install && npm run build
```

4. Run the coordinator:

```bash
dotnet run --project engine/Engine
```

## Project Structure

```
engine/              — Coordinator (C# console app)
clients/csharp/      — System-authoring SDK (C# class library)
editor/frontend/     — React + TypeScript + Vite web app
editor/backend/      — ASP.NET Core Minimal API (WebSocket bridge to NATS)
examples/            — Example components and systems
.devcontainer/       — Dev container configuration
```
