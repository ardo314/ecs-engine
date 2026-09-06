# Protocol runtime

The wire contract has two halves.

**Message shapes** live in `proto/` and are generated. Nothing here duplicates
them.

**Algorithms** — schema hashing, batch encoding, subject names — cannot be
generated from a schema, so each language implements them itself. They are
specified once in [`SPEC.md`](./SPEC.md) and pinned by the vectors in
[`conformance/`](./conformance).

```
protocol/
├── SPEC.md            normative; implementations follow it, not each other
├── conformance/       golden vectors every implementation must reproduce
├── csharp/            Ecs.Protocol  — used by the coordinator and the C# SDK
└── ts/                @ecs/protocol — used by the editor
```

## Why vectors rather than a shared library

A schema hash that differs between two processes is a schema hash that does not
work: the coordinator binds a logical name to exactly one hash, so an
implementation that is one byte out cannot register a single component type.

Sharing one implementation across languages is not an option — the algorithm has
to run inside each SDK. So the contract is enforced from outside instead: every
implementation reproduces the same vectors, and CI fails if the vectors change
without being reviewed.

The vectors carry the full canonical rendering alongside each hash, so a
mismatch shows up as a readable diff rather than two different 64-bit numbers.

## Adding a language

1. Read [`SPEC.md`](./SPEC.md). Do not read another implementation first — if the
   spec is not enough on its own, that is a bug in the spec.
2. Implement the canonical rendering over `FileDescriptorProto`. Resist the urge
   to use your runtime's reflection API; §1.1 lists the three places that
   silently disagree.
3. Run the vectors in [`conformance/schema-hash.json`](./conformance/schema-hash.json).
   Compare `canonical` before comparing `schemaHash`.
4. Implement subjects (§4) and the protobuf batch codec (§3.1).
5. Wire the conformance suite into CI.

Changing the canonical form is a **breaking protocol change**: every
implementation and every stored `ComponentSchema` changes with it. Regenerate the
vectors deliberately, in their own commit.
