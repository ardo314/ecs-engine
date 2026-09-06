# ECS Engine Wire Protocol

Normative specification. This document, not any implementation, defines the
protocol. Where an implementation and this document disagree, the implementation
is wrong.

Message **shapes** are defined by the `.proto` files under `proto/ecs/protocol/v1`
and `proto/ecs/v1`, and are not repeated here. This document defines the parts of
the contract that are **algorithms**, which generated code cannot express:

1. [Schema hash](#1-schema-hash) — how a component type's identity is computed
2. [Descriptor sets](#2-descriptor-sets) — how a schema travels
3. [Component batches](#3-component-batches) — how bulk component data is encoded
4. [Subjects](#4-subjects) — where messages are sent
5. [Handshake](#5-handshake) — the order operations must happen in
6. [Encoding footguns](#6-encoding-footguns) — things that bite exactly once

Every implementation MUST pass the conformance vectors in
[`conformance/`](./conformance). See [`README.md`](./README.md) for how to add a
language.

The key words MUST, MUST NOT, SHOULD and MAY are to be interpreted as in
RFC 2119.

---

## 1. Schema hash

A component type's identity is the 64-bit `schema_hash` in
`ecs.protocol.v1.ComponentTypeRef`. The coordinator binds a logical name to
exactly one hash for the lifetime of a world and refuses any other, so an
implementation that computes a different value cannot register a single type.

### 1.1 Why a canonical rendering

The hash is taken over a canonical **text** rendering, not over serialised
descriptors. Protobuf serialisation is not canonical — field ordering and
unknown-field retention both vary by runtime — so hashing descriptor bytes would
make a type's identity depend on which library produced them.

The rendering is defined over `google.protobuf.FileDescriptorProto` and the
messages nested inside it. It MUST NOT be derived from a runtime's reflection
API, because runtimes disagree about how to model a schema:

| | C# | protobuf-es | Python |
| --- | --- | --- | --- |
| Map field | repeated, `MessageType` is the synthetic entry | `fieldKind: "map"`, entry hidden | repeated, entry has `map_entry` |
| Field type | `FieldType.Float` | numeric enum | `TYPE_FLOAT` |
| Synthetic oneof | `OneofDescriptor.IsSynthetic` | not exposed | infer from `proto3_optional` |

The descriptor has one answer for all three. Work there and the disagreements
disappear — in particular a map field needs **no special case**, because the
descriptor already models it as a repeated message field pointing at a nested
entry type.

### 1.2 Scope

Only `syntax = "proto3"` is supported. An implementation MUST reject a type whose
own file, or the file of any type it renders, declares another syntax.

`google/protobuf/descriptor.proto` is proto2 and is present in most descriptor
sets, but no component references its types, so it is never rendered. Editions
and proto2 presence rules are out of scope until something needs them.

### 1.3 Inputs

Computing the hash takes a `FileDescriptorSet` and the fully-qualified name of
the root message. The set MUST satisfy [§2](#2-descriptor-sets).

An implementation MUST resolve type references against that set itself. It MUST
NOT rely on a runtime's descriptor pool, which may resolve names differently or
hide synthetic types.

### 1.4 Grammar

Lines are joined with `\n` (U+000A). There is no trailing separator beyond the
final line's own `\n`. The result is encoded as UTF-8.

```
message <full_name>\n
field <number> <name> <label> <type>[ <type_name>]\n
oneof <index> <name>\n
enum <full_name>\n
value <number> <name>\n
```

- `<full_name>` and `<type_name>` are fully qualified with **no leading dot**.
  `FieldDescriptorProto.type_name` carries a leading dot; strip it.
- `<number>`, `<index>` are rendered in base 10 with no padding and no sign.
  `<number>` for an enum value MAY be negative and is rendered with a leading `-`.
- Single U+0020 between tokens.

### 1.5 Field label

Derived from `label` and `proto3_optional`:

| Condition | `<label>` |
| --- | --- |
| `label == LABEL_REPEATED` | `repeated` |
| `proto3_optional == true` | `optional` |
| otherwise | `singular` |

`optional` and `singular` differ in presence semantics, which changes what a
payload means, so they MUST hash differently.

### 1.6 Field type

`<type>` is the canonical name of `FieldDescriptorProto.Type`, taken from this
table. An implementation MUST use this table rather than its language's enum
formatter.

| # | `<type>` | # | `<type>` |
| --- | --- | --- | --- |
| 1 | `TYPE_DOUBLE` | 10 | `TYPE_GROUP` |
| 2 | `TYPE_FLOAT` | 11 | `TYPE_MESSAGE` |
| 3 | `TYPE_INT64` | 12 | `TYPE_BYTES` |
| 4 | `TYPE_UINT64` | 13 | `TYPE_UINT32` |
| 5 | `TYPE_INT32` | 14 | `TYPE_ENUM` |
| 6 | `TYPE_FIXED64` | 15 | `TYPE_SFIXED32` |
| 7 | `TYPE_FIXED32` | 16 | `TYPE_SFIXED64` |
| 8 | `TYPE_BOOL` | 17 | `TYPE_SINT32` |
| 9 | `TYPE_STRING` | 18 | `TYPE_SINT64` |

`<type_name>` is emitted **only** for `TYPE_MESSAGE`, `TYPE_GROUP` and
`TYPE_ENUM`, separated from `<type>` by a single space. For every other type
nothing follows `<type>`.

### 1.7 Oneofs

proto3 `optional` is implemented by protoc as a **synthetic** oneof containing the
single field. Synthetic oneofs MUST NOT be rendered — the presence they encode is
already carried by [§1.5](#15-field-label), and rendering them would count the
same fact twice.

The set of synthetic oneof indices MUST be derived from the **fields**:

```
synthetic = { f.oneof_index : f in message.field, f.proto3_optional == true }
```

A oneof is rendered if and only if its index is not in that set.

It is tempting to define this the other way round — "a oneof declared by exactly
one field, which has `proto3_optional`" — but that requires knowing whether a
field declares `oneof_index` **at all**, and not every runtime can tell.
`descriptor.proto` is proto2, where `oneof_index` is an optional scalar;
protobuf-es reports it as `0` for fields that belong to no oneof, making a
non-oneof field indistinguishable from a member of oneof `0`. Reading
`oneof_index` only for fields that carry `proto3_optional` sidesteps the question,
because those fields always have it set. protoc guarantees each such field gets
its own dedicated synthetic oneof, so the mapping is exact.

Every other oneof is rendered as `oneof <index> <name>`, where `<index>` is its
position in `DescriptorProto.oneof_decl`. Which fields belong to it is implied by
their order and numbers, which are already rendered.

### 1.8 Reference closure

Starting from the root message, follow `type_name` on every field, transitively,
collecting messages and enums. The closure is a **set**: a type is collected once
however many paths reach it, and mutual recursion MUST terminate.

Map entry types are collected like any other nested message. Nested types that
nothing references are NOT collected.

An implementation MUST fail if a `type_name` cannot be resolved within the set.

### 1.9 Ordering

1. The root message, rendered first. This anchors the digest to the root, so two
   types with identical reference closures still hash differently.
2. Every other collected message, ordered by full name ascending.
3. Every collected enum, ordered by full name ascending.

Within a message, fields are ordered by `number` ascending, then real oneofs by
`index` ascending. Within an enum, values are ordered by `number` ascending, then
by `name` ascending — aliases share a number.

Ordering is **byte-wise over UTF-8**. Protobuf identifiers are restricted to
`[A-Za-z0-9_.]`, so byte-wise, ordinal and code-point ordering all coincide; an
implementation MAY use whichever its language provides, but MUST NOT use a
locale-sensitive comparison.

### 1.10 Digest

```
digest = SHA-256(utf8(rendering))
schema_hash = uint64 from digest[0..8] read big-endian
```

`schema_hash` is transported as `fixed64`, so no varint sign or width ambiguity
arises.

### 1.11 What is deliberately excluded

Comments, options (including custom options such as `ecs.v1.description`), source
file names, declaration order, JSON names, default values, deprecation, and
reserved ranges. None of them change what a payload means. In particular a type's
description can change without changing its identity.

### 1.12 Worked example

```proto
package testing.v1;

message ConformanceLeaf {
  bytes blob = 1;
}
```

renders as, and hashes to, the value pinned in
[`conformance/schema-hash.json`](./conformance/schema-hash.json):

```
message testing.v1.ConformanceLeaf
field 1 blob singular TYPE_BYTES
```

---

## 2. Descriptor sets

`ComponentTypeDeclaration.file_descriptor_set` and
`ComponentTypeInfo.file_descriptor_set` carry a serialised
`google.protobuf.FileDescriptorSet`.

- It MUST be **transitively closed**: the type's own file and every file that
  file imports, directly or indirectly.
- Files MUST be ordered **dependencies first**. Both C#
  (`FileDescriptor.BuildFromByteStrings`) and Python (`DescriptorPool.Add`)
  require this and fail on a set that is merely complete.
- Each file MUST appear at most once.

A consumer building a pool from the set MUST tolerate files it already holds.
Well-known types are the common case: Python's default descriptor pool raises on
re-adding `google/protobuf/*.proto`, so a consumer MUST skip a file it already
has rather than adding it blindly.

---

## 3. Component batches

`ecs.protocol.v1.ComponentBatch` carries one component type over a slice of
entities. `encoding` names the layout, so a producer and a consumer may differ in
which layouts they support without either being rebuilt. A consumer that receives
an encoding it cannot read MUST refuse the batch rather than guess.

`BATCH_ENCODING_UNSPECIFIED` MUST be read as `BATCH_ENCODING_PROTOBUF`. The field
was introduced alongside that codec, so nothing else can be meant.

### 3.1 `BATCH_ENCODING_PROTOBUF`

`entities`, `rows` and `present` are parallel arrays. For index `i`:

- `entities[i]` is the entity.
- `present[i]` is false when the entity does not carry the component.
- `rows[i]` is the encoded message when present, and MUST be ignored otherwise.

`present` MUST be honoured. Length MUST NOT be used to infer absence: an
all-default protobuf message encodes to **zero bytes**, so a zero-length row is a
valid marker component, not a missing one. Implementations that consumed a batch
written before `present` existed MAY treat a missing bitmap entry as present.

`rows` and `present` MUST be at least as long as `entities`; a consumer MUST
process `min(len(entities), len(rows))` elements.

### 3.2 Reserved encodings

`BATCH_ENCODING_ARROW` and `BATCH_ENCODING_SHARED_MEMORY` are reserved. Both
would carry their payload in `opaque` and leave `rows`/`present` empty. Neither
is implemented, and neither may be emitted until it is specified here.

---

## 4. Subjects

All subjects are prefixed `engine.`. Routing metadata belongs in the subject and
in NATS headers, never in a payload.

| Subject | Pattern | Payload → Reply |
| --- | --- | --- |
| `engine.schema.register` | request | `RegisterSchemasRequest` → `RegisterSchemasResponse` |
| `engine.system.register` | publish | `SystemRegistration` |
| `engine.system.unregister` | publish | `SystemUnregistration` |
| `engine.system.invoke.<system>` | publish, queue group `<system>` | `SystemInvocation` |
| `engine.system.result` | publish | `SystemResult` |
| `engine.system.rejected.<instance>` | publish | `ResultRejected` |
| `engine.world.command` | request | `CommandBatch` → `CommandBatchAck` |
| `engine.world.query.systems` | request | `QuerySystemsRequest` → `QuerySystemsResponse` |
| `engine.world.query.entities` | request | `QueryEntitiesRequest` → `QueryEntitiesResponse` |
| `engine.world.watch.subscribe` | request | `WatchRequest` → `WatchResponse` |
| `engine.world.watch.cancel` | publish | `WatchCancel` |
| `engine.world.watch.data.<watchId>` | publish | `WatchData` |

`<system>` is `SystemRegistration.name`. `<instance>` is
`SystemRegistration.instance_id`. `<watchId>` is `WatchRequest.watch_id`.

Instances sharing a system name MUST join a NATS queue group of that name, so
exactly one instance receives each invocation and therefore each lease.

---

## 5. Handshake

Order is part of the contract. A client MUST:

1. **Register schemas** on `engine.schema.register` for every component type it
   will name, before naming any of them. The response binds each logical name to
   a `type_id`.
2. **Bind** its queries, batches and commands to those ids. A `type_id` is
   assigned by the coordinator and is meaningless until step 1 has returned.
3. **Register itself** on `engine.system.register`, repeating until its first
   invocation arrives. The coordinator holds no durable system list, so a system
   that starts before the coordinator must keep announcing itself.

A rejection in step 1 is fatal and MUST NOT be retried with the same schema. The
alternative is a client silently reading or writing a type other than the one it
was built against.

A client that introduces a type later — because a command adds a component it has
not used before — MUST return to step 1 for that type before sending the command.

### 5.1 Leases

`SystemInvocation` grants a claim over a tick, an entity slice and a set of
writable type ids. `SystemResult` MUST quote `tick` and `lease_id` back
unchanged. The coordinator applies a result only if the lease is live and
unclaimed, the tick matches, every written type is writable under the lease,
every written entity is inside the slice, every type is registered, and every
payload validates against its schema. Otherwise the result is refused **whole**
and the reason is published to `engine.system.rejected.<instance>`.

A lease is single-use, and a stage retires its leases when it closes. A client
MUST NOT assume a late result will be applied.

Structural commands returned in `SystemResult.commands` are not covered by the
lease's entity slice; they are queued and applied at the coordinator's next
synchronisation point.

---

## 6. Encoding footguns

Each of these has cost someone an afternoon.

- **An empty protobuf message is zero bytes.** NATS delivers a zero-byte payload
  as *no payload*, which surfaces as `null` in several clients. Parse replies as
  `Parse(data ?? empty)`. `CommandBatchAck` and `QuerySystemsRequest` hit this on
  every call.
- **`uint64` exceeds JavaScript's safe integer range.** Entity ids, ticks and
  `schema_hash` are all 64-bit. TypeScript implementations MUST carry them as
  `bigint` and MUST NOT round-trip them through `number`. Serialising a hash to
  JSON MUST use a string.
- **`schema_hash` is `fixed64`, not `uint64`.** Deliberate: no varint width or
  sign ambiguity, and a fixed cost for a value that is uniformly distributed.
- **Field names that collide with generated members.** Protobuf's C# generator
  renames a field called `types` to `Types_`. Prefer `component_types`.
- **Presence on descriptor fields is not portable.** `descriptor.proto` is proto2,
  so `oneof_index` is an optional scalar. C# exposes `HasOneofIndex`; protobuf-es
  reports `0` whether the field is in oneof `0` or in no oneof at all. Never make
  the canonical form depend on distinguishing those. See
  [§1.7](#17-oneofs).
- **Descriptor sets are not interchangeable with descriptor pools.** See
  [§2](#2-descriptor-sets).
