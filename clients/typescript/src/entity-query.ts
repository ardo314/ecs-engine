import { create, fromBinary, toBinary } from "@bufbuild/protobuf";
import type { DescMessage, MessageShape } from "@bufbuild/protobuf";
import { Access, QueryDescriptorSchema } from "@ecs/protos/ecs/protocol/v1/query_pb.js";
import type { QueryDescriptor } from "@ecs/protos/ecs/protocol/v1/query_pb.js";
import type { ComponentBatch, SystemInvocation } from "@ecs/protos/ecs/protocol/v1/tick_pb.js";
import type { ComponentTypeDeclaration } from "@ecs/protos/ecs/protocol/v1/schema_pb.js";
import { componentType } from "./component-type.js";
import { encodeBatch, type ComponentColumn } from "./protocol/index.js";
import { Query, SchemaBindings, type ComponentAccess } from "./query-types.js";

/**
 * A typed view over the entities a system was leased for this tick.
 *
 * Declared in the system constructor, bound to dense type ids during the registration
 * handshake, then repopulated each tick from the invocation's component batches.
 *
 * A system cannot look up an arbitrary entity: it sees exactly the slice its lease
 * covers. To dereference a reference component, declare a second query matching the
 * target entities and index it — both are filled from the same invocation, so that
 * costs bandwidth rather than a round trip.
 */
export class EntityQuery {
  private readonly required: ComponentAccess[] = [];
  private readonly optional: ComponentAccess[] = [];
  private readonly excluded: ComponentAccess[] = [];
  private readonly tags: ComponentAccess[] = [];
  private frozen = false;

  private bindings = new SchemaBindings();
  private requiredIds: number[] = [];
  private optionalIds: number[] = [];
  private excludedIds: number[] = [];
  private tagIds: number[] = [];
  private declaredTypes = new Set<number>();
  private writableTypes = new Set<number>();

  private data = new Map<number, Map<bigint, Uint8Array>>();
  private resolvedTags = new Map<number, number[]>();
  private matched: bigint[] = [];
  private mutations = new Map<number, Map<bigint, Uint8Array>>();

  // ── Declaration ─────────────────────────────────────────────

  /** Adds a required component. An entity must have it to match. */
  with(access: ComponentAccess): this {
    this.throwIfFrozen();
    this.required.push(access);
    return this;
  }

  /** Adds optional components. An entity must have at least one to match. */
  withAny(...accesses: ComponentAccess[]): this {
    this.throwIfFrozen();
    this.optional.push(...accesses);
    return this;
  }

  /** Excludes entities carrying the component. */
  without(schema: DescMessage): this {
    this.throwIfFrozen();
    this.excluded.push(Query.read(schema));
    return this;
  }

  /**
   * Joins through the type system: an entity matches when it carries at least one
   * component whose *type entity* has this tag. The matching types are resolved by the
   * coordinator every tick, so a type registered later is picked up without this query
   * changing. Tag joins are read-only.
   */
  withAnyTagged(schema: DescMessage): this {
    this.throwIfFrozen();
    this.tags.push(Query.read(schema));
    return this;
  }

  // ── Binding ─────────────────────────────────────────────────

  /** Every schema this query needs the coordinator to know about. */
  declarations(): ComponentTypeDeclaration[] {
    return [...this.required, ...this.optional, ...this.excluded, ...this.tags].map(
      (a) => a.type.declaration,
    );
  }

  freeze(): void {
    this.frozen = true;
  }

  bind(bindings: SchemaBindings): void {
    this.bindings = bindings;

    this.requiredIds = this.required.map((a) => bindings.require(a.type.name));
    this.optionalIds = this.optional.map((a) => bindings.require(a.type.name));
    this.excludedIds = this.excluded.map((a) => bindings.require(a.type.name));
    this.tagIds = this.tags.map((a) => bindings.require(a.type.name));

    this.declaredTypes = new Set([...this.requiredIds, ...this.optionalIds]);
    this.writableTypes = new Set(
      [...this.required, ...this.optional]
        .filter((a) => a.access === Access.WRITE)
        .map((a) => bindings.require(a.type.name)),
    );
  }

  toDescriptor(): QueryDescriptor {
    return create(QueryDescriptorSchema, {
      required: this.required.map((a, i) => ({
        typeId: this.requiredIds[i]!,
        access: a.access,
      })),
      optional: this.optional.map((a, i) => ({
        typeId: this.optionalIds[i]!,
        access: a.access,
      })),
      excluded: this.excludedIds,
      tagged: this.tagIds.map((id) => ({ tagTypeId: id })),
    });
  }

  readNames(): string[] {
    return [...this.required, ...this.optional]
      .filter((a) => a.access !== Access.WRITE)
      .map((a) => a.type.name);
  }

  writeNames(): string[] {
    return [...this.required, ...this.optional]
      .filter((a) => a.access === Access.WRITE)
      .map((a) => a.type.name);
  }

  // ── Per-tick population ─────────────────────────────────────

  populate(columns: Map<number, ComponentColumn>, invocation: SystemInvocation): void {
    this.data = new Map();
    this.matched = [];
    this.mutations = new Map();
    this.resolvedTags = new Map();

    const taggedTypes = new Set<number>();
    for (const tagId of this.tagIds) {
      const resolved = invocation.tags.find((t) => t.tagTypeId === tagId);
      const types = resolved?.typeIds ?? [];
      this.resolvedTags.set(tagId, [...types]);
      for (const type of types) taggedTypes.add(type);
    }

    for (const [typeId, column] of columns) {
      if (
        !this.declaredTypes.has(typeId) &&
        !this.excludedIds.includes(typeId) &&
        !taggedTypes.has(typeId)
      ) {
        continue;
      }

      const byEntity = new Map<bigint, Uint8Array>();
      const count = Math.min(column.entities.length, column.rows.length);
      for (let i = 0; i < count; i++) {
        const row = column.rows[i];
        if (row !== null && row !== undefined) byEntity.set(column.entities[i]!, row);
      }
      this.data.set(typeId, byEntity);
    }

    let candidates: Set<bigint> | undefined;

    for (const typeId of this.requiredIds) {
      const byEntity = this.data.get(typeId);
      if (byEntity === undefined) return;

      candidates =
        candidates === undefined
          ? new Set(byEntity.keys())
          : intersect(candidates, new Set(byEntity.keys()));
    }

    for (const tagId of this.tagIds) {
      const withTag = new Set<bigint>();
      for (const typeId of this.resolvedTags.get(tagId) ?? []) {
        for (const entity of this.data.get(typeId)?.keys() ?? []) withTag.add(entity);
      }
      candidates = candidates === undefined ? withTag : intersect(candidates, withTag);
    }

    if (candidates === undefined) return;

    if (this.optionalIds.length > 0) {
      for (const entity of [...candidates]) {
        const any = this.optionalIds.some((id) => this.data.get(id)?.has(entity) === true);
        if (!any) candidates.delete(entity);
      }
    }

    for (const typeId of this.excludedIds) {
      for (const entity of this.data.get(typeId)?.keys() ?? []) candidates.delete(entity);
    }

    this.matched = [...candidates];
  }

  // ── Data access ─────────────────────────────────────────────

  /** Entities matching this query for the current tick. */
  get entities(): readonly bigint[] {
    return this.matched;
  }

  get<Desc extends DescMessage>(entity: bigint, schema: Desc): MessageShape<Desc> {
    const value = this.tryGet(entity, schema);
    if (value === undefined) {
      throw new Error(`Entity ${entity} has no ${schema.typeName} in this query.`);
    }
    return value;
  }

  tryGet<Desc extends DescMessage>(
    entity: bigint,
    schema: Desc,
  ): MessageShape<Desc> | undefined {
    const typeId = this.bindings.tryGetId(schema.typeName);
    if (typeId === undefined) return undefined;

    const payload = this.data.get(typeId)?.get(entity);
    return payload === undefined ? undefined : fromBinary(schema, payload);
  }

  has(entity: bigint, schema: DescMessage): boolean {
    const typeId = this.bindings.tryGetId(schema.typeName);
    return typeId === undefined ? false : this.data.get(typeId)?.has(entity) === true;
  }

  /**
   * Buffers a component write. Throws unless the type was declared with `Query.write` —
   * the client half of the borrow check, so a mistake surfaces here rather than as a
   * rejected result a network hop later.
   */
  set<Desc extends DescMessage>(
    entity: bigint,
    schema: Desc,
    component: MessageShape<Desc>,
  ): void {
    const typeId = this.bindings.require(schema.typeName);

    if (!this.writableTypes.has(typeId)) {
      throw new Error(`Cannot write ${schema.typeName}: this query declared it read-only.`);
    }

    let byEntity = this.mutations.get(typeId);
    if (byEntity === undefined) {
      byEntity = new Map();
      this.mutations.set(typeId, byEntity);
    }
    byEntity.set(entity, toBinary(schema, component));
  }

  // ── Tag joins ───────────────────────────────────────────────

  /** The component type names carrying the tag this tick. */
  taggedTypeNames(schema: DescMessage): string[] {
    const tagId = this.bindings.tryGetId(schema.typeName);
    if (tagId === undefined) return [];

    return (this.resolvedTags.get(tagId) ?? []).map((id) => this.bindings.nameOf(id));
  }

  /**
   * The components on an entity whose type carries the tag. Payloads stay raw because
   * the concrete types are only known at runtime.
   */
  *getTagged(entity: bigint, schema: DescMessage): Generator<TaggedComponent> {
    const tagId = this.bindings.tryGetId(schema.typeName);
    if (tagId === undefined) return;

    for (const typeId of this.resolvedTags.get(tagId) ?? []) {
      const payload = this.data.get(typeId)?.get(entity);
      if (payload !== undefined) {
        yield new TaggedComponent(this.bindings.nameOf(typeId), payload);
      }
    }
  }

  // ── Flush ───────────────────────────────────────────────────

  flushWrites(): ComponentBatch[] {
    const batches: ComponentBatch[] = [];

    for (const [typeId, byEntity] of this.mutations) {
      if (byEntity.size === 0) continue;

      batches.push(
        encodeBatch({
          typeId,
          entities: [...byEntity.keys()],
          rows: [...byEntity.values()],
        }),
      );
    }

    this.mutations = new Map();
    return batches;
  }

  private throwIfFrozen(): void {
    if (this.frozen) {
      throw new Error(
        "Queries must be declared in the system constructor, before it joins a world.",
      );
    }
  }
}

/** A component reached through a tag join, whose concrete type is only known at runtime. */
export class TaggedComponent {
  constructor(
    readonly typeName: string,
    readonly payload: Uint8Array,
  ) {}

  /** Decodes the payload, or returns undefined if it is a different type. */
  as<Desc extends DescMessage>(schema: Desc): MessageShape<Desc> | undefined {
    if (this.typeName !== schema.typeName) return undefined;
    return fromBinary(schema, this.payload);
  }
}

function intersect(a: Set<bigint>, b: Set<bigint>): Set<bigint> {
  const result = new Set<bigint>();
  for (const value of a) {
    if (b.has(value)) result.add(value);
  }
  return result;
}

export { componentType };
