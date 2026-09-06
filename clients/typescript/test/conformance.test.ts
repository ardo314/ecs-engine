import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import type { DescMessage } from "@bufbuild/protobuf";
import { ComponentBatchSchema, StructuralCommandSchema } from "@ecs/protos/ecs/protocol/v1/tick_pb.js";
import { ComponentInfoSchema, ComponentSchemaSchema } from "@ecs/protos/ecs/v1/component_pb.js";
import { ParentRefSchema } from "@ecs/protos/ecs/v1/relations_pb.js";
import { PositionSchema } from "@ecs/protos/movement/v1/movement_pb.js";
import {
  ConformanceEnumsSchema,
  ConformanceLeafSchema,
  ConformanceLeafTwinSchema,
  ConformanceMapsSchema,
  ConformanceOneofSchema,
  ConformanceOrderingSchema,
  ConformancePresenceSchema,
  ConformanceRecursiveSchema,
  ConformanceScalarsSchema,
  ConformanceWellKnownSchema,
} from "@ecs/protos/testing/v1/conformance_pb.js";
import { canonicalize, fileDescriptorSetFor, formatSchemaHash, schemaHash } from "../src/index.js";

/**
 * The point of this file.
 *
 * This implementation was written from `protocol/SPEC.md`, not from the C# one, and
 * it is checked against vectors neither implementation owns. If the two agree here,
 * the protocol is genuinely implementable more than once — which is the only way a
 * Rust or Python client is ever going to work.
 */

interface Vector {
  logicalName: string;
  schemaHash: string;
  canonical: string;
}

interface Vectors {
  version: number;
  algorithm: string;
  cases: Vector[];
}

const vectors: Vectors = JSON.parse(
  readFileSync(
    fileURLToPath(new URL("../../../protocol/conformance/schema-hash.json", import.meta.url)),
    "utf8",
  ),
);

const SCHEMAS: DescMessage[] = [
  ComponentBatchSchema,
  StructuralCommandSchema,
  ComponentInfoSchema,
  ComponentSchemaSchema,
  ParentRefSchema,
  PositionSchema,
  ConformanceEnumsSchema,
  ConformanceLeafSchema,
  ConformanceLeafTwinSchema,
  ConformanceMapsSchema,
  ConformanceOneofSchema,
  ConformanceOrderingSchema,
  ConformancePresenceSchema,
  ConformanceRecursiveSchema,
  ConformanceScalarsSchema,
  ConformanceWellKnownSchema,
];

const byName = new Map(SCHEMAS.map((schema) => [schema.typeName, schema]));

describe("schema hash conformance", () => {
  it("pins the algorithm the vectors were produced with", () => {
    expect(vectors.version).toBe(1);
    expect(vectors.algorithm).toBe("sha256-truncated-64-be");
    expect(vectors.cases.length).toBeGreaterThan(0);
  });

  it("covers every pinned type", () => {
    for (const vector of vectors.cases) {
      expect(byName.has(vector.logicalName), `missing schema for ${vector.logicalName}`).toBe(true);
    }
  });

  for (const vector of vectors.cases) {
    // Canonical form first: when this fails it says which line is wrong, whereas a
    // hash mismatch says only that something, somewhere, differs.
    it(`renders ${vector.logicalName} canonically`, () => {
      const schema = byName.get(vector.logicalName)!;
      const set = fileDescriptorSetFor(schema);

      expect(canonicalize(set, vector.logicalName)).toBe(vector.canonical);
    });

    it(`hashes ${vector.logicalName}`, () => {
      const schema = byName.get(vector.logicalName)!;
      const set = fileDescriptorSetFor(schema);

      expect(formatSchemaHash(schemaHash(set, vector.logicalName))).toBe(vector.schemaHash);
    });
  }
});

describe("schema hash", () => {
  it("carries the hash as a bigint, not a rounded number", () => {
    const set = fileDescriptorSetFor(ConformanceLeafSchema);
    const hash = schemaHash(set, "testing.v1.ConformanceLeaf");

    expect(typeof hash).toBe("bigint");
    expect(hash).toBeLessThanOrEqual(0xffffffffffffffffn);
  });

  it("refuses a name the set does not contain", () => {
    const set = fileDescriptorSetFor(ConformanceLeafSchema);

    expect(() => schemaHash(set, "testing.v1.NotInThisFile")).toThrow(/does not contain/);
  });

  it("distinguishes types with identical fields", () => {
    const leaf = schemaHash(fileDescriptorSetFor(ConformanceLeafSchema), "testing.v1.ConformanceLeaf");
    const twin = schemaHash(
      fileDescriptorSetFor(ConformanceLeafTwinSchema),
      "testing.v1.ConformanceLeafTwin",
    );

    expect(leaf).not.toBe(twin);
  });
});
