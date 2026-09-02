import { serve } from "@hono/node-server";
import { serveStatic } from "@hono/node-server/serve-static";
import { createNodeWebSocket } from "@hono/node-ws";
import { Hono } from "hono";
import { Broadcaster, createApp, EngineBridge } from "./app.js";
import { connectToNats } from "./nats.js";

const port = Number(process.env.PORT ?? 8080);
const basePath = normalizeBasePath(process.env.BASE_PATH);

const nats = await connectToNats();
const broadcaster = new Broadcaster();

const root = new Hono();
const { injectWebSocket, upgradeWebSocket } = createNodeWebSocket({ app: root });

const app = createApp({ nats, broadcaster, upgradeWebSocket });

// The built client is served from the same origin as the API, so the browser needs
// no backend URL and there is nothing to inject at container start.
app.use("/assets/*", serveStatic({ root: "./dist/client" }));
app.get("*", serveStatic({ path: "./dist/client/index.html" }));

root.route(basePath, app);

const server = serve({ fetch: root.fetch, port }, (info) =>
  console.log(`[Editor] Listening on http://localhost:${info.port}${basePath}`),
);
injectWebSocket(server);

const shutdown = new AbortController();
for (const signal of ["SIGINT", "SIGTERM"] as const) {
  process.on(signal, () => {
    shutdown.abort();
    server.close(() => process.exit(0));
  });
}

await new EngineBridge(nats, broadcaster).run(shutdown.signal);

function normalizeBasePath(value: string | undefined): string {
  const trimmed = (value ?? "").replace(/^\/+|\/+$/g, "");
  return trimmed === "" ? "/" : `/${trimmed}`;
}
