import {
  createFileRegistry,
  createRegistry,
  fromBinary,
  toJson,
  type DescMessage,
  type JsonValue,
  type Registry,
} from "@bufbuild/protobuf";
import {
  FileDescriptorSetSchema,
  type FileDescriptorProto,
} from "@bufbuild/protobuf/wkt";
import { file_ecs_v1_component } from "@ecs/protos/ecs/v1/component_pb.js";
import { file_ecs_v1_entity } from "@ecs/protos/ecs/v1/entity_pb.js";
import { file_ecs_v1_relations } from "@ecs/protos/ecs/v1/relations_pb.js";
import type { ComponentTypeRef } from "@ecs/protos/ecs/protocol/v1/schema_pb.js";
import type { ComponentTypeInfo } from "@ecs/protos/ecs/protocol/v1/world_pb.js";

// The engine's own component types are always decodable, even before any system has
// registered a schema.
const BUILT_IN = [file_ecs_v1_component, file_ecs_v1_entity, file_ecs_v1_relations];

/**
 * The editor's mirror of the world's schema registry.
 *
 * The coordinator sends a `ComponentTypeInfo` for every type it has bound: the dense
 * id used on the wire, the logical name, and the descriptors needed to decode it. The
 * editor is built against none of the domain component types and can still render all
 * of them — which is the whole reason the world keeps descriptors rather than compiled
 * types.
 */
export class SchemaRegistry {
  private readonly files = new Map<string, FileDescriptorProto>();
  private readonly namesById = new Map<number, string>();
  private readonly refsByName = new Map<string, ComponentTypeRef>();
  private registry: Registry = createRegistry(...BUILT_IN);
  private readonly messages = new Map<string, DescMessage | null>();

  /** Absorbs a component type the world has bound. Returns true if anything is new. */
  add(info: ComponentTypeInfo): boolean {
    const name = info.type?.logicalName;
    if (name === undefined || name === "") return false;

    const known = this.namesById.get(info.typeId) === name;
    this.namesById.set(info.typeId, name);
    if (info.type !== undefined) this.refsByName.set(name, info.type);

    let changed = false;
    if (info.fileDescriptorSet.length > 0) {
      const set = fromBinary(FileDescriptorSetSchema, info.fileDescriptorSet);
      for (const file of set.file) {
        if (this.files.has(file.name)) continue;
        this.files.set(file.name, file);
        changed = true;
      }
    }

    if (changed) this.rebuild();
    return changed || !known;
  }

  /** The logical name bound to a wire type id. */
  nameOf(typeId: number): string {
    return this.namesById.get(typeId) ?? `type:${typeId}`;
  }

  /**
   * The exact identity the coordinator will accept for this type. Commands must quote
   * it, so a client that has not seen the type cannot address it at all.
   */
  refOf(typeName: string): ComponentTypeRef | undefined {
    return this.refsByName.get(typeName);
  }

  has(typeName: string): boolean {
    return this.lookup(typeName) !== null;
  }

  /** Canonical protobuf JSON for a payload, or null if the type is not decodable. */
  decode(typeName: string, data: Uint8Array): JsonValue | null {
    const desc = this.lookup(typeName);
    if (desc === null) return null;

    try {
      return toJson(desc, fromBinary(desc, data), {
        alwaysEmitImplicit: true,
        registry: this.registry,
      });
    } catch {
      return null;
    }
  }

  decodeById(typeId: number, data: Uint8Array): JsonValue | null {
    return this.decode(this.nameOf(typeId), data);
  }

  private lookup(typeName: string): DescMessage | null {
    const cached = this.messages.get(typeName);
    if (cached !== undefined) return cached;

    const desc = this.registry.getMessage(typeName) ?? null;
    this.messages.set(typeName, desc);
    return desc;
  }

  private rebuild(): void {
    const described = createFileRegistry({
      $typeName: "google.protobuf.FileDescriptorSet",
      file: [...this.files.values()],
    });
    this.registry = createRegistry(described, ...BUILT_IN);
    this.messages.clear();
  }
}
