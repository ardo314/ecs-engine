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

| Layer              | Technology                          |
| ------------------ | ----------------------------------- |
| Language           | C# / .NET 9                         |
| Messaging          | NATS (via `NATS.Net`)               |
| Serialisation      | MessagePack (`MessagePack-CSharp`)  |
| Editor Backend     | ASP.NET Core Minimal API            |
| Editor Frontend    | React + TypeScript + Vite           |
| Editor Comms       | WebSocket (backend ↔ frontend)      |
| Dev Environment    | Dev Container (.NET 9 + Node 22)    |

---

## Core Concepts

### Entity

A unique `ulong` identifier allocated by the coordinator. Entities have no data
of their own — they are pure identifiers that components are attached to.

### Component

A serialisable piece of data attached to an entity (e.g. `Transform3D`,
`Velocity`). Components are serialized with MessagePack using the contractless
resolver — no attributes are required on the types.

### Component Type

Component types are **entities themselves**. Every component type a system uses
gets an entity allocated from the same counter as any other entity, carrying
`ComponentInfo { TypeName }`. Everything else on a type entity is an ordinary
user-defined component.

A component describes itself by buffering commands against its own type entity:

```csharp
public readonly record struct Setting : IComponent;
public readonly record struct Category(string Name) : IComponent;

public record struct PidSettings(float Kp, float Ki, bool Enabled) : IComponent
{
    public static void Describe(EntityCommandBuffer commands, EntityRef self)
    {
        commands.AddComponent(self, new Setting());
        commands.AddComponent(self, new Category("Control"));
    }
}
```

`Describe` is a `static virtual` member of `IComponent` with an empty default, so
implementing it is optional — a component that describes nothing still gets a type
entity with its `ComponentInfo`. The Client SDK calls it once per component type a
system uses — discovered from the system's queries — and the resulting commands
flow through the same command buffer, subjects and tick phase as any other
structural change. `ComponentInfo` is itself an ordinary component, so there is no
separate schema message, registration API or startup hook.

Because the attachments are ordinary components on an ordinary entity, an open
set of user-defined contracts is expressed without the engine knowing what any of
them mean, and "which component types carry `Setting`" is an ordinary entity
query. The description is world data, so it outlives the process that sent it.

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

### Query

A declarative description of which component types a system needs, and whether
it needs them mutably or immutably. The coordinator uses queries to compute
data dependencies and schedule systems with maximum parallelism.

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
│   │   ├── package.json
│   │   ├── vite.config.ts
│   │   ├── index.html
│   │   └── src/
│   └── backend/                # ASP.NET Core Minimal API
│       ├── EditorBackend.sln
│       └── EditorBackend/
│           ├── EditorBackend.csproj
│           └── Program.cs
├── examples/                   # Example components & systems
├── .devcontainer/              # Dev container (build environment)
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
| `engine.coord.tick`                 | Coordinator → Systems   | `TickStart { TickId, Dt }`                      | Signals start of a new tick.                      |
| `engine.coord.tick.done`            | Systems → Coordinator   | `TickAck { TickId, InstanceId }`                | System instance acknowledges tick completion.     |
| `engine.entity.create`              | Coordinator → *         | `EntityCreated { Entity, Archetype }`           | Broadcasts entity creation.                       |
| `engine.entity.destroy`             | Coordinator → *         | `EntityDestroyed { Entity }`                    | Broadcasts entity destruction.                    |
| `engine.entity.spawn.request`       | Systems → Coordinator   | `EntitySpawnRequest { Types, Data }`            | System requests entity creation.                  |
| `engine.component.set.<system>`     | Coordinator → System(s) | `ComponentShard` or `DataDone` sentinel         | Sends component data to a system.                 |
| `engine.component.changed.<system>` | Systems → Coordinator   | `ComponentShard` or `ChangesDone` sentinel      | System publishes mutated data back.               |
| `engine.entity.component.add`       | Any → Coordinator       | `ComponentAddRequest { Target, ComponentType, Data }` | Upserts a component on the target.                |
| `engine.entity.component.remove`    | Any → Coordinator       | `ComponentRemoveRequest { Target, ComponentType }` | Removes a component from the target.              |
| `engine.system.register`            | System → Coordinator    | `SystemDescriptor { Name, Query, InstanceId }`  | System registers itself on startup.               |
| `engine.system.unregister`          | System → Coordinator    | `SystemUnregister { Name, InstanceId }`         | System unregisters on shutdown.                   |
| `engine.system.schedule.<system>`   | Coordinator → System(s) | `SystemSchedule { TickId, ShardRange }`         | Tells system to execute on a shard.               |
| `engine.system.heartbeat`           | Systems → Coordinator   | `Heartbeat { InstanceId, System, Load }`        | Periodic health & load report.                    |
| `engine.query.systems`              | Any → Coordinator       | (empty)                                          | Request/reply: returns registered systems + stages. |
| `engine.query.entities`             | Any → Coordinator       | `QueryEntitiesRequest { ComponentFilter?, AnyTypes? }` | Request/reply: returns matching entities + data.  |
| `engine.watch.subscribe`            | Any → Coordinator       | `WatchRequest { WatchId, Include*, Filter }`    | Request/reply: register a watch subscription.     |
| `engine.watch.unsubscribe`          | Any → Coordinator       | `WatchCancel { WatchId }`                       | Cancels an active watch subscription.             |
| `engine.watch.data.<watchId>`       | Coordinator → Watcher   | `WatchData { TickId, Systems?, Entities? }`     | Per-tick data pushed to an active watcher.        |

> JetStream is used for `engine.component.*` subjects so that late-joining
> system instances can replay the latest state.

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

The editor backend connects to NATS and bridges state to the React frontend
over WebSocket using the coordinator's generic query/watch APIs. The coordinator
has no knowledge of the editor — it only exposes generic NATS endpoints.

### Query APIs (request/reply)

- **`engine.query.systems`** — returns all registered systems with their
  read/write declarations and computed execution stages.
- **`engine.query.entities`** — returns entities with component data. Accepts
  an optional `ComponentFilter` (entity has ALL of these types) and an optional
  `AnyTypes` (entity has ANY of these types). Component type entities are
  returned by the same endpoint, so tools discover the type system through it.

A generic editor for a user-defined contract such as `Setting` therefore
needs no engine support:

1. Query entities with `ComponentFilter = ["Nova.Components.Setting"]` → the type
   entities carrying that component.
2. Read their `ComponentInfo` → the component type names.
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

All messages are serialised with **MessagePack** (`MessagePack-CSharp`) for
compact binary encoding. NATS headers carry routing metadata (`msg-type`,
`tick-id`, `instance-id`) so consumers can filter without deserialising the
payload.

---

## Error Handling & Resilience

| Failure           | Mitigation                                                                |
| ----------------- | ------------------------------------------------------------------------- |
| System crash      | Coordinator detects missing ack; other queue group instances continue.    |
| Coordinator crash | JetStream retains state; new coordinator replays and resumes.             |
| NATS disconnect   | NATS.Net reconnects automatically; systems buffer and retry.              |
| Slow system       | Scale horizontally (more queue group instances). Tick deadline enforced.  |

---

## Design Decisions

1. **NATS over gRPC** — Built-in pub/sub, queue groups, JetStream persistence.
2. **System = process** — Simple failure isolation, trivial horizontal scaling.
3. **Coordinator as single authority** — Simplifies entity allocation and conflict resolution.
4. **Staged scheduling** — Maximises parallelism while guaranteeing data-race freedom.
5. **MessagePack over JSON/Protobuf** — Compact binary, no schema compilation.
6. **Archetype-based storage** — Cache-friendly SoA layout, efficient batch shipping.
7. **Fixed tick loop** — Deterministic simulation.

---

## Dependencies

| Package          | Purpose                              |
| ---------------- | ------------------------------------ |
| `NATS.Net`       | NATS client for .NET                 |
| `MessagePack`    | MessagePack serialisation            |
