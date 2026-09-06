import { randomUUID } from "node:crypto";
import { fromBinary, toBinary } from "@bufbuild/protobuf";
import type { NatsConnection } from "@nats-io/nats-core";
import {
  WatchCancelSchema,
  WatchDataSchema,
  WatchRequestSchema,
  WatchResponseSchema,
  type EntitySnapshot,
  type SystemInfo,
} from "@ecs/protos/ecs/protocol/v1/world_pb.js";
import type { Broadcaster } from "./broadcaster.js";
import { SchemaRegistry } from "./schemas.js";

const SUBJECT_SUBSCRIBE = "engine.world.watch.subscribe";
const SUBJECT_CANCEL = "engine.world.watch.cancel";
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
 *
 * Everything on this path is protobuf, decoded with the generated protocol types. The
 * component payloads inside are decoded reflectively against the descriptors the world
 * sends, so the editor never needs to be rebuilt when a domain adds a component.
 */
export class EngineBridge {
  private readonly watchId = randomUUID();
  private lastSystems: SystemInfo[] = [];
  private lastStages: string[][] = [];

  /** The world's schema registry, as this editor has learned it. */
  readonly schemas = new SchemaRegistry();

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
        this.nats.publish(
          SUBJECT_CANCEL,
          toBinary(WatchCancelSchema, { $typeName: WatchCancelSchema.typeName, watchId: this.watchId }),
        );
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
          SUBJECT_SUBSCRIBE,
          toBinary(WatchRequestSchema, {
            $typeName: WatchRequestSchema.typeName,
            watchId: this.watchId,
            includeSystems: true,
            includeEntities: true,
            filter: undefined,
          }),
          { timeout: 10_000 },
        );

        const { dataSubject } = fromBinary(WatchResponseSchema, reply.data);
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
    const watchData = fromBinary(WatchDataSchema, data);

    // Schemas arrive before anything that needs them, and only when they change.
    for (const info of watchData.componentTypes) this.schemas.add(info);

    if (watchData.systems.length > 0) this.lastSystems = watchData.systems;
    if (watchData.stages.length > 0) {
      this.lastStages = watchData.stages.map((stage) => stage.systems);
    }

    this.broadcaster.broadcast(
      JSON.stringify({
        type: "snapshot",
        tickId: Number(watchData.tick),
        systems: this.lastSystems.map((system) => ({
          name: system.name,
          instanceId: system.instanceId,
          reads: system.reads.map((id) => this.schemas.nameOf(id)),
          writes: system.writes.map((id) => this.schemas.nameOf(id)),
        })),
        stages: this.lastStages,
        entities: watchData.entities.map((entity) => this.toEditorEntity(entity)),
      }),
    );
  }

  private toEditorEntity(entity: EntitySnapshot) {
    const components: Record<string, unknown> = {};

    for (const binding of entity.components) {
      const name = this.schemas.nameOf(binding.typeId);
      components[name] = this.schemas.decodeById(binding.typeId, binding.payload);
    }

    return { entityId: Number(entity.entity), components };
  }
}
