import { decode, encode } from "@msgpack/msgpack";

/**
 * Wire codecs for the coordinator's MessagePack envelopes.
 *
 * MessagePack-CSharp's ContractlessStandardResolver maps C# properties to PascalCase
 * keys, serialises Guid as its 36-character "D" string and ulong as uint64. Component
 * payloads inside these envelopes are protobuf and stay as raw bytes.
 */

export type Guid = string;

export interface WatchRequest {
  watchId: Guid;
  includeSystems: boolean;
  includeEntities: boolean;
  componentFilter?: string[] | null;
  anyTypes?: string[] | null;
}

export interface WatchResponse {
  watchId: Guid;
  dataSubject: string;
}

export interface QueryDescriptor {
  requiredTypes: string[];
  optionalTypes: string[];
  excludedTypes: string[];
  readTypes: string[];
  writeTypes: string[];
  taggedTypes: string[];
}

export interface SystemInfo {
  name: string;
  instanceId: string;
  reads: string[];
  writes: string[];
  queries: QueryDescriptor[];
}

export interface EntitySnapshot {
  entityId: bigint;
  components: Map<string, Uint8Array>;
}

export interface WatchData {
  watchId: Guid;
  tickId: bigint;
  systems: SystemInfo[] | null;
  stages: string[][] | null;
  entities: EntitySnapshot[] | null;
}

// ── Encoding ────────────────────────────────────────────────────

const encodeOptions = { useBigInt64: true } as const;
const decodeOptions = { useBigInt64: true } as const;

export function encodeWatchRequest(request: WatchRequest): Uint8Array {
  return encode(
    {
      WatchId: request.watchId,
      IncludeSystems: request.includeSystems,
      IncludeEntities: request.includeEntities,
      ComponentFilter: request.componentFilter ?? null,
      AnyTypes: request.anyTypes ?? null,
    },
    encodeOptions,
  );
}

export function encodeWatchCancel(watchId: Guid): Uint8Array {
  return encode({ WatchId: watchId }, encodeOptions);
}

export function encodeEntitySpawnRequest(
  components: { type: string; data: Uint8Array }[],
): Uint8Array {
  return encode(
    {
      ComponentTypes: components.map((c) => c.type),
      ComponentData: components.map((c) => c.data),
    },
    encodeOptions,
  );
}

export function encodeEntityDestroyRequest(entityIds: bigint[]): Uint8Array {
  return encode({ EntityIds: entityIds }, encodeOptions);
}

export function encodeComponentRemoveRequest(
  entityId: bigint,
  componentType: string,
): Uint8Array {
  return encode(
    {
      Target: { EntityId: entityId, ComponentType: null },
      ComponentType: componentType,
    },
    encodeOptions,
  );
}

// ── Decoding ────────────────────────────────────────────────────

type RawObject = Record<string, unknown>;

function asObject(value: unknown): RawObject {
  if (typeof value !== "object" || value === null || Array.isArray(value))
    throw new Error("Expected a MessagePack map");
  return value as RawObject;
}

function str(source: RawObject, key: string): string {
  const value = source[key];
  return typeof value === "string" ? value : "";
}

function bigintOf(value: unknown): bigint {
  if (typeof value === "bigint") return value;
  if (typeof value === "number") return BigInt(value);
  return 0n;
}

function strings(value: unknown): string[] {
  return Array.isArray(value) ? value.filter((v): v is string => typeof v === "string") : [];
}

export function decodeWatchResponse(data: Uint8Array): WatchResponse {
  const source = asObject(decode(data, decodeOptions));
  return { watchId: str(source, "WatchId"), dataSubject: str(source, "DataSubject") };
}

export function decodeWatchData(data: Uint8Array): WatchData {
  const source = asObject(decode(data, decodeOptions));
  const { Systems: systems, Stages: stages, Entities: entities } = source;

  return {
    watchId: str(source, "WatchId"),
    tickId: bigintOf(source.TickId),
    systems: Array.isArray(systems) ? systems.map(decodeSystemInfo) : null,
    stages: Array.isArray(stages) ? stages.map(strings) : null,
    entities: Array.isArray(entities) ? entities.map(decodeEntitySnapshot) : null,
  };
}

function decodeSystemInfo(value: unknown): SystemInfo {
  const source = asObject(value);
  const queries = source.Queries;
  return {
    name: str(source, "Name"),
    instanceId: str(source, "InstanceId"),
    reads: strings(source.Reads),
    writes: strings(source.Writes),
    queries: Array.isArray(queries) ? queries.map(decodeQueryDescriptor) : [],
  };
}

function decodeQueryDescriptor(value: unknown): QueryDescriptor {
  const source = asObject(value);
  return {
    requiredTypes: strings(source.RequiredTypes),
    optionalTypes: strings(source.OptionalTypes),
    excludedTypes: strings(source.ExcludedTypes),
    readTypes: strings(source.ReadTypes),
    writeTypes: strings(source.WriteTypes),
    taggedTypes: strings(source.TaggedTypes),
  };
}

function decodeEntitySnapshot(value: unknown): EntitySnapshot {
  const source = asObject(value);
  const components = new Map<string, Uint8Array>();

  if (source.Components !== undefined && source.Components !== null) {
    for (const [typeName, payload] of Object.entries(asObject(source.Components))) {
      if (payload instanceof Uint8Array) components.set(typeName, payload);
    }
  }

  return { entityId: bigintOf(source.EntityId), components };
}
