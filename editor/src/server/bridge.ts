import { randomUUID } from "node:crypto";
import type { NatsConnection } from "@nats-io/nats-core";
import type { Broadcaster } from "./broadcaster.js";
import {
  decodeWatchData,
  decodeWatchResponse,
  encodeWatchCancel,
  encodeWatchRequest,
  type EntitySnapshot,
  type SystemInfo,
} from "./msgpack.js";
import { SchemaRegistry } from "./schemas.js";

const COMPONENT_SCHEMA = "ecs.v1.ComponentSchema";
const RETRY_DELAY_MS = 2000;

function delay(ms: number, signal: AbortSignal): Promise<void> {
  return new Promise((resolve) => {
    const timer = setTimeout(resolve, ms);
    signal.addEventListener("abort", () => {
      clearTimeout(timer);
      resolve();
    }, { once: true });
  });
}

/**
 * Registers a watch with the coordinator and pushes decoded state snapshots to all
 * connected WebSocket clients.
 */
export class EngineBridge {
  private readonly watchId = randomUUID();
  private readonly schemas = new SchemaRegistry();
  private lastSystems: SystemInfo[] = [];
  private lastStages: string[][] = [];

  constructor(
    private readonly nats: NatsConnection,
    private readonly broadcaster: Broadcaster,
  ) {}

  async run(signal: AbortSignal): Promise<void> {
    try {
      // The editor and the coordinator start together, and the coordinator may
      // restart under us, so keep re-registering until we are told to stop.
      while (!signal.aborted) {
        const dataSubject = await this.register(signal);
        if (dataSubject === null) break;
        await this.consume(dataSubject, signal);
      }
    } finally {
      try {
        this.nats.publish("engine.watch.unsubscribe", encodeWatchCancel(this.watchId));
        console.log("[EditorBridge] Watch cancelled.");
      } catch {
        // Best-effort cleanup.
      }
    }
  }

  private async register(signal: AbortSignal): Promise<string | null> {
    while (!signal.aborted) {
      try {
        const reply = await this.nats.request(
          "engine.watch.subscribe",
          encodeWatchRequest({
            watchId: this.watchId,
            includeSystems: true,
            includeEntities: true,
          }),
          { timeout: 10_000 },
        );

        const { dataSubject } = decodeWatchResponse(reply.data);
        console.log(`[EditorBridge] Watch registered, subscribing to ${dataSubject}`);
        return dataSubject;
      } catch {
        console.log("[EditorBridge] Coordinator unavailable, retrying...");
        await delay(RETRY_DELAY_MS, signal);
      }
    }
    return null;
  }

  private async consume(dataSubject: string, signal: AbortSignal): Promise<void> {
    const subscription = this.nats.subscribe(dataSubject);
    const stop = () => subscription.unsubscribe();
    signal.addEventListener("abort", stop, { once: true });

    try {
      for await (const message of subscription) {
        try {
          this.onWatchData(message.data);
        } catch (error) {
          console.error(`[EditorBridge] Error processing watch data: ${String(error)}`);
        }
      }
    } finally {
      signal.removeEventListener("abort", stop);
    }
  }

  private onWatchData(data: Uint8Array): void {
    const watchData = decodeWatchData(data);

    if (watchData.systems !== null) this.lastSystems = watchData.systems;
    if (watchData.stages !== null) this.lastStages = watchData.stages;

    const entities = watchData.entities ?? [];
    this.absorbSchemas(entities);

    this.broadcaster.broadcast(
      JSON.stringify({
        type: "snapshot",
        tickId: Number(watchData.tickId),
        systems: this.lastSystems,
        stages: this.lastStages,
        entities: entities.map((entity) => this.toEditorEntity(entity)),
      }),
    );
  }

  /** Component type entities carry their own descriptors; learn them before decoding. */
  private absorbSchemas(entities: EntitySnapshot[]): void {
    for (const entity of entities) {
      const schema = entity.components.get(COMPONENT_SCHEMA);
      if (schema !== undefined) this.schemas.add(schema);
    }
  }

  private toEditorEntity(entity: EntitySnapshot) {
    const components: Record<string, unknown> = {};

    for (const [typeName, payload] of entity.components) {
      components[typeName] = this.schemas.decode(typeName, payload);
    }

    return { entityId: Number(entity.entityId), components };
  }
}
