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
- **Protobuf** (`proto/`) — the single source of truth for component types,
  managed with `buf` and generated into C# and TypeScript.
- **Client** (`clients/csharp/`) — C# SDK for authoring systems that
  connect to the coordinator via NATS. Other language SDKs live under
  `clients/`.

---

## Repository Layout

```
ecs-engine/
├── proto/                       # Protobuf component schemas (buf module)
│   ├── ecs/v1/                  # EntityId, ComponentInfo, ComponentSchema, ParentRef
│   ├── movement/v1/
│   ├── nova/v1/
│   ├── testing/v1/              # Components used only by test suites
│   └── gen/                     # `buf generate` output — committed, never hand-edited
│       ├── csharp/              # Ecs.Protos.csproj
│       └── ts/                  # @ecs/protos npm package
├── engine/                      # C# solution — Coordinator
│   ├── Engine.sln
│   └── Engine/                  # Coordinator (self-contained, includes core types)
├── clients/                     # Language-specific SDKs
│   └── csharp/                  # C# client SDK
│       ├── CSharp.sln
│       ├── Client/              # SDK library (own copy of ECS primitives)
│       └── Client.Tests/
├── editor/                      # One Node process: React UI + API + NATS bridge
│   ├── src/client/              # React + Mantine
│   ├── src/server/              # Hono, MessagePack envelopes, schema registry
│   └── test/                    # Vitest, plus wire fixtures written by Client.Tests
├── examples/                    # Example systems
├── deployments/nova/            # Wandelbots NOVA installer
├── .devcontainer/               # Dev container (build environment)
├── buf.yaml / buf.gen.yaml
├── package.json                 # npm workspace root (buf CLI, protos, editor)
├── ARCHITECTURE.md
├── AGENTS.md                    # This file
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

- **Component payloads are Protobuf.** Define them in `proto/`, run
  `npm run proto:generate`, and use the generated types. Never hand-write a
  component type and never edit anything under `proto/gen/`.
- **Message envelopes are MessagePack** (`MessagePack-CSharp`,
  `ContractlessStandardResolver`, no attributes). Call `Serialization.Initialize()`
  at startup or rely on the Client SDK's module initializer.
- Do not mix the two: the coordinator stores component payloads as opaque bytes
  and never decodes them, except `ecs.v1.ComponentInfo`, which it parses with
  protobuf directly.
- Envelope field names, `Guid` formatting and integer widths are a cross-language
  contract with the editor's TypeScript decoders. `Client.Tests` writes fixtures
  into `editor/test/fixtures/`; change an envelope and you must regenerate them.
- Use `System.Text.Json` only for human-readable config files.

### Protobuf & buf

- One buf module at the repo root (`buf.yaml`), sources under `proto/`.
- Packages are versioned and idiomatic: `ecs.v1`, `movement.v1`, `nova.v1`.
  A component's identity is its full name, e.g. `movement.v1.Position`.
- Engine-generic types live in `ecs.v1`; domain types live in that domain's own
  package.
- Run `npx buf lint` and `npx buf format -w` before committing. CI also runs
  `buf breaking` against `main`.
- Generated code is committed; CI fails if `buf generate` produces a diff.

### ECS-Specific Rules

- Components are protobuf messages. In C# they satisfy
  `where T : IMessage<T>, new()` — there is no `IComponent` marker.
- Entity IDs are `ulong`. Do not use `int` for entity identifiers.
- Entity references are components suffixed `Ref`, holding a single
  `ecs.v1.EntityId` field named for the target's role — `ParentRef`, `CellRef`.
  Never a raw `uint64` or a string.
- Foreign keys into external systems are suffixed `Id` and hold strings —
  `NovaControllerId { cell, controller }`. Never mix `Ref` and `Id` data in one
  component.
- Role-qualify when an entity holds two references of the same kind
  (`SourceControllerRef`) or when the target type is generic (`OwnerRef`).
  Do not name a component `EntityRef` — `CommandTarget` is the command-buffer
  target type, which is a different thing.
- In C# authoring code use the `Entity` struct; it converts implicitly to and
  from `ecs.v1.EntityId`. proto3 has no `required`, so an unset reference reads
  as entity `0`.
- An empty protobuf message is **zero bytes**, so absence is encoded as `null`
  in shards, never as a zero-length payload. Do not reintroduce length checks.
- A component type describes itself through the `ecs.v1.description` message
  option, which carries `google.protobuf.Any` attachments. The SDK replays them
  as ordinary `AddComponent` commands on the type entity.
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
- `proto/gen/csharp/Ecs.Protos.csproj` is the one project both Engine and Client
  reference. It is generated code, so it is exempt from the rule below that the
  two keep their own copies of the ECS primitives.
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

## Editor (one Node process)

The editor is a single npm package at `editor/`. It serves the React bundle, the
HTTP API and the WebSocket from the same origin, and bridges NATS. There is no
separate backend service and no backend URL to configure.

### Client (`editor/src/client/`)

- React + TypeScript + Vite, Mantine for UI.
- Use functional components with hooks. No class components.
- TypeScript strict mode is enabled.
- Talk to the API with relative paths — never an absolute origin.

### Server (`editor/src/server/`)

- Hono on `@hono/node-server`, listening on port 8080.
- Decodes MessagePack envelopes by hand in `msgpack.ts`; keep it in step with
  `Messages.cs` and the fixtures in `editor/test/fixtures/`.
- Decodes component payloads through `SchemaRegistry`, which is built from the
  `ecs.v1.ComponentSchema` descriptors the world publishes. Do not add generated
  types for domain components here — the point is that the editor does not need
  them.

---

## Dev Container

All building and testing should happen inside the dev container:

```bash
# Regenerate component types after editing any .proto
npm run proto:generate

# Build everything
dotnet build engine/Engine.sln
dotnet build clients/csharp/CSharp.sln
dotnet build examples/Examples.sln
npm run build

# Run coordinator
dotnet run --project engine/Engine

# Run editor (http://localhost:8080)
npm run start --workspace @ecs/editor

# Run tests
dotnet test engine/Engine.sln
dotnet test clients/csharp/CSharp.sln
npm test
```

The dev container includes:
- .NET 9 SDK
- Node.js 22
- buf CLI
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
- Do not hand-write component types — define them in `proto/` and generate.
- Do not edit anything under `proto/gen/` — it is regenerated and CI checks it.
- Do not use blocking I/O in async contexts.
- Do not hardcode NATS URLs — always read from configuration or the `NATS_URL`
  environment variable.
- Do not commit `bin/`, `obj/`, `dist/` or `node_modules/` directories.
- Do not install .NET, Node.js or buf on the host — use the dev container.
