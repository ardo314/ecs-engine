import { serve } from "@hono/node-server";
import { Hono } from "hono";
import { DEFAULT_PORT } from "./plan.js";

// 1x1 transparent PNG — NOVA requires an app_icon path to be servable.
const ICON = Buffer.from(
  "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==",
  "base64",
);

export interface HealthServer {
  readonly port: number;
  close(): Promise<void>;
}

/**
 * Starts a server that serves nothing but the probe endpoints NOVA polls, mounted below the
 * BASE_PATH it injects. Returns null when the port is already taken — several processes on
 * one dev machine must not fail to start.
 */
export async function tryStartHealthEndpoint(port = DEFAULT_PORT): Promise<HealthServer | null> {
  const app = new Hono();
  app.get("/health", (c) => c.json({ status: "healthy" }));
  app.get("/app_icon.png", () => new Response(ICON, { headers: { "Content-Type": "image/png" } }));

  const basePath = normalizeBasePath(process.env.BASE_PATH);
  const root = basePath === "/" ? app : new Hono().route(basePath, app);

  return new Promise((resolve) => {
    const onError = (error: Error) => {
      console.log(`Health endpoint disabled: ${error.message}`);
      resolve(null);
    };

    const server = serve({ fetch: root.fetch, port }, (info) => {
      server.off("error", onError);
      resolve({
        port: info.port,
        close: () => new Promise<void>((closed) => server.close(() => closed())),
      });
    });

    server.once("error", onError);
  });
}

export function normalizeBasePath(value: string | undefined): string {
  const trimmed = (value ?? "").replace(/^\/+|\/+$/g, "");
  return trimmed === "" ? "/" : `/${trimmed}`;
}
