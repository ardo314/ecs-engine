import { afterEach, describe, expect, it } from "vitest";
import { type HealthServer, tryStartHealthEndpoint } from "../src/health.js";

const started: HealthServer[] = [];

async function start(port = 0): Promise<HealthServer | null> {
  const server = await tryStartHealthEndpoint(port);
  if (server !== null) started.push(server);
  return server;
}

afterEach(async () => {
  await Promise.all(started.splice(0).map((s) => s.close()));
  delete process.env.BASE_PATH;
});

describe("tryStartHealthEndpoint", () => {
  it("answers the probe with healthy json", async () => {
    const server = await start();

    const response = await fetch(`http://localhost:${server!.port}/health`);

    expect(response.status).toBe(200);
    expect(await response.json()).toEqual({ status: "healthy" });
  });

  it("serves the app icon NOVA requires", async () => {
    const server = await start();

    const response = await fetch(`http://localhost:${server!.port}/app_icon.png`);

    expect(response.status).toBe(200);
    expect(response.headers.get("content-type")).toBe("image/png");
  });

  // NOVA injects BASE_PATH, and probes arrive through it.
  it("mounts everything below the injected base path", async () => {
    process.env.BASE_PATH = "/cell/ecs-installer";
    const server = await start();

    expect((await fetch(`http://localhost:${server!.port}/cell/ecs-installer/health`)).status).toBe(200);
    expect((await fetch(`http://localhost:${server!.port}/health`)).status).toBe(404);
  });

  it("returns null when the port is taken", async () => {
    const first = await start();

    expect(await start(first!.port)).toBeNull();
  });
});
