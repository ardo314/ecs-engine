import type { QueryDescriptor } from "@ecs/protos/ecs/protocol/v1/query_pb.js";
import type { ComponentTypeDeclaration } from "@ecs/protos/ecs/protocol/v1/schema_pb.js";
import { EntityCommandBuffer } from "./commands.js";
import { EntityQuery } from "./entity-query.js";
import type { SchemaBindings } from "./query-types.js";

/**
 * Base class for ECS systems. Subclass it, declare queries in the constructor, and
 * implement `onUpdate`.
 *
 * A system declares its access up front and gets nothing it did not ask for. That is
 * what lets the coordinator schedule it: two systems that only read the same types run
 * in parallel, anything else is serialised, and neither had to know the other exists.
 */
export abstract class SystemBase {
  private readonly queries: EntityQuery[] = [];
  private queriesFrozen = false;

  /** The registration name. Instances sharing it form one queue group. */
  readonly systemName: string;

  /** Seconds covered by the current tick, as told by the invocation. */
  protected deltaTime = 0;

  /** The tick this invocation's lease is scoped to. */
  protected tickId = 0n;

  /**
   * Structural changes. Applied by the coordinator at its next synchronisation point,
   * never while a system is iterating.
   */
  protected readonly commands = new EntityCommandBuffer();

  /**
   * Pass an explicit name in any build that minifies class names — the default is
   * derived from `constructor.name`, which a bundler is free to mangle.
   */
  protected constructor(name?: string) {
    this.systemName = name ?? deriveName(new.target.name);
  }

  /**
   * Declares a query. Call this in the constructor; it throws once the system has
   * joined a world.
   */
  protected newQuery(): EntityQuery {
    if (this.queriesFrozen) {
      throw new Error(
        `System '${this.systemName}' cannot declare queries after joining a world. ` +
          "Declare them in the constructor.",
      );
    }

    const query = new EntityQuery();
    this.queries.push(query);
    return query;
  }

  /**
   * Called when the system joins a world. May fire more than once on the same
   * instance, so keep it idempotent.
   */
  protected onAdd(): void {}

  /** Called once per tick, over the slice the lease covers. */
  protected abstract onUpdate(): Promise<void> | void;

  /** Called when the system leaves a world. */
  protected onRemove(): void {}

  // ── Internal plumbing, used by SystemRunner ─────────────────

  /** @internal */
  invokeOnAdd(): void {
    this.queriesFrozen = true;
    for (const query of this.queries) query.freeze();
    this.onAdd();
  }

  /** @internal */
  async invokeOnUpdate(tick: bigint, deltaSeconds: number): Promise<void> {
    this.tickId = tick;
    this.deltaTime = deltaSeconds;
    await this.onUpdate();
  }

  /** @internal */
  invokeOnRemove(): void {
    this.onRemove();
  }

  /** @internal */
  getQueries(): readonly EntityQuery[] {
    return this.queries;
  }

  /** @internal */
  getCommands(): EntityCommandBuffer {
    return this.commands;
  }

  /** @internal Every schema this system needs the coordinator to bind before it runs. */
  declarations(): ComponentTypeDeclaration[] {
    return [...this.queries.flatMap((q) => q.declarations()), ...this.commands.schemas];
  }

  /** @internal */
  bindQueries(bindings: SchemaBindings): void {
    for (const query of this.queries) query.bind(bindings);
  }

  /** @internal */
  queryDescriptors(): QueryDescriptor[] {
    return this.queries.map((q) => q.toDescriptor());
  }

  /** @internal */
  readNames(): string[] {
    return [...new Set(this.queries.flatMap((q) => q.readNames()))];
  }

  /** @internal */
  writeNames(): string[] {
    return [...new Set(this.queries.flatMap((q) => q.writeNames()))];
  }
}

function deriveName(className: string): string {
  return className.endsWith("System") && className.length > "System".length
    ? className.slice(0, -"System".length)
    : className;
}
