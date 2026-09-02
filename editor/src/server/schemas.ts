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
import {
  ComponentSchemaSchema,
  file_ecs_v1_component,
} from "@ecs/protos/ecs/v1/component_pb.js";
import { file_ecs_v1_entity } from "@ecs/protos/ecs/v1/entity_pb.js";
import { file_ecs_v1_relations } from "@ecs/protos/ecs/v1/relations_pb.js";

// The engine's own component types are always decodable, even before a system
// has described anything.
const BUILT_IN = [file_ecs_v1_component, file_ecs_v1_entity, file_ecs_v1_relations];

/**
 * Decodes component payloads using only the descriptors the world tells us about.
 *
 * Systems attach an `ecs.v1.ComponentSchema` to each component type entity, so the
 * editor can render component types it was never built against.
 */
export class SchemaRegistry {
  private readonly files = new Map<string, FileDescriptorProto>();
  private registry: Registry = createRegistry(...BUILT_IN);
  private readonly messages = new Map<string, DescMessage | null>();

  /** Absorbs a serialised `ecs.v1.ComponentSchema` payload. Returns true if anything is new. */
  add(componentSchemaBytes: Uint8Array): boolean {
    const schema = fromBinary(ComponentSchemaSchema, componentSchemaBytes);
    const set = fromBinary(FileDescriptorSetSchema, schema.fileDescriptorSet);

    let changed = false;
    for (const file of set.file) {
      if (this.files.has(file.name)) continue;
      this.files.set(file.name, file);
      changed = true;
    }

    if (changed) this.rebuild();
    return changed;
  }

  has(typeName: string): boolean {
    return this.lookup(typeName) !== null;
  }

  /** Canonical protobuf JSON for a component payload, or null if the type is unknown. */
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
