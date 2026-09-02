# Architecture — Distributed ECS

## Overview

This engine implements a **distributed Entity Component System (ECS)** where the
world state is spread across multiple processes that communicate over
[NATS](https://nats.io). The **engine** project acts as the **central
coordinator** — it owns the canonical entity table, registers systems and
queries, orchestrates tick execution, and brokers component data between system
processes.

Each **system** is both the logic _and_ the process that runs it — there is no
separate "worker" concept. A system is a standalone process that connects to
NATS, declares its query, receives component shards, executes, and publishes
results. Horizontal scaling is achieved by launching multiple instances of the
same system behind a NATS queue group — the coordinator distributes archetype
shards across instances automatically.

```
┌──────────────────────────────────────────────────────────────┐
│                        NATS Cluster                          │
└──┬──────────┬──────────┬──────────┬──────────┬────────┬──┬──┘
   │          │          │          │          │        │  │
   ▼          ▼          ▼          ▼          ▼        ▼  ▼
┌───────┐ ┌───────┐ ┌───────┐ ┌───────┐ ┌───────┐ ┌────────┐
│Physics│ │Physics│ │  AI   │ │Render │ │  …    │ │ Editor │
│ (#1)  │ │ (#2)  │ │       │ │Prep   │ │       │ │        │
└───────┘ └───────┘ └───────┘ └───────┘ └───────┘ └────────┘
     ▲         ▲         ▲
     │         │         │
     └─────────┴─────────┘
               │
     ┌─────────┴─────────┐
     │      Engine        │
     │   (Coordinator)    │
     └────────────────────┘
```

> Instances of the same system (e.g. Physics #1 and #2) form a **NATS queue
> group** so the coordinator can scatter shards across them.

---

## Technology Stack

| Layer              | Technology                              |
| ------------------ | --------------------------------------- |
| Language           | C# / .NET 9                             |
| Messaging          | NATS (via `NATS.Net`)                   |
| Component schemas  | Protobuf, managed with `buf`            |
| Component payloads | Protobuf binary                         |
| Message envelopes  | MessagePack (`MessagePack-CSharp`)      |
| Editor             | Node 22 + Hono + React + Vite           |
| Editor Comms       | WebSocket (server ↔ browser)             |
| Dev Environment    | Dev Container (.NET 9 + Node 22 + buf)  |

---

## Core Concepts

### Entity

A unique `ulong` identifier allocated by the coordinator. Entities have no data
of their own — they are pure identifiers that components are attached to.

### Component

A serialisable piece of data attached to an entity (e.g. `Transform3D`,
`Velocity`). Components are **defined in Protobuf**, not in any host language.
The `.proto` files live in `proto/`, are governed by a local `buf` module, and
`buf generate` produces the C# and TypeScript types from them. There is no
hand-written component type anywhere in the repository.

```proto
// proto/movement/v1/movement.proto
package movement.v1;

message Position {
  float x = 1;
  float y = 2;
  float z = 3;
}
```

Component **payloads** are protobuf binary. The message **envelopes** that carry
them (`SystemDescriptor`, `ComponentShard`, `WatchData`, …) stay MessagePack —
the coordinator treats payloads as opaque bytes, so the two encodings never meet.

A component's identity is its **protobuf full name** — `movement.v1.Position`,
`nova.v1.CellRef` — which is language-neutral, so a system written in any
language addresses the same type as any other.

Because an empty protobuf message encodes to zero bytes, a shard cannot use
length to signal absence: a missing component is sent as `null`, and a
zero-length payload is a genuine marker component.

### Component Type

Component types are **entities themselves**. Every component type a system uses
gets an entity allocated from the same counter as any other entity, carrying
`ecs.v1.ComponentInfo { type_name }` and `ecs.v1.ComponentSchema
{ file_descriptor_set }`. Everything else on a type entity is an ordinary
user-defined component.

`ComponentSchema` holds the **transitively closed `FileDescriptorSet`** for the
type's own `.proto` file. That is what makes the type system self-describing: a
tool can decode and render instances of a component type it was never compiled
against, which is exactly what the editor does in the browser-facing layer.

A component describes itself further through the `ecs.v1.description` message
option, which carries an open set of `google.protobuf.Any` attachments:

```proto
message PidSettings {
  option (ecs.v1.description) = {
    [type.googleapis.com/nova.v1.Setting] {}
  };
  option (ecs.v1.description) = {
    [type.googleapis.com/nova.v1.Category] {name: "Control"}
  };

  float kp = 1;
  float ki = 2;
  bool enabled = 3;
}
```

Describing is optional — a component that describes nothing still gets a type
entity with its `ComponentInfo` and `ComponentSchema`. The Client SDK reads the
option once per component type a system uses, whether that use is a query or a
command, and replays each attachment as an ordinary `AddComponent` command
through the same command buffer, subjects and tick phase as any other structural
change. There is no separate schema message, registration API or startup hook.

Commands buffered during `OnAdd` are held until the system receives its first
schedule, which proves the coordinator is running — otherwise they would be
published into the void when a system starts before the coordinator.

Because the attachments are ordinary components on an ordinary entity, an open
set of user-defined contracts is expressed without the engine knowing what any of
them mean, and "which component types carry `Setting`" is an ordinary entity
query. The description is world data, so it outlives the process that sent it.
The attachments also travel inside the descriptor, so a consumer that only has
the `FileDescriptorSet` sees them too.

### Entity References

The engine has no relationship primitive. One entity points at another with an
ordinary component holding an `ecs.v1.EntityId` field, governed by one naming
rule:

| Suffix | Field type       | Means |
| ------ | ---------------- | ----- |
| `Ref`  | `ecs.v1.EntityId`| A reference to another entity **in this world**. |
| `Id`   | `string`         | A foreign key into an **external system**. |

```proto
message ParentRef { ecs.v1.EntityId parent = 1; }                  // in-world
message NovaControllerId { string cell = 1; string controller = 2; } // external
```

A `Ref` component holds exactly one `EntityId`, named for the role the target
plays. Never a raw `uint64`, never a string. Where an entity holds two references
of the same kind, the role qualifies the prefix — `SourceControllerRef`,
`TargetControllerRef`. Do not name a component `EntityRef`; untyped references
are role-qualified too (`OwnerRef`, `TargetRef`).

`ParentRef` is the one relation the engine defines, in `ecs.v1`. Domain relations
belong in that domain's own package — component identity is the full name, so the
same relation declared twice in two packages silently never matches.

The message is called `EntityId` rather than `Entity` so it does not collide with
the SDK's `Entity` struct, which stays the authoring type: it is a value type, so
query iteration allocates nothing, and it converts implicitly to and from
`ecs.v1.EntityId` so authoring code never names the generated type. Note that
proto3 has no `required`, so a reference field is always presence-tracked; an
unset reference reads as entity `0`.

`Entity` and the conversions are authoring-side types and live in the client SDK
only — the coordinator addresses entities by raw id and stores component payloads
as opaque bytes, so it never resolves a reference.

#### Dereferencing

A query only ships the entities it matches, so a system cannot look up an
arbitrary entity by id. To read the target of a reference, declare a **second
query** matching the target entities and index it:

```csharp
public MySystem()
{
    _children = NewQuery().With(Query.ReadOnly<ParentRef>());
    _parents  = NewQuery().With(Query.ReadOnly<Transform3D>());
}
```

Both queries are populated from the same shard set in the same tick, so this
costs extra **bandwidth**, not an extra round trip. Multi-hop traversal is not:
following a chain of references one level per tick means one tick of latency per
hop. Where a hop is hot, denormalise — cache the resolved value on the entity as
its own component and let one system keep it current.

### Archetype

A unique combination of component types. Entities with the same set of
components are stored together for cache-friendly iteration. Each archetype is
identified by a deterministic hash of its sorted component type IDs.

### System

A function that operates on a **query** — a filtered view of entities and their
components. Each system runs as its own process. A system connects to NATS,
declares its query to the coordinator, receives matching component shards,
executes, and publishes changed data back. Multiple instances of the same
system can be launched to parallelise work across archetype shards via NATS
queue groups.

#### Lifecycle

| Stage | Purpose |
| ----- | ------- |
| Constructor | Declare queries and take dependencies. Fields are `readonly`. |
| `OnAdd` | The system joined a world. Acquire world-scoped resources. |
| `OnUpdateAsync` | One tick. |
| `OnRemove` | The system left the world. Release what `OnAdd` acquired. |

Queries are declared in the **constructor**, not in `OnAdd` — they describe what
the system *is*, not which world it currently belongs to. `NewQuery` throws once
the system has been added, so the declaration cannot drift into a lifecycle hook.
This keeps query fields `readonly` and non-null from construction, and makes
`OnAdd`/`OnRemove` a genuine cycle: the same instance can be removed from a world
and added again without accumulating duplicate query registrations.

Collaborators are injected, not constructed internally, so a system owns no
resource it did not receive and can be tested against a substitute:

```csharp
using var novaClient = new NovaIoClient(baseUrl);
world.AddSystem(new SetControllerIOSystem(novaClient));
```

```csharp
public class MovementSystem : SystemBase
{
    private readonly EntityQuery _q;

    public MovementSystem()
    {
        _q = NewQuery()
            .With(Query.ReadWrite<Position>())
            .With(Query.ReadOnly<Velocity>());
    }

    protected override Task OnUpdateAsync() { /* ... */ }
}
```

#### World-level commands

Seed data, fixtures and demo entities are not system logic, so they are not
created by systems. `World` carries its own command buffer for changes made
outside any system:

```csharp
world.Commands.CreateEntity(new Position(0f, 0f, 0f), new Velocity(1f, 0f, 0f));
await world.FlushAsync();
```

`FlushAsync` waits for the coordinator before publishing, so commands issued
before it starts are not lost. Systems keep their own `Commands` buffer for
structural changes that *are* part of their logic; those flush every tick.

### Query

A declarative description of which component types a system needs, and whether
it needs them mutably or immutably. The coordinator uses queries to compute
data dependencies and schedule systems with maximum parallelism.

#### Tag joins

A query can select on the type system instead of on concrete types:

```csharp
_settings = NewQuery()
    .With(Query.ReadOnly<ControllerRef>())
    .WithAnyTagged<Setting>();
```

`WithAnyTagged<TTag>` matches entities carrying at least one component whose
**type entity** has `TTag` — the join the editor performs manually in two steps,
expressed as one query. The coordinator resolves the tag to concrete type names
every tick, matches entities against them, ships those shards, and returns the
resolution in `SystemSchedule.TaggedTypes`, so component types that appear later
are picked up without the system changing. The resolved types also count as reads
for stage conflict detection.

Because the concrete types are unknown at compile time, tagged components are
read-only and are read by type name:

```csharp
foreach (var entity in _settings.Entities)
foreach (var tagged in _settings.GetTagged<Setting>(entity))
{
    if (tagged.Is<PidSettings>())
        Use(tagged.As<PidSettings>());
}
```

Multiple `WithAnyTagged` calls on one query are ANDed — one matching component
per tag.

---

## Repository Layout

```
ecs-engine/
├── engine/                     # C# solution — Coordinator
│   ├── Engine.sln
│   ├── Engine/                 # Self-contained: includes the core types it needs
│   │   ├── Engine.csproj
│   │   └── Program.cs
│   └── Engine.Tests/
├── clients/                    # Language-specific SDKs
│   └── csharp/
│       ├── CSharp.sln
│       ├── Client/             # System-authoring SDK (class library)
│       │   ├── Client.csproj
│       │   └── SystemRunner.cs
│       └── Client.Tests/
├── proto/                      # Protobuf component schemas (a buf module)
│   ├── ecs/v1/                 # Engine-generic: EntityId, ComponentInfo, ComponentSchema
│   ├── movement/v1/
│   ├── nova/v1/
│   └── gen/                    # `buf generate` output, committed
│       ├── csharp/             # Ecs.Protos.csproj, referenced by Engine and Client
│       └── ts/                 # @ecs/protos npm package (browser and Node)
├── editor/                     # One Node process: React UI + API + NATS bridge
│   ├── package.json
│   ├── vite.config.ts
│   ├── index.html
│   └── src/
│       ├── client/             # React + Mantine
│       └── server/             # Hono, MessagePack envelopes, schema registry
├── examples/                   # Example systems
├── deployments/                # Target-specific installers
│   └── nova/                   # Wandelbots NOVA app installer + NATS image
├── .devcontainer/              # Dev container (build environment)
├── buf.yaml                    # buf workspace
├── buf.gen.yaml                # Codegen: C# + TypeScript
├── ARCHITECTURE.md             # This file
├── AGENTS.md                   # AI agent guidelines
└── README.md
```

---

## NATS Subject Hierarchy

All subjects are prefixed with `engine.` to namespace within a shared NATS
cluster.

| Subject                             | Direction               | Payload                                         | Purpose                                           |
| ----------------------------------- | ----------------------- | ----------------------------------------------- | ------------------------------------------------- |
| `engine.coord.tick.done`            | Systems → Coordinator   | `TickAck { TickId, InstanceId }`                | System instance acknowledges tick completion.     |
| `engine.entity.create`              | Coordinator → *         | `EntityCreated { EntityId, ComponentTypes }`    | Broadcasts entity creation.                       |
| `engine.entity.destroyed`           | Coordinator → *         | `EntityDestroyed { EntityId }`                  | Broadcasts entity destruction.                    |
| `engine.entity.spawn.request`       | Any → Coordinator       | `EntitySpawnRequest { ComponentTypes, ComponentData }` | Requests entity creation.                   |
| `engine.entity.destroy.request`     | Any → Coordinator       | `EntityDestroyRequest { EntityIds }`            | Requests entity destruction.                      |
| `engine.component.set.<system>`     | Coordinator → System(s) | `ComponentShard { TickId, Entities, ComponentType, Data }` | Sends component data to a system.      |
| `engine.component.changed.<system>` | Systems → Coordinator   | `ComponentChanges { TickId, ComponentType, Entities, Data }` | System publishes mutated data back.  |
| `engine.entity.component.add`       | Any → Coordinator       | `ComponentAddRequest { Target, ComponentType, Data }` | Upserts a component on the target.                |
| `engine.entity.component.remove`    | Any → Coordinator       | `ComponentRemoveRequest { Target, ComponentType }` | Removes a component from the target.              |
| `engine.system.register`            | System → Coordinator    | `SystemDescriptor { Name, InstanceId, Queries }` | System registers itself on startup.              |
| `engine.system.unregister`          | System → Coordinator    | `SystemUnregister { Name, InstanceId }`         | System unregisters on shutdown.                   |
| `engine.system.schedule.<system>`   | Coordinator → System(s) | `SystemSchedule { TickId, ShardCount, TaggedTypes }` | Tells system to execute on a shard, with the tick's tag resolution. |
| `engine.query.systems`              | Any → Coordinator       | (empty)                                          | Request/reply: returns registered systems + stages. |
| `engine.query.entities`             | Any → Coordinator       | `QueryEntitiesRequest { ComponentFilter?, AnyTypes? }` | Request/reply: returns matching entities + data.  |
| `engine.watch.subscribe`            | Any → Coordinator       | `WatchRequest { WatchId, Include*, Filter }`    | Request/reply: register a watch subscription.     |
| `engine.watch.unsubscribe`          | Any → Coordinator       | `WatchCancel { WatchId }`                       | Cancels an active watch subscription.             |
| `engine.watch.data.<watchId>`       | Coordinator → Watcher   | `WatchData { TickId, Systems?, Entities? }`     | Per-tick data pushed to an active watcher.        |

> All subjects currently use core NATS. Systems are driven by
> `engine.system.schedule.<system>` rather than a global tick broadcast, so a
> system only wakes when it has matching entities.

---

## Tick Lifecycle

```
Coordinator                         Systems (one process each)
    │                                  │
    │── 0. Apply pending system        │
    │      register/unregister changes │
    │── 1. Allocate / destroy entities │
    │── 2. Build dependency graph      │
    │── 3. Compute execution stages    │
    │                                  │
    │   ┌─── Stage 1 (parallel) ───┐   │
    │   │  Systems with no conflicts│   │
    ├──►│  run concurrently         │   │
    │   └──────────────────────────┘   │
    │── 4a. Merge stage 1 results      │
    │                                  │
    │   ┌─── Stage 2 (parallel) ───┐   │
    │   │  Next conflict-free set   │   │
    ├──►│  runs concurrently        │   │
    │   └──────────────────────────┘   │
    │── 4b. Merge stage 2 results      │
    │                                  │
    │── 5. Broadcast events            │
    │── 6. Advance tick                │
    ▼                                  ▼
```

### Scheduling Algorithm

Two systems **conflict** if one writes a component type that the other reads or
writes. Systems with no conflicts run in the same stage (parallel). Conflicting
systems are placed in separate sequential stages with a merge barrier between
them.

---

## Editor Integration

The editor is a **single Node process**. It serves the React bundle, exposes the
HTTP API and the WebSocket, and bridges NATS — all from one origin, so the browser
needs no backend URL and there is nothing to inject at container start. The
coordinator has no knowledge of the editor; it only exposes generic NATS
endpoints.

### Decoding components it was never built against

The editor holds generated types only for `ecs.v1`. Everything else it learns at
runtime: as component type entities arrive it absorbs their `ComponentSchema`
descriptors into a `protobuf-es` file registry, then decodes each component
payload to canonical protobuf JSON through that registry. A new component type
therefore shows up in the editor with correct field names and types without the
editor being rebuilt or knowing the domain.

### Query APIs (request/reply)

- **`engine.query.systems`** — returns all registered systems with their
  read/write declarations and computed execution stages.
- **`engine.query.entities`** — returns entities with component data. Accepts
  an optional `ComponentFilter` (entity has ALL of these types) and an optional
  `AnyTypes` (entity has ANY of these types). Component type entities are
  returned by the same endpoint, so tools discover the type system through it.

A generic editor for a user-defined contract such as `Setting` therefore
needs no engine support:

1. Query entities with `ComponentFilter = ["nova.v1.Setting"]` → the type
   entities carrying that component.
2. Read their `ComponentInfo` → the component type names, and their
   `ComponentSchema` → how to decode instances of them.
3. Query entities with `AnyTypes = [<those type names>]` → the instances.
4. Edit and write back via `engine.entity.component.add`, which upserts.

### Watch API (subscription)

1. Client sends a `WatchRequest` to `engine.watch.subscribe` specifying what
   to include (systems, entities, optional `ComponentFilter`/`AnyTypes`) and a
   `WatchId`.
2. Coordinator replies with a `WatchResponse` containing the `DataSubject`
   (`engine.watch.data.<watchId>`) to subscribe to.
3. At the end of each tick, the coordinator publishes `WatchData` to the
   watcher's data subject. Systems/stages are only included when they change.
4. Client sends `WatchCancel` to `engine.watch.unsubscribe` to stop.

The editor uses this to provide:

- **Real-time entity inspection** with deserialized component field values.
- **System schedule view** showing systems grouped by execution stage with
  their read/write component queries.
- **Live tick counter** showing the current simulation tick.

---

## Serialisation

Two encodings, cleanly separated:

- **Component payloads are Protobuf.** Schemas live in `proto/`, so the same
  component type is addressable from any language with a protobuf runtime. The
  coordinator never decodes them — they are opaque bytes to it.
- **Message envelopes are MessagePack** (`MessagePack-CSharp`, contractless
  resolver). These are the coordinator's own protocol: `SystemDescriptor`,
  `ComponentShard`, `WatchData` and friends.

The contractless resolver writes C# property names verbatim, so envelope keys are
`PascalCase`, `Guid` is its 36-character string and `ulong` is a uint64. The
editor's TypeScript decoders depend on that; `Client.Tests` writes wire fixtures
that the editor's Vitest suite reads back, so the two implementations cannot
drift apart silently.

NATS headers carry routing metadata (`msg-type`, `tick-id`, `instance-id`) so
consumers can filter without deserialising the payload.

---

## Error Handling & Resilience

| Failure           | Mitigation                                                                |
| ----------------- | ------------------------------------------------------------------------- |
| System crash      | Coordinator detects missing ack; other queue group instances continue.    |
| Coordinator crash | JetStream retains state; new coordinator replays and resumes.             |
| NATS disconnect   | NATS.Net reconnects automatically; systems buffer and retry.              |
| Slow system       | Scale horizontally (more queue group instances). Tick deadline enforced.  |

---

## Deployment

Every process ships as a container image. Locally they are wired together by
`docker-compose.yml`.

### Health endpoint

Orchestrators that health-probe over HTTP treat a headless process as unhealthy,
so every process — coordinator, systems, editor backend and the NOVA installer —
hosts a `WebApplication`. `HealthEndpoint` — duplicated into `Engine` and the C#
client SDK, following the same asymmetric-copy rule as the core types — is the
extension that adds the probe surface to any of them:

- `builder.AddHealthEndpoint()` binds port 8080, the port every deployment target
  probes, unless the host was already pointed elsewhere (`ASPNETCORE_URLS`,
  `--urls`, a launch profile).
- `app.UseBasePath()` mounts the whole app below the host-injected `BASE_PATH`
  with `UsePathBase`, so every endpoint — not only the probes — is reachable
  through the ingress prefix (`/<cell>/<app>/api/entities`). It goes before any
  other middleware.
- `app.UseHealthEndpoint()` maps `GET /health` and `GET /app_icon.png`.
- `HealthEndpoint.TryStartAsync()` builds and starts a host that serves nothing
  else, for a process with no HTTP surface of its own. It returns null when the
  port is already taken rather than failing, which keeps several local processes
  on one machine working. The client SDK's `ECS` starts one for every system.

### Wandelbots NOVA

NOVA exposes no direct Kubernetes access, so `deployments/nova` installs the
stack through the NOVA app API (`POST /api/v2/cells/{cell}/apps`). Each process
becomes one NOVA app, published at `http://<instance>/<cell>/<app-name>`:

| App | Serves |
| --- | --- |
| `ecs-engine` | Coordinator |
| `ecs-editor` | Editor UI and API, mounted at the public path NOVA injects as `BASE_PATH` |
| `ecs-<system>` | One app per system image |

No broker is installed: NOVA runs its own NATS and injects the address as
`NATS_BROKER` — a `nats://user:token@host` URL, credentials included — which both
the coordinator and `NatsConfig` read when `NATS_URL` is unset and mask before
logging. NOVA also injects `NOVA_API` (the REST endpoint) and `CELL_NAME`, which
the installer and the NOVA example systems fall back to so they work unconfigured
inside an instance. The coordinator is installed before anything that talks to it.
Re-running the installer deletes and recreates existing apps, so it doubles as an
upgrade.

The installer can itself be deployed as a NOVA app. It serves `/health` from its
own `HealthEndpoint` for the duration of the install and then idles when NOVA's
injected `BASE_PATH` is present, because NOVA restarts an app whose probe stops
answering — a one-shot process would reinstall the stack on every restart.

---

## Design Decisions

1. **NATS over gRPC** — Built-in pub/sub, queue groups, JetStream persistence.
2. **System = process** — Simple failure isolation, trivial horizontal scaling.
3. **Coordinator as single authority** — Simplifies entity allocation and conflict resolution.
4. **Staged scheduling** — Maximises parallelism while guaranteeing data-race freedom.
5. **Protobuf for components** — One language-neutral definition, generated types for
   every SDK, and descriptors the world can carry so tools decode types they were
   never built against.
6. **MessagePack for envelopes** — The coordinator's own protocol needs no schema
   sharing and never inspects payloads, so a contractless encoding costs nothing.
7. **Archetype-based storage** — Cache-friendly SoA layout, efficient batch shipping.
8. **Fixed tick loop** — Deterministic simulation.
9. **One editor process** — UI and API share an origin, so there is no CORS, no
   backend URL to configure and one image to ship.

---

## Dependencies

| Package                    | Purpose                                        |
| -------------------------- | ---------------------------------------------- |
| `NATS.Net`                 | NATS client for .NET                           |
| `MessagePack`              | Message envelope serialisation                 |
| `Google.Protobuf`          | Component payload serialisation (C#)           |
| `@bufbuild/buf`            | Protobuf linting, formatting and codegen       |
| `@bufbuild/protobuf`       | Component payload serialisation (TypeScript)   |
| `@nats-io/transport-node`  | NATS client for Node                           |
| `@msgpack/msgpack`         | Message envelope decoding in the editor        |
| `hono` / `@hono/node-server` | Editor HTTP, static serving and WebSocket    |
