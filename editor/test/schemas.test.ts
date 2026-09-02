import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import { SchemaRegistry } from "../src/server/schemas";

function fixture(name: string): Uint8Array {
  return new Uint8Array(readFileSync(fileURLToPath(new URL(`./fixtures/${name}`, import.meta.url))));
}

describe("SchemaRegistry", () => {
  it("cannot decode a type it has not been told about", () => {
    const registry = new SchemaRegistry();

    expect(registry.has("movement.v1.Position")).toBe(false);
    expect(registry.decode("movement.v1.Position", fixture("position.bin"))).toBeNull();
  });

  it("decodes a component after absorbing the schema the world published", () => {
    const registry = new SchemaRegistry();

    expect(registry.add(fixture("positionSchema.bin"))).toBe(true);

    expect(registry.decode("movement.v1.Position", fixture("position.bin"))).toEqual({
      x: 1.5,
      y: -2,
      z: 0,
    });
  });

  it("knows the engine's own component types without being told", () => {
    const registry = new SchemaRegistry();

    expect(registry.has("ecs.v1.ComponentInfo")).toBe(true);
    expect(registry.has("ecs.v1.ComponentSchema")).toBe(true);
  });

  it("ignores a schema it already holds", () => {
    const registry = new SchemaRegistry();
    registry.add(fixture("positionSchema.bin"));

    expect(registry.add(fixture("positionSchema.bin"))).toBe(false);
  });
});
