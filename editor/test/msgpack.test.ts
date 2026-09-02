import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import { decodeWatchData, decodeWatchResponse } from "../src/server/msgpack";

/**
 * The fixtures are written by Client.Tests' WireFixtureTests, so these assertions
 * fail the moment the C# envelopes and this decoder disagree.
 */
function fixture(name: string): Uint8Array {
  const file = name.includes(".") ? name : `${name}.msgpack`;
  return new Uint8Array(readFileSync(fileURLToPath(new URL(`./fixtures/${file}`, import.meta.url))));
}

describe("MessagePack envelopes produced by the coordinator", () => {
  it("decodes a WatchResponse, reading Guid as its canonical string", () => {
    const response = decodeWatchResponse(fixture("watchResponse"));

    expect(response.watchId).toBe("6f9619ff-8b86-d011-b42d-00c04fc964ff");
    expect(response.dataSubject).toBe(
      "engine.watch.data.6f9619ff-8b86-d011-b42d-00c04fc964ff",
    );
  });

  it("decodes a WatchData, reading ulong as bigint", () => {
    const data = decodeWatchData(fixture("watchData"));

    expect(data.watchId).toBe("6f9619ff-8b86-d011-b42d-00c04fc964ff");
    expect(data.tickId).toBe(12345678901234n);
    expect(data.stages).toEqual([["Movement"], ["Render"]]);
  });

  it("decodes system descriptors", () => {
    const { systems } = decodeWatchData(fixture("watchData"));

    expect(systems).toHaveLength(1);
    expect(systems![0].name).toBe("Movement");
    expect(systems![0].reads).toEqual(["movement.v1.Velocity"]);
    expect(systems![0].writes).toEqual(["movement.v1.Position"]);
    expect(systems![0].queries[0].requiredTypes).toEqual([
      "movement.v1.Position",
      "movement.v1.Velocity",
    ]);
  });

  it("keeps component payloads as raw bytes, including zero-length ones", () => {
    const { entities } = decodeWatchData(fixture("watchData"));

    expect(entities).toHaveLength(1);
    expect(entities![0].entityId).toBe(7n);
    expect(entities![0].components.get("movement.v1.Position")).toEqual(
      fixture("position.bin"),
    );

    // An empty protobuf message is zero bytes, not absent.
    expect(entities![0].components.get("testing.v1.TestSetting")).toEqual(new Uint8Array());
  });
});
