import { create, toBinary } from "@bufbuild/protobuf";
import { FileDescriptorSetSchema } from "@bufbuild/protobuf/wkt";
import { describe, expect, it } from "vitest";
import { ComponentTypeInfoSchema } from "@ecs/protos/ecs/protocol/v1/world_pb.js";
import {
  file_movement_v1_movement,
  PositionSchema,
} from "@ecs/protos/movement/v1/movement_pb.js";
import { SchemaRegistry } from "@ecs/client";

/** What the coordinator sends for a component type it has bound. */
function positionType(typeId: number) {
  const set = create(FileDescriptorSetSchema, { file: [file_movement_v1_movement.proto] });

  return create(ComponentTypeInfoSchema, {
    typeId,
    type: { logicalName: "movement.v1.Position", schemaHash: 0x8a74n },
    fileDescriptorSet: toBinary(FileDescriptorSetSchema, set),
  });
}

const position = toBinary(PositionSchema, create(PositionSchema, { x: 1.5, y: -2, z: 0 }));

describe("SchemaRegistry", () => {
  it("cannot decode a type it has not been told about", () => {
    const registry = new SchemaRegistry();

    expect(registry.has("movement.v1.Position")).toBe(false);
    expect(registry.decode("movement.v1.Position", position)).toBeNull();
  });

  it("decodes a component after absorbing the schema the world published", () => {
    const registry = new SchemaRegistry();

    expect(registry.add(positionType(17))).toBe(true);

    expect(registry.decode("movement.v1.Position", position)).toEqual({
      x: 1.5,
      y: -2,
      z: 0,
    });
  });

  it("decodes by the dense type id the world assigned", () => {
    const registry = new SchemaRegistry();
    registry.add(positionType(17));

    expect(registry.nameOf(17)).toBe("movement.v1.Position");
    expect(registry.decodeById(17, position)).toEqual({ x: 1.5, y: -2, z: 0 });
  });

  it("names an id it has never seen without throwing", () => {
    expect(new SchemaRegistry().nameOf(99)).toBe("type:99");
  });

  it("knows the engine's own component types without being told", () => {
    const registry = new SchemaRegistry();

    expect(registry.has("ecs.v1.ComponentInfo")).toBe(true);
    expect(registry.has("ecs.v1.ComponentSchema")).toBe(true);
  });

  it("ignores a type it already holds", () => {
    const registry = new SchemaRegistry();
    registry.add(positionType(17));

    expect(registry.add(positionType(17))).toBe(false);
  });
});
