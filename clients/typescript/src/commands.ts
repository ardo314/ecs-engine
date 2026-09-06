import { create, toBinary } from "@bufbuild/protobuf";
import type { DescMessage, MessageShape } from "@bufbuild/protobuf";
import type { ComponentTypeDeclaration } from "@ecs/protos/ecs/protocol/v1/schema_pb.js";
import {
  CommandTargetSchema,
  StructuralCommandSchema,
  type CommandTarget,
  type StructuralCommand,
} from "@ecs/protos/ecs/protocol/v1/tick_pb.js";
import { componentType } from "./component-type.js";

/**
 * Builds the target of a structural command: a concrete entity, or the entity that
 * represents a component type.
 */
export const Target = {
  of(entity: bigint): CommandTarget {
    return create(CommandTargetSchema, { target: { case: "entity", value: entity } });
  },

  /**
   * The entity representing a component type. Addressed by name rather than id because
   * a system may describe a type in the same breath as it registers it.
   */
  ofComponentType(schema: DescMessage): CommandTarget {
    return create(CommandTargetSchema, {
      target: { case: "componentType", value: schema.typeName },
    });
  },
} as const;

/**
 * Buffers structural changes for the coordinator to apply at its next synchronisation
 * point.
 *
 * Systems never mutate the world's topology while iterating it. Spawns, despawns and
 * component add/remove go in here and ride back with the tick's result; the world plays
 * them all at one deterministic point.
 *
 * The buffer also collects the schemas it touches, so a type introduced by a command is
 * registered before the command that needs it is sent.
 */
export class EntityCommandBuffer {
  private buffered: StructuralCommand[] = [];
  private readonly declared = new Map<string, ComponentTypeDeclaration>();

  get commands(): readonly StructuralCommand[] {
    return this.buffered;
  }

  /** Schemas referenced by the buffered commands, for the registration handshake. */
  get schemas(): ComponentTypeDeclaration[] {
    return [...this.declared.values()];
  }

  get hasPendingCommands(): boolean {
    return this.buffered.length > 0;
  }

  createEntity(...components: ComponentInstance[]): void {
    const values = components.map(({ schema, value }) => {
      const type = componentType(schema);
      this.declare(type.declaration);
      return type.value(value);
    });

    this.buffered.push(
      create(StructuralCommandSchema, {
        command: { case: "spawn", value: { components: values } },
      }),
    );
  }

  destroyEntity(entity: bigint): void {
    this.buffered.push(
      create(StructuralCommandSchema, {
        command: { case: "despawn", value: { entity } },
      }),
    );
  }

  addComponent<Desc extends DescMessage>(
    target: CommandTarget | bigint,
    schema: Desc,
    component: MessageShape<Desc>,
  ): void {
    const type = componentType(schema);
    this.declare(type.declaration);

    this.buffered.push(
      create(StructuralCommandSchema, {
        command: {
          case: "add",
          value: { target: resolve(target), component: type.value(component) },
        },
      }),
    );
  }

  removeComponent(target: CommandTarget | bigint, schema: DescMessage): void {
    const type = componentType(schema);
    this.declare(type.declaration);

    this.buffered.push(
      create(StructuralCommandSchema, {
        command: {
          case: "remove",
          value: { target: resolve(target), type: type.ref },
        },
      }),
    );
  }

  /**
   * Records a type's schema without buffering a command, so a type used only in a query
   * still reaches the coordinator's registry.
   */
  declare(declaration: ComponentTypeDeclaration): void {
    const name = declaration.type?.logicalName;
    if (name !== undefined && !this.declared.has(name)) this.declared.set(name, declaration);
  }

  /** Takes the buffered commands, leaving the buffer empty. Schemas survive. */
  drain(): StructuralCommand[] {
    const drained = this.buffered;
    this.buffered = [];
    return drained;
  }
}

/** A component value paired with the schema that describes it. */
export interface ComponentInstance<Desc extends DescMessage = DescMessage> {
  schema: Desc;
  value: MessageShape<Desc>;
}

/** Pairs a value with its schema, for APIs that take heterogeneous components. */
export function component<Desc extends DescMessage>(
  schema: Desc,
  value: MessageShape<Desc>,
): ComponentInstance<Desc> {
  return { schema, value };
}

function resolve(target: CommandTarget | bigint): CommandTarget {
  return typeof target === "bigint" ? Target.of(target) : target;
}

export { toBinary };
