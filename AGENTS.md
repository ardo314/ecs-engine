# AGENTS.md — Guidelines for AI Coding Agents

This file contains instructions and conventions for AI agents (GitHub Copilot,
Cursor, Cline, etc.) working on this codebase.

---

## Project Overview

This is a **distributed Entity Component System (ECS) engine** written in C#
(.NET 9), with a React-based web editor. See `ARCHITECTURE.md` for the full
design.

Key concepts:

- **Coordinator** (`engine/`) — single authority for world state.
- **Systems** — stateless processes, each running exactly one system function.
- **NATS** — message transport between coordinator, systems, and editor.
- **Client** (`engine/Client/`) — C# SDK for authoring systems that
  connect to the coordinator via NATS. Other language SDKs live outside
  `engine/`.

---

## Repository Layout

```
ecs-engine/
├── engine/                     # C# solution — Coordinator + Client SDK
│   ├── Engine.sln
│   ├── Engine/                 # Coordinator (self-contained, includes core types)
│   │   ├── Engine.csproj
│   │   ├── Program.cs
│   │   └── Core/               # ECS primitives (Entity, Messages, Serialization…)
│   └── Client/                 # C# system-authoring SDK (self-contained)
│       ├── Client.csproj
│       ├── SystemRunner.cs
│       └── Core/               # Own copy of ECS primitives
├── editor/
│   ├── frontend/               # React + Vite web app
│   └── backend/                # ASP.NET Core Minimal API
│       ├── EditorBackend.sln
│       └── EditorBackend/
├── examples/                   # Example components & systems
├── .devcontainer/              # Dev container (build environment)
├── ARCHITECTURE.md
├── AGENTS.md                   # This file
└── README.md
```

---

## C# Conventions

### Target Framework

- **.NET 9** (`net9.0`) for all projects.
- Use the dev container for building — do not assume .NET is installed on the host.

### Style

- Use file-scoped namespaces.
- Use top-level statements for `Program.cs` in console apps.
- Follow standard C# naming: `PascalCase` for types and public members,
  `camelCase` for locals, `_camelCase` for private fields.
- Prefer `var` when the type is obvious from the right-hand side.
- Use expression-bodied members for simple one-liners.

### Error Handling

- Use exceptions for exceptional conditions only.
- Prefer returning `Result<T>` or nullable types for expected failure cases.
- Never swallow exceptions silently — always log.

### Async

- Use `async`/`await` throughout. No blocking calls in async contexts.
- Use `CancellationToken` for cooperative cancellation.
- Prefer `ValueTask` over `Task` for hot-path async methods that often
  complete synchronously.

### Serialisation

- Use MessagePack (`MessagePack-CSharp`) with `ContractlessStandardResolver`
  for wire format — no `[MessagePackObject]` or `[Key]` attributes needed.
- Call `Serialization.Initialize()` (from `Engine.Core`) at startup or rely on
  the Client SDK's module initializer.
- Use `System.Text.Json` only for human-readable config files.

### ECS-Specific Rules

- Components must be structs or records implementing `IComponent`.
- Entity IDs are `ulong`. Do not use `int` for entity identifiers.
- Entity references are components suffixed `Ref`, holding a single `Entity`
  field named for the target's role — `ParentRef(Entity Parent)`,
  `CellRef(Entity Cell)`. Never a raw `ulong` or a string.
- Foreign keys into external systems are suffixed `Id` and hold strings —
  `NovaControllerId(string Cell, string Controller)`. Never mix `Ref` and `Id`
  data in one component.
- Role-qualify when an entity holds two references of the same kind
  (`SourceControllerRef`) or when the target type is generic (`OwnerRef`).
  Do not name a component `EntityRef` — `CommandTarget` is the command-buffer
  target type, which is a different thing.
- Engine-generic relations live in `Engine.Core`; domain relations live in that
  domain's shared `*.Components` assembly. Component identity is the full type
  name, so the same relation declared in two namespaces never matches.
- To dereference, declare a second query matching the target entities and index
  it. All of a system's queries are populated from the same shard set in the same
  tick, so this costs bandwidth, not a round trip. Systems cannot look up an
  arbitrary entity by id.
- Systems must declare their queries explicitly — no implicit world access.
- Declare queries in the system **constructor**, not in a lifecycle hook, so query
  fields are `readonly` and non-null. `NewQuery` throws once the system has been
  added to a world.
- System lifecycle is constructor → `OnAdd` → `OnUpdateAsync` → `OnRemove`.
  `OnAdd`/`OnRemove` track world membership and may fire more than once on the
  same instance, so they must be idempotent.
- Inject collaborators (HTTP clients, etc.) through the system constructor. A
  system must not construct or dispose a resource it was not given.
- Seed, fixture and demo entities are not system logic — create them outside
  systems with `world.Commands` and `await world.FlushAsync()`. A system's own
  `Commands` buffer is only for structural changes that are part of its logic.
- Each system process runs **exactly one system function** — never multiplex
  multiple systems in a single process.
- Horizontal scaling is done by launching more instances of the same system
  behind a NATS queue group.

### Dependencies

- Keep dependency count minimal. Justify new packages.
- Use workspace-level `Directory.Build.props` for shared settings if needed.
- Pin major versions in `.csproj` files.

### Testing

- Use xUnit for unit tests.
- Write tests in a separate `.Tests` project alongside the project under test.
- Name tests descriptively: `EntityAllocator_AllocatesUniqueIds`.

---

## NATS Conventions

- All subjects are prefixed with `engine.`.
- See `ARCHITECTURE.md` for the full subject hierarchy.
- Use NATS headers for routing metadata (`msg-type`, `tick-id`, `instance-id`).
- Never put routing information in the payload.
- Use JetStream for any data that must survive restarts.

---

## Editor (React + ASP.NET Core)

### Frontend (`editor/frontend/`)

- React + TypeScript + Vite.
- Use functional components with hooks. No class components.
- TypeScript strict mode is enabled.

### Backend (`editor/backend/`)

- ASP.NET Core Minimal API.
- Connects to NATS and bridges data to the frontend over WebSocket.
- Keep endpoints thin — delegate to shared logic.

---

## Dev Container

All building and testing should happen inside the dev container:

```bash
# Build everything
dotnet build engine/Engine.sln
dotnet build editor/backend/EditorBackend.sln
cd editor/frontend && npm run build

# Run coordinator
dotnet run --project engine/Engine

# Run tests
dotnet test engine/Engine.sln
```

The dev container includes:
- .NET 9 SDK
- Node.js 22
- NATS server (started automatically via `postStartCommand`)

---

## Git Conventions

- Branch naming: `feat/<name>`, `fix/<name>`, `refactor/<name>`.
- Write clear, imperative commit messages: "Add entity allocation to coordinator".
- Keep commits atomic — one logical change per commit.
- Do not commit build artifacts (`bin/`, `obj/`, `node_modules/`).

---

## Architecture ↔ Code Consistency

`ARCHITECTURE.md` is the **source of truth** for high-level design. Code is the
source of truth for implementation detail. The two must stay in sync:

- **Code changes → update architecture.** When you add or modify a project,
  NATS subject, message type, or system lifecycle step, update
  `ARCHITECTURE.md`.
- **Architecture changes → update code.** When you change a design decision,
  propagate to the relevant code.
- **Check alignment before implementing.** Before starting work, read the
  relevant sections of `ARCHITECTURE.md` and verify the planned change is
  consistent. If not, ask the user.

---

## What NOT to Do

- Do not bypass the coordinator for entity creation — all entity IDs must come
  from the engine coordinator.
- Do not use blocking I/O in async contexts.
- Do not hardcode NATS URLs — always read from configuration or the `NATS_URL`
  environment variable.
- Do not commit `bin/`, `obj/`, or `node_modules/` directories.
- Do not install .NET or Node.js on the host — use the dev container.
