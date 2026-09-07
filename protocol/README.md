# Protocol

The wire contract has three parts, and only one of them lives here.

**Message shapes** are in `proto/` and are generated. **Implementations** live with the
things that use them — the coordinator has one, each client SDK has one, and none of
them share code:

```
engine/Engine/Protocol/            the coordinator's implementation
clients/csharp/Client/Protocol/    the C# SDK's implementation
clients/typescript/src/protocol/   the TypeScript SDK's implementation
```

**This directory holds what binds them together:**

```
protocol/
├── SPEC.md            normative; implementations follow it, not each other
└── conformance/       vectors every implementation must reproduce
```

## Why implementations are not shared

Across languages there is no choice: the algorithm runs inside each process, and a Rust
system cannot link a C# library.

Within a language it is a deliberate choice. The coordinator and the C# SDK could share
a project, but sharing is not what keeps them in agreement — the spec and the vectors
are, which is exactly how the TypeScript implementation stays correct while sharing
nothing. Once the vectors carry that weight, a shared library only blurs who owns what,
and it invites server-only code into a library the client ships.

So each side owns its copy, and each side proves itself against the same file.

## Why vectors

A schema hash that differs between two processes is a schema hash that does not work:
the coordinator binds a logical name to exactly one hash, so an implementation that is
one byte out cannot register a single component type.

There is no way to check that by inspection, so it is checked by construction. Every
implementation reproduces `conformance/schema-hash.json`, and CI fails if the file
changes without review. The vectors carry the full canonical rendering alongside each
hash, so a mismatch is a readable diff rather than two 64-bit numbers.

This is not theoretical. Writing the TypeScript implementation from the spec
immediately exposed a bug in the spec — see [`SPEC.md`](./SPEC.md) §1.7.

## Adding a language

1. Read [`SPEC.md`](./SPEC.md). Do not read another implementation first — if the spec
   is not enough on its own, that is a bug in the spec.
2. Implement the canonical rendering over `FileDescriptorProto`. Resist your runtime's
   reflection API; §1.1 lists the three places runtimes silently disagree.
3. Run [`conformance/schema-hash.json`](./conformance/schema-hash.json). Compare
   `canonical` before comparing `schemaHash`.
4. Implement subjects (§4) and the protobuf batch codec (§3.1). Skip payload validation
   (§5.2) unless you are writing a coordinator.
5. Wire the conformance suite into CI.

Changing the canonical form is a **breaking protocol change**: every implementation and
every stored `ComponentSchema` changes with it. Regenerate with
`ECS_WRITE_CONFORMANCE_VECTORS=1 dotnet test engine/Engine.Tests`, in its own commit.
