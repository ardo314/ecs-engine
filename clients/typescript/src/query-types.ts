import type { DescMessage } from "@bufbuild/protobuf";
import { Access } from "@ecs/protos/ecs/protocol/v1/query_pb.js";
import { componentType, type ComponentType } from "./component-type.js";

/**
 * A component type a query touches, and how.
 *
 * The bridge between the two notions of typing: authoring code writes
 * `Query.read(PositionSchema)` and keeps compile-time safety from the generated type,
 * while the wire carries only `{ type_id, access }`. Nothing requires the world to have
 * compiled `Position`.
 */
export interface ComponentAccess<Desc extends DescMessage = DescMessage> {
  readonly type: ComponentType<Desc>;
  readonly access: Access;
}

export const Query = {
  /** Read-only. Two systems reading the same type run in parallel. */
  read<Desc extends DescMessage>(schema: Desc): ComponentAccess<Desc> {
    return { type: componentType(schema), access: Access.READ };
  },

  /**
   * Read-write. Conflicts with any other system reading or writing the type, so the
   * scheduler puts them in different stages.
   */
  write<Desc extends DescMessage>(schema: Desc): ComponentAccess<Desc> {
    return { type: componentType(schema), access: Access.WRITE };
  },
} as const;

/**
 * The dense type ids the coordinator bound this process's schemas to.
 *
 * Ids are assigned by the world, so they are meaningless until the registration
 * handshake returns. Everything after that point is expressed in them.
 */
export class SchemaBindings {
  private readonly byName = new Map<string, number>();
  private readonly byId = new Map<number, string>();

  add(logicalName: string, typeId: number): void {
    this.byName.set(logicalName, typeId);
    this.byId.set(typeId, logicalName);
  }

  tryGetId(logicalName: string): number | undefined {
    return this.byName.get(logicalName);
  }

  require(logicalName: string): number {
    const typeId = this.byName.get(logicalName);
    if (typeId === undefined) {
      throw new Error(
        `Component type '${logicalName}' has no id — its schema was never registered. ` +
          "Declare it in a query, or add a component of that type through a command buffer.",
      );
    }
    return typeId;
  }

  nameOf(typeId: number): string {
    return this.byId.get(typeId) ?? `<unbound:${typeId}>`;
  }

  get size(): number {
    return this.byName.size;
  }
}
