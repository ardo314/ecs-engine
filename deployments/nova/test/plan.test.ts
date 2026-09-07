import { describe, expect, it } from "vitest";
import { readOptions } from "../src/options.js";
import { DEFAULT_PORT, planStack } from "../src/plan.js";

const plan = (env: Record<string, string> = {}) => planStack(readOptions((key) => env[key]));

describe("planStack", () => {
  it("installs the engine and the editor when no systems are given", () => {
    expect(plan().map((a) => a.name)).toEqual(["ecs-engine", "ecs-editor"]);
  });

  it("never installs a broker", () => {
    const apps = plan({ ECS_SYSTEM_IMAGES: "ghcr.io/acme/movement-system:1.0" });

    expect(apps.some((a) => a.name.includes("nats"))).toBe(false);
  });

  it("places the coordinator before every consumer", () => {
    const apps = plan({ ECS_SYSTEM_IMAGES: "ghcr.io/acme/movement-system:1.0" });

    expect(apps[0].name).toBe("ecs-engine");
  });

  it("skips the editor app when it is disabled", () => {
    expect(plan({ ECS_INSTALL_EDITOR: "false" }).map((a) => a.name)).toEqual(["ecs-engine"]);
  });

  it("appends one app per system image", () => {
    const apps = plan({
      ECS_INSTALL_EDITOR: "false",
      ECS_SYSTEM_IMAGES: "ghcr.io/acme/movement-system:1.0,nova=ghcr.io/acme/nova-systems:2.0",
    });

    expect(apps.map((a) => a.name)).toEqual(["ecs-engine", "ecs-movement-system", "ecs-nova"]);
    expect(apps[2].container_image.image).toBe("ghcr.io/acme/nova-systems:2.0");
  });

  it("serves every app on the fixed health port", () => {
    const apps = plan({ ECS_SYSTEM_IMAGES: "ghcr.io/acme/movement-system:1.0" });

    for (const app of apps) {
      expect(app.port).toBe(DEFAULT_PORT);
      expect(app.health_path).toBe("health");
      expect(app.environment).not.toContainEqual(expect.objectContaining({ name: "HEALTH_PORT" }));
    }
  });

  // NOVA joins these onto BASE_PATH; a leading slash probes /<cell>/<app>//health.
  it("keeps probe paths relative to the app base path", () => {
    const apps = plan({ ECS_SYSTEM_IMAGES: "ghcr.io/acme/movement-system:1.0" });

    for (const app of apps) {
      expect(app.health_path?.startsWith("/")).toBe(false);
      expect(app.app_icon?.startsWith("/")).toBe(false);
    }
  });

  it("omits NATS_URL when no broker address is known", () => {
    const apps = plan({ ECS_SYSTEM_IMAGES: "ghcr.io/acme/movement-system:1.0" });

    expect(apps.flatMap((a) => a.environment ?? [])).not.toContainEqual(
      expect.objectContaining({ name: "NATS_URL" }),
    );
  });

  it("passes NOVA's injected broker on as NATS_URL", () => {
    const apps = plan({
      ECS_SYSTEM_IMAGES: "ghcr.io/acme/movement-system:1.0",
      NATS_BROKER: "nats://user:token@nova-nats:4222",
    });

    for (const app of apps)
      expect(app.environment).toContainEqual({
        name: "NATS_URL",
        value: "nats://user:token@nova-nats:4222",
      });
  });

  it("uses an explicit NATS_URL when provided", () => {
    const apps = plan({
      ECS_SYSTEM_IMAGES: "ghcr.io/acme/movement-system:1.0",
      ECS_NATS_URL: "nats://platform-nats:4222",
    });

    for (const name of ["ecs-engine", "ecs-editor", "ecs-movement-system"]) {
      const app = apps.find((a) => a.name === name);
      expect(app?.environment).toContainEqual({
        name: "NATS_URL",
        value: "nats://platform-nats:4222",
      });
    }
  });

  it("serves the editor UI and API from one app", () => {
    const editor = plan({ NOVA_CELL: "cell" }).find((a) => a.name.startsWith("ecs-editor"));

    expect(editor?.environment).not.toContainEqual(
      expect.objectContaining({ name: "EDITOR_BACKEND_URL" }),
    );
  });

  it("leaves BASE_PATH to NOVA", () => {
    expect(plan().flatMap((a) => a.environment ?? [])).not.toContainEqual(
      expect.objectContaining({ name: "BASE_PATH" }),
    );
  });

  it("applies registry credentials when both parts are set", () => {
    const apps = plan({
      ECS_INSTALL_EDITOR: "false",
      ECS_REGISTRY_USER: "robot",
      ECS_REGISTRY_PASSWORD: "secret",
    });

    expect(apps[0].container_image.credentials).toEqual({
      registry: "ghcr.io",
      user: "robot",
      password: "secret",
    });
  });

  it("omits credentials when only the user is set", () => {
    const apps = plan({ ECS_INSTALL_EDITOR: "false", ECS_REGISTRY_USER: "robot" });

    expect(apps[0].container_image.credentials).toBeUndefined();
  });

  it("serializes to the API's snake_case shape", () => {
    const json = JSON.stringify(plan()[0]);

    expect(json).toContain('"app_icon":"app_icon.png"');
    expect(json).toContain('"container_image":{"image":');
    expect(json).toContain('"health_path":"health"');
    expect(json).not.toContain('"storage"');
  });
});
