import { create, toBinary } from "@bufbuild/protobuf";
import { Hono } from "hono";
import { cors } from "hono/cors";
import type { UpgradeWebSocket } from "hono/ws";
import type { NatsConnection } from "@nats-io/nats-core";
import {
  CommandBatchSchema,
  type StructuralCommand,
} from "@ecs/protos/ecs/protocol/v1/tick_pb.js";
import { Broadcaster } from "./broadcaster.js";
import { EngineBridge } from "./bridge.js";

const SUBJECT_COMMAND = "engine.world.command";

export interface AppDeps {
  nats: NatsConnection;
  broadcaster: Broadcaster;
  bridge: EngineBridge;
  upgradeWebSocket: UpgradeWebSocket;
}

export function createApp({ nats, broadcaster, bridge, upgradeWebSocket }: AppDeps) {
  const app = new Hono();

  /**
   * Editor edits are ordinary structural commands. They take the same path as a
   * system's, so they land at the coordinator's synchronisation point rather than
   * mutating the world while a tick is in flight.
   */
  const submit = async (...commands: StructuralCommand[]) => {
    const batch = create(CommandBatchSchema, { commands });
    await nats.request(SUBJECT_COMMAND, toBinary(CommandBatchSchema, batch), { timeout: 5000 });
  };

  app.use("/api/*", cors());

  app.get("/health", (c) => c.json({ status: "healthy" }));

  app.post("/api/entities", async (c) => {
    await submit({
      $typeName: "ecs.protocol.v1.StructuralCommand",
      command: {
        case: "spawn",
        value: { $typeName: "ecs.protocol.v1.SpawnEntity", components: [] },
      },
    });
    return c.body(null, 202);
  });

  app.delete("/api/entities/:id", async (c) => {
    await submit({
      $typeName: "ecs.protocol.v1.StructuralCommand",
      command: {
        case: "despawn",
        value: { $typeName: "ecs.protocol.v1.DespawnEntity", entity: BigInt(c.req.param("id")) },
      },
    });
    return c.body(null, 202);
  });

  app.delete("/api/entities/:id/components/:componentType", async (c) => {
    const typeName = decodeURIComponent(c.req.param("componentType"));

    // The coordinator matches on exact schema identity, so a type the editor has not
    // been told about cannot be addressed at all.
    const type = bridge.schemas.refOf(typeName);
    if (type === undefined) {
      return c.json({ error: `Unknown component type '${typeName}'.` }, 404);
    }

    await submit({
      $typeName: "ecs.protocol.v1.StructuralCommand",
      command: {
        case: "remove",
        value: {
          $typeName: "ecs.protocol.v1.RemoveComponent",
          target: {
            $typeName: "ecs.protocol.v1.CommandTarget",
            target: { case: "entity", value: BigInt(c.req.param("id")) },
          },
          type,
        },
      },
    });
    return c.body(null, 202);
  });

  app.get(
    "/ws",
    upgradeWebSocket(() => ({
      onOpen(_event, ws) {
        broadcaster.add(ws);
        const cached = broadcaster.cachedSnapshot;
        if (cached !== null) ws.send(cached);
      },
      onClose(_event, ws) {
        broadcaster.remove(ws);
      },
      onError(_event, ws) {
        broadcaster.remove(ws);
      },
    })),
  );

  return app;
}

export { Broadcaster, EngineBridge };
