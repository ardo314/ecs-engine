export interface SystemInfo {
  name: string;
  instanceId: string;
  reads: string[];
  writes: string[];
}

export interface EntitySnapshot {
  entityId: number;
  components: Record<string, Record<string, unknown> | null>;
}

export interface Snapshot {
  type: "snapshot";
  tickId: number;
  systems: SystemInfo[];
  stages: string[][];
  entities: EntitySnapshot[];
}
