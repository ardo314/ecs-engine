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
- **Client** (`engine/Client/`) — SDK for authoring systems that
  connect to the coordinator via NATS.

---

## Repository Layout

```
ecs-engine/
├── engine/                     # C# solution — Coordinator + Client SDK
│   ├── Engine.sln
│   ├── Engine/
│   │   ├── Engine.csproj
│   │   └── Program.cs
│   └── Client/                 # System-authoring SDK (class library)
│       ├── Client.csproj
│       └── SystemRunner.cs
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
- Systems must declare their queries explicitly — no implicit world access.
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
