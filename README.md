# ECS Engine

A distributed Entity Component System engine built with C# (.NET 9) and NATS.

## Architecture

See [ARCHITECTURE.md](ARCHITECTURE.md) for the full design.

- **Engine** — Coordinator that owns world state, schedules systems, and brokers data via NATS.
- **Client** — C# SDK (`clients/csharp/`) for writing system processes.
- **Protos** — Component schemas in Protobuf (`proto/`), managed with `buf` and
  generated into C# and TypeScript.
- **Editor** — One Node process serving a React web app and its API, for
  real-time entity/component inspection.

## Prerequisites

- [Docker](https://www.docker.com/) (for the dev container)
- [VS Code](https://code.visualstudio.com/) with the Dev Containers extension

All tooling (.NET 9 SDK, Node.js 22, buf, NATS server) is provided by the dev container.

## Getting Started

1. Open the repository in VS Code.
2. Reopen in the dev container when prompted (or use `Dev Containers: Reopen in Container`).
3. Build:

```bash
npm install
dotnet build engine/Engine.sln
dotnet build clients/csharp/CSharp.sln
dotnet build examples/Examples.sln
npm run build
```

4. Run the coordinator:

```bash
dotnet run --project engine/Engine
```

5. Run the tests:

```bash
dotnet test engine/Engine.sln
dotnet test clients/csharp/CSharp.sln
npm test
```

Alternatively, bring the whole stack up in containers — NATS, coordinator, an
example system, and the editor on <http://localhost:8080>:

```bash
docker compose up --build
```

## Defining components

Components are Protobuf messages, not hand-written classes. Add a message under
`proto/<domain>/v1/`, then regenerate:

```bash
npm run proto:generate
```

This writes C# into `proto/gen/csharp/` and TypeScript into `proto/gen/ts/`, both
of which are committed. `npm run proto:lint` and `npm run proto:format` keep the
schemas tidy; CI enforces both, plus backwards compatibility against `main`.

Each component type publishes its own descriptors into the world, so the editor
renders component types it was never built against.

## Project Structure

```
proto/               — Protobuf component schemas and generated code
engine/              — Coordinator console app and its tests
clients/csharp/      — C# system-authoring SDK (Client) and its tests
editor/              — React UI + Hono API + NATS bridge, one Node process
examples/            — Example systems
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
