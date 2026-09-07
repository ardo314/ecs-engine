import { describe, expect, it } from "vitest";
import {
  DEFAULT_IMAGE_TAG,
  DEFAULT_REGISTRY,
  InstallerConfigurationError,
  deriveName,
  normalizeBaseUrl,
  parseSystemImages,
  readOptions,
  sanitizeAppName,
} from "../src/options.js";

const options = (env: Record<string, string> = {}) => readOptions((key) => env[key]);

describe("readOptions", () => {
  it("derives images from registry and tag", () => {
    const result = options({ ECS_IMAGE_REGISTRY: "registry.example.com/ecs", ECS_IMAGE_TAG: "1.2.3" });

    expect(result.engineImage).toBe("registry.example.com/ecs/engine:1.2.3");
    expect(result.editorImage).toBe("registry.example.com/ecs/editor:1.2.3");
  });

  it("defaults to the tag baked in at build time", () => {
    const result = options();

    expect(result.engineImage).toBe(`${DEFAULT_REGISTRY}/engine:${DEFAULT_IMAGE_TAG}`);
    expect(result.editorImage).toBe(`${DEFAULT_REGISTRY}/editor:${DEFAULT_IMAGE_TAG}`);
  });

  it("leaves the broker address unset by default", () => {
    expect(options().natsUrl).toBeUndefined();
  });

  it("takes the broker address from NOVA's injected NATS_BROKER", () => {
    expect(options({ NATS_BROKER: "nats://user:token@nova-nats:4222" }).natsUrl).toBe(
      "nats://user:token@nova-nats:4222",
    );
  });

  it("prefers an explicit ECS_NATS_URL over the injected broker", () => {
    const result = options({
      ECS_NATS_URL: "nats://platform-nats:4222",
      NATS_BROKER: "nats://user:token@nova-nats:4222",
    });

    expect(result.natsUrl).toBe("nats://platform-nats:4222");
  });

  it("prefers explicit image overrides", () => {
    expect(options({ ECS_ENGINE_IMAGE: "docker.io/acme/engine:dev" }).engineImage).toBe(
      "docker.io/acme/engine:dev",
    );
  });

  it("trims the trailing slash from the base url", () => {
    expect(options({ NOVA_BASE_URL: "https://nova.example.com/" }).novaBaseUrl).toBe(
      "https://nova.example.com",
    );
  });

  it("falls back to the injected NOVA_API and CELL_NAME", () => {
    const result = options({
      NOVA_API: "http://api-gateway.wandelbots.svc.cluster.local/api/v1",
      CELL_NAME: "cell-a",
    });

    expect(result.novaBaseUrl).toBe("http://api-gateway.wandelbots.svc.cluster.local");
    expect(result.cell).toBe("cell-a");
  });

  it("prefers an explicit base url over the injected NOVA_API", () => {
    const result = options({
      NOVA_BASE_URL: "https://nova.example.com",
      NOVA_API: "http://api-gateway/api/v1",
    });

    expect(result.novaBaseUrl).toBe("https://nova.example.com");
  });

  it("rejects a non-positive tick rate", () => {
    expect(() => options({ ECS_TICK_RATE: "0" })).toThrow(InstallerConfigurationError);
  });

  it("rejects an unparseable boolean", () => {
    expect(() => options({ ECS_INSTALL_EDITOR: "maybe" })).toThrow(InstallerConfigurationError);
  });

  it("rejects an app prefix with nothing alphanumeric in it", () => {
    expect(() => options({ ECS_APP_PREFIX: "---" })).toThrow(InstallerConfigurationError);
  });
});

describe("normalizeBaseUrl", () => {
  it.each([
    ["api-gateway:8080", "http://api-gateway:8080"],
    ["https://nova.example.com/api", "https://nova.example.com"],
    ["https://nova.example.com/api/v2/", "https://nova.example.com"],
  ])("reduces %s to the instance root", (raw, expected) => {
    expect(normalizeBaseUrl(raw)).toBe(expected);
  });

  it("rejects non-http addresses", () => {
    expect(() => normalizeBaseUrl("nats://broker:4222")).toThrow(InstallerConfigurationError);
  });
});

describe("deriveName", () => {
  it.each([
    ["ghcr.io/acme/movement-system:1.0", "movement-system"],
    ["docker.io/library/nats", "nats"],
    ["acme/nova-systems@sha256:abc", "nova-systems"],
  ])("uses the repository segment of %s without tag or digest", (image, expected) => {
    expect(deriveName(image)).toBe(expected);
  });
});

describe("parseSystemImages", () => {
  it("accepts bare images and name pairs", () => {
    const systems = parseSystemImages("ghcr.io/acme/movement-system:1.0, io = ghcr.io/acme/nova:2.0");

    expect(systems.map((s) => s.name)).toEqual(["movement-system", "io"]);
    expect(systems.map((s) => s.image)).toEqual([
      "ghcr.io/acme/movement-system:1.0",
      "ghcr.io/acme/nova:2.0",
    ]);
  });

  it("rejects duplicate names", () => {
    expect(() => parseSystemImages("a=ghcr.io/x/one:1,a=ghcr.io/x/two:1")).toThrow(
      InstallerConfigurationError,
    );
  });

  it("rejects an entry without an image", () => {
    expect(() => parseSystemImages("name=")).toThrow(InstallerConfigurationError);
  });

  it("treats an empty value as no systems", () => {
    expect(parseSystemImages("  ")).toEqual([]);
  });
});

describe("sanitizeAppName", () => {
  it.each([
    ["Movement System", "movement-system"],
    ["Nova.Systems", "nova-systems"],
    ["--weird__name--", "weird-name"],
    ["9lives", "a9lives"],
  ])("reduces %s to an RFC 1035 label", (input, expected) => {
    expect(sanitizeAppName(input)).toBe(expected);
  });

  it("truncates to the label length limit", () => {
    expect(sanitizeAppName("a".repeat(100))).toHaveLength(63);
  });
});
