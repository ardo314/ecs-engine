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
| Control plane      | Protobuf (`ecs.protocol.v1`)            |
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

Component **payloads** are protobuf binary, and so is the control plane that
carries them. The coordinator still treats a payload as opaque bytes — it only
ever checks that the bytes are shaped like the schema they claim to be.

A component's identity is its **protobuf full name** — `movement.v1.Position`,
`nova.v1.CellRef` — which is language-neutral, so a system written in any
language addresses the same type as any other.

Because an empty protobuf message encodes to zero bytes, a batch cannot use
length to signal absence: a missing component is carried by a presence bitmap,
and a zero-length payload is a genuine marker component.

### Two notions of typing

The design turns on keeping two ideas of "type" apart:

```
System typing                 Framework typing
  Position                      ComponentTypeId  (17)
  Velocity                      schema hash      (0x8a74…)
  Health                        FileDescriptorSet
  Inventory                     opaque payloads
```

A system is written against the generated `Position` class and gets full
compile-time safety from it. The coordinator has never compiled `Position` and
never will; it holds a **runtime description** of the type instead. That is what
resolves the apparent contradiction between "the world must be type safe" and
"the world must not know my types beforehand" — and it is why a new component
type, or a whole new domain, needs no coordinator rebuild.

### Schema Registry

Before a system does anything else it declares the schemas it uses:

```
ComponentTypeDeclaration
    logical_name        "movement.v1.Position"
    schema_hash         0x8a74…
    file_descriptor_set <transitively closed>
```

The coordinator recomputes the hash from the descriptors rather than trusting
the one it was sent, binds the name to a **dense integer id**, and answers with
it. Ids are assigned in registration order and are stable for the life of the
world. Everything after the handshake — queries, batches, leases, snapshots — is
expressed in them.

Identity is **exact**. A logical name is bound to one schema hash, and a second
declaration with a different hash is refused rather than reconciled. Compatible
schema evolution is deliberately not modelled: until there is a concrete need
for it, a changed schema is a different type, and saying so loudly beats
guessing. A refusal is fatal on the client, because the alternative is a system
quietly reading something other than what it was compiled against.

The hash is taken over a canonical textual rendering of the message and every
message and enum it transitively references, not over serialised descriptors —
protobuf serialisation is not canonical, so hashing it would make identity
depend on which runtime produced the bytes. Comments, options, file names and
declaration order are excluded; field numbers, names, labels, types, oneof
grouping and the referenced type closure are not. The exact rendering is
specified in `proto/ecs/protocol/v1/schema.proto` and implemented once, in
`protocol/csharp/Ecs.Protocol/SchemaHash.cs`.

Because the world holds descriptors, it can do something a bytes-only store
cannot: **validate a payload against the schema it claims to be** without
compiling the type. The check is structural — the bytes parse, and every field
number the schema knows about carries a wire type the schema permits, nested
messages included. Unknown field numbers are allowed, because proto3 keeps them
and rejecting them would make the world stricter than protobuf itself.

### Component Type

Component types are **entities themselves**. Every registered component type
gets an entity allocated from the same counter as any other entity, carrying
`ecs.v1.ComponentInfo { type_name }` and `ecs.v1.ComponentSchema
{ file_descriptor_set, schema_hash, type_id }`. Everything else on a type entity
is an ordinary user-defined component.

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
option once per component type, resolves each attachment's own message type out
of the declaring file's descriptor pool so it carries the same exact identity as
any other component, and sends them along with the declaration. The coordinator
replays each one as an ordinary `AddComponent` on the type entity, through the
same command path and the same tick phase as any other structural change.

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
    _children = NewQuery().With(Query.Read<ParentRef>());
    _parents  = NewQuery().With(Query.Read<Transform3D>());
}
```

Both queries are populated from the same invocation in the same tick, so this
costs extra **bandwidth**, not an extra round trip. Multi-hop traversal is not:
following a chain of references one level per tick means one tick of latency per
hop. Where a hop is hot, denormalise — cache the resolved value on the entity as
its own component and let one system keep it current.

### Archetype

A unique combination of component types. The world does not store by archetype
today — components are held in a map keyed by (entity, type id) — but query
matching is expressed so that a columnar layout can be introduced underneath it
without the protocol changing.

### System

A function that operates on a **query** — a filtered view of entities and their
components. Each system runs as its own process. A system connects to NATS,
registers the schemas it uses, declares its queries to the coordinator, receives
an invocation carrying a lease and the matching component batches, executes, and
returns its writes under that lease. Multiple instances of the same system can be
launched to spread load via NATS queue groups.

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
            .With(Query.Write<Position>())
            .With(Query.Read<Velocity>());
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

`FlushAsync` waits for the coordinator before publishing, registers any schema
the commands touch, and then submits them out of band. Systems keep their own
`Commands` buffer for structural changes that *are* part of their logic; those
ride back inside the tick's result.

### Query

A declarative description of which component types a system needs, and whether
it needs them mutably or immutably. The SDK turns `Query.Read<Position>()` into
a protocol-level `{ type_id, access }` pair; the coordinator uses those pairs to
compute data dependencies and schedule systems with maximum parallelism, without
ever learning what a component means.

#### Tag joins

A query can select on the type system instead of on concrete types:

```csharp
_settings = NewQuery()
    .With(Query.Read<ControllerRef>())
    .WithAnyTagged<Setting>();
```

`WithAnyTagged<TTag>` matches entities carrying at least one component whose
**type entity** has `TTag` — the join the editor performs manually in two steps,
expressed as one query. The coordinator resolves the tag to concrete type ids
every tick, matches entities against them, ships those batches, and returns the
resolution in `SystemInvocation.tags`, so component types registered later are
picked up without the system changing. The resolved types also count as reads
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
├── proto/                       # Protobuf schemas (a buf module)
│   ├── ecs/v1/                  # Engine-generic: EntityId, ComponentInfo, ComponentSchema
│   ├── ecs/protocol/v1/         # The control plane: schema, query, tick, world
│   ├── movement/v1/
│   ├── nova/v1/
│   └── gen/                     # `buf generate` output, committed
│       ├── csharp/              # Ecs.Protos.csproj
│       └── ts/                  # @ecs/protos npm package (browser and Node)
├── protocol/                    # Hand-written companions to the generated code
│   └── csharp/Ecs.Protocol/     # Schema hashing, batch codecs, validation, subjects
├── editor/                     # One Node process: React UI + API + NATS bridge
│   ├── package.json
│   ├── vite.config.ts
│   ├── index.html
│   └── src/
│       ├── client/             # React + Mantine
│       └── server/             # Hono, protobuf decoding, schema registry
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
cluster. Every payload is a message from `ecs.protocol.v1`.

| Subject                            | Direction               | Payload                   | Purpose                                                          |
| ---------------------------------- | ----------------------- | ------------------------- | ---------------------------------------------------------------- |
| `engine.schema.register`           | Any → Coordinator       | `RegisterSchemasRequest`  | Request/reply: bind component schemas to dense type ids.         |
| `engine.system.register`           | System → Coordinator    | `SystemRegistration`      | System announces itself and its queries. Repeated until invoked. |
| `engine.system.unregister`         | System → Coordinator    | `SystemUnregistration`    | System unregisters on shutdown.                                  |
| `engine.system.invoke.<system>`    | Coordinator → System(s) | `SystemInvocation`        | Hands a system its tick-scoped lease, entity slice and data.     |
| `engine.system.result`             | Systems → Coordinator   | `SystemResult`            | Returns writes and structural commands under that lease.         |
| `engine.system.rejected.<instance>`| Coordinator → System    | `ResultRejected`          | The world refused a result, and why.                             |
| `engine.world.command`             | Any → Coordinator       | `CommandBatch`            | Request/reply: structural commands submitted outside a tick.     |
| `engine.world.query.systems`       | Any → Coordinator       | `QuerySystemsRequest`     | Request/reply: registered systems and computed stages.           |
| `engine.world.query.entities`      | Any → Coordinator       | `QueryEntitiesRequest`    | Request/reply: matching entities, plus the schemas to decode them.|
| `engine.world.watch.subscribe`     | Any → Coordinator       | `WatchRequest`            | Request/reply: register a watch subscription.                    |
| `engine.world.watch.cancel`        | Any → Coordinator       | `WatchCancel`             | Cancels an active watch subscription.                            |
| `engine.world.watch.data.<id>`     | Coordinator → Watcher   | `WatchData`               | Per-tick push to an active watcher.                              |

> All subjects use core NATS. Systems are driven by
> `engine.system.invoke.<system>` rather than a global tick broadcast, so a
> system only wakes when it has matching entities. Instances of the same system
> share a queue group, so exactly one receives each invocation — and therefore
> each lease.

Subject names are written down once, in `Ecs.Protocol.Subjects`. Routing
metadata belongs in subjects and NATS headers, never in a payload.

---

## Tick Lifecycle

```
Coordinator                              System (one process each)
    │                                       │
    │── 0. Synchronisation point:           │
    │      ensure type entities exist,      │
    │      apply every buffered             │
    │      structural command               │
    │── 1. Resolve tag joins                │
    │── 2. Compute execution stages         │
    │                                       │
    │   ┌─── Stage 1 (parallel) ────────┐   │
    │   │ issue a lease per system      │   │
    ├──►│ SystemInvocation ────────────────►│ populate queries
    │   │                               │   │ OnUpdateAsync
    │◄──┤ ◄──────────────────────── SystemResult
    │   │ check lease, apply writes,    │   │
    │   │ buffer structural commands    │   │
    │   └───────────────────────────────┘   │
    │── 3. Retire the stage's leases        │
    │                                       │
    │   ┌─── Stage 2 (parallel) ────────┐   │
    ├──►│ next conflict-free set         │  │
    │   └───────────────────────────────┘   │
    │                                       │
    │── 4. Push watch data                  │
    │── 5. Sleep to hold the tick rate      │
    ▼                                       ▼
```

### Scheduling Algorithm

The scheduler sees a system as two sets of integers and applies one rule:

```
conflict(A, B) = A.writes ∩ B.reads  ≠ ∅
              || A.reads  ∩ B.writes ≠ ∅
              || A.writes ∩ B.writes ≠ ∅
```

so `Read<X>` pairs with `Read<X>` and everything else serialises. Non-conflicting
systems share a stage and run in parallel; conflicting systems are placed in
separate sequential stages with a barrier between them. Because the rule is
expressed over type ids alone, a component type introduced at runtime
participates in scheduling without the coordinator being rebuilt.

### Tick-scoped leases

Systems do not share a process, so there is no borrow checker to lean on. The
world issues an explicit, single-use claim instead:

```
SystemInvocation                  SystemResult
    tick      8291                    tick      8291
    lease_id  …                       lease_id  …
    entities  [...]                   writes    [...]
    writable  [22]                    commands  [...]
    components[...]
```

A result is applied only if **all** of the following hold:

- the lease is still live, and has not already been claimed;
- the tick matches;
- every written type is one the lease grants write access to;
- every written entity is inside the assigned slice;
- the type is registered;
- the payload validates against that type's schema.

Anything else is refused whole rather than partially applied, and the offending
instance is told why on `engine.system.rejected.<instance>`. A stage retires its
leases when it closes, so a worker that stalls through tick 8291 and returns its
writes during 8292 finds its claim gone — the world is not silently corrupted by
stale data.

### Structural changes

Systems never mutate the world's topology while iterating it. `SystemResult`
separates component writes from structural commands: writes land immediately,
commands are buffered and played at the next synchronisation point, all together
and in order. That removes the whole class of problems around invalidated
slices, archetype migration, iteration stability and distributed
synchronisation. Editor edits and seed data take the same path through
`engine.world.command`, so there is one place where the world's shape changes.

### Component batches

Component data travels as `ComponentBatch`, a self-contained column of one type
over a slice of entities. The batch names its own encoding, so the data plane
can change without the control plane moving:

```
BATCH_ENCODING_PROTOBUF        one encoded message per entity + presence bitmap
BATCH_ENCODING_ARROW           reserved — Arrow IPC stream
BATCH_ENCODING_SHARED_MEMORY   reserved
```

Only the protobuf codec is implemented, and it is the right first choice.
Arrow would be a good fit for large slices — it is a language-neutral columnar
format with an IPC representation designed to avoid re-serialising buffers, and
it has implementations in every language an SDK might target — but introducing
it before profiling justifies it would be speculation. What matters now is that
the abstraction exists (`IComponentBatchCodec`), so adopting one later is a codec
registration rather than a protocol change. An encoding a process cannot speak is
refused rather than guessed at.

---

## Editor Integration

The editor is a **single Node process**. It serves the React bundle, exposes the
HTTP API and the WebSocket, and bridges NATS — all from one origin, so the browser
needs no backend URL and there is nothing to inject at container start. The
coordinator has no knowledge of the editor; it only exposes generic NATS
endpoints.

### Decoding components it was never built against

The editor holds generated types only for `ecs.v1` and `ecs.protocol.v1`.
Everything else it learns at runtime: the coordinator sends a `ComponentTypeInfo`
for each bound type — dense id, exact identity and descriptors — which the editor
absorbs into a `protobuf-es` file registry and then uses to decode each payload
to canonical protobuf JSON. A new component type shows up with correct field
names and types without the editor being rebuilt or knowing the domain.

Schemas are sent only when the registry version changes, so a steady world costs
entity data alone.

### Query APIs (request/reply)

- **`engine.world.query.systems`** — returns all registered systems with their
  read/write type ids and computed execution stages.
- **`engine.world.query.entities`** — returns entities with component payloads,
  plus the `ComponentTypeInfo` needed to decode them. Accepts an optional filter
  by logical name (`all_of`, `any_of`). Component type entities are returned by
  the same endpoint, so tools discover the type system through it.

A generic editor for a user-defined contract such as `Setting` therefore
needs no engine support:

1. Query entities with `all_of = ["nova.v1.Setting"]` → the type entities
   carrying that component.
2. Read their `ComponentInfo` → the component type names, and their
   `ComponentSchema` → how to decode instances of them.
3. Query entities with `any_of = [<those type names>]` → the instances.
4. Edit and write back with an `AddComponent` command, which upserts.

### Watch API (subscription)

1. Client sends a `WatchRequest` to `engine.world.watch.subscribe` specifying
   what to include (systems, entities, an optional `EntityFilter`) and a
   `watch_id`.
2. Coordinator replies with a `WatchResponse` containing the `data_subject`
   (`engine.world.watch.data.<watchId>`) to subscribe to.
3. At the end of each tick, the coordinator publishes `WatchData` to that
   subject. Systems, stages and schemas are included only when they change.
4. Client sends `WatchCancel` to `engine.world.watch.cancel` to stop.

The editor uses this to provide:

- **Real-time entity inspection** with deserialized component field values.
- **System schedule view** showing systems grouped by execution stage with
  their read/write component queries.
- **Live tick counter** showing the current simulation tick.

---

## Serialisation

One encoding, all the way down: **Protobuf**.

- **Component payloads.** Schemas live in `proto/`, so the same component type
  is addressable from any language with a protobuf runtime. The coordinator
  never decodes them; it only checks that the bytes are shaped like the schema
  they claim to be.
- **The control plane.** Schema registration, system registration, queries,
  leases, tick messages, commands, errors and metadata are all messages in
  `ecs.protocol.v1`. They are generated for every target language from the same
  `buf` module as the components, so there is no hand-written decoder to keep in
  step and no cross-language wire fixtures to maintain.

A few parts of the contract are algorithms rather than message shapes — the
schema hash, the batch codec, payload validation, the subject names. Those live
in `protocol/csharp/Ecs.Protocol`, referenced by both the coordinator and the
SDK. A schema hash that differs between two processes is a schema hash that does
not work, so there is exactly one implementation of it.

NATS headers carry routing metadata so consumers can filter without
deserialising the payload.

---

## Error Handling & Resilience

| Failure           | Mitigation                                                                 |
| ----------------- | -------------------------------------------------------------------------- |
| System crash      | Its lease expires with the tick; other queue group instances continue.     |
| Slow system       | The stage deadline passes, the lease is retired, and a late result is refused rather than applied to a world that has moved on. |
| Stale write       | Rejected on lease, tick, write permission, entity slice, type and payload. |
| Schema mismatch   | Registration is refused with the bound hash, and the system fails loudly rather than reading the wrong shape. |
| Coordinator crash | State is in memory today; systems and the editor re-register and resume.   |
| NATS disconnect   | NATS.Net reconnects automatically; systems buffer and retry.               |

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
5. **Protobuf descriptors as the source of truth** — One language-neutral
   definition, generated types for every SDK, and a runtime description the world
   can hold so it validates types it was never built against.
6. **Dense type ids below the registry** — The scheduler, storage and lease
   checker deal in integers, so nothing under the registry needs rebuilding when
   a component type appears.
7. **Exact schema identity** — A name binds to one hash. Compatible evolution is
   not modelled until something actually needs it.
8. **Tick-scoped leases** — The distributed equivalent of a borrow check, and the
   thing that stops a slow worker landing stale writes.
9. **Structural changes at a synchronisation point** — No system observes the
   world's shape changing underneath its iteration.
10. **A codec abstraction, not a codec choice** — Protobuf batches now; the
    protocol does not have to move to adopt Arrow later.
11. **Fixed tick loop** — Deterministic simulation.
12. **One editor process** — UI and API share an origin, so there is no CORS, no
    backend URL to configure and one image to ship.

---

## Dependencies

| Package                    | Purpose                                        |
| -------------------------- | ---------------------------------------------- |
| `NATS.Net`                 | NATS client for .NET                           |
| `Google.Protobuf`          | Payload and control-plane serialisation (C#)   |
| `@bufbuild/buf`            | Protobuf linting, formatting and codegen       |
| `@bufbuild/protobuf`       | Payload and control-plane serialisation (TS)   |
| `@nats-io/transport-node`  | NATS client for Node                           |
| `hono` / `@hono/node-server` | Editor HTTP, static serving and WebSocket    |
