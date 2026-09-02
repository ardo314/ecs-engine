import { Hono } from "hono";
import { cors } from "hono/cors";
import type { UpgradeWebSocket } from "hono/ws";
import type { NatsConnection } from "@nats-io/nats-core";
import { Broadcaster } from "./broadcaster.js";
import { EngineBridge } from "./bridge.js";
import {
  encodeComponentRemoveRequest,
  encodeEntityDestroyRequest,
  encodeEntitySpawnRequest,
} from "./msgpack.js";

export interface AppDeps {
  nats: NatsConnection;
  broadcaster: Broadcaster;
  upgradeWebSocket: UpgradeWebSocket;
}

export function createApp({ nats, broadcaster, upgradeWebSocket }: AppDeps) {
  const app = new Hono();

  app.use("/api/*", cors());

  app.get("/health", (c) => c.json({ status: "healthy" }));

  app.post("/api/entities", (c) => {
    nats.publish("engine.entity.spawn.request", encodeEntitySpawnRequest([]));
    return c.body(null, 202);
  });

  app.delete("/api/entities/:id", (c) => {
    nats.publish(
      "engine.entity.destroy.request",
      encodeEntityDestroyRequest([BigInt(c.req.param("id"))]),
    );
    return c.body(null, 202);
  });

  app.delete("/api/entities/:id/components/:componentType", (c) => {
    nats.publish(
      "engine.entity.component.remove",
      encodeComponentRemoveRequest(
        BigInt(c.req.param("id")),
        decodeURIComponent(c.req.param("componentType")),
      ),
    );
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
