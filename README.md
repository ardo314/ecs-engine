# ECS Engine

A distributed Entity Component System engine built with C# (.NET 9) and NATS.

## Architecture

See [ARCHITECTURE.md](ARCHITECTURE.md) for the full design.

- **Engine** — Coordinator that owns world state, schedules systems, and brokers data via NATS.
- **Client** — C# SDK (`clients/csharp/`) for writing system processes.
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
dotnet build clients/csharp/CSharp.sln
dotnet build editor/backend/EditorBackend.sln
dotnet build examples/Examples.sln
cd editor/frontend && npm install && npm run build
```

4. Run the coordinator:

```bash
dotnet run --project engine/Engine
```

5. Run the tests:

```bash
dotnet test engine/Engine.sln
dotnet test clients/csharp/CSharp.sln
```

Alternatively, bring the whole stack up in containers — NATS, coordinator, an
example system, and the editor on <http://localhost:3000>:

```bash
docker compose up --build
```

## Project Structure

```
engine/              — Coordinator console app and its tests
clients/csharp/      — C# system-authoring SDK (Client) and its tests
editor/frontend/     — React + TypeScript + Vite web app
editor/backend/      — ASP.NET Core Minimal API (WebSocket bridge to NATS)
examples/            — Example components and systems
deployments/nova/    — Installer for Wandelbots NOVA instances
.devcontainer/       — Dev container configuration
```

## Configuration

Every process reads its NATS address from `NATS_URL`, falling back to
`NATS_BROKER` (injected by hosts that supply their own broker, such as NOVA, and
may embed credentials as `nats://user:token@host`) and then to
`nats://localhost:4222`. The coordinator and system processes serve `/health` on
port 8080 for orchestrators to probe.

## Deployment

Locally the stack runs with `docker compose up`. To install it onto a Wandelbots
NOVA instance, see [deployments/nova/README.md](deployments/nova/README.md).
