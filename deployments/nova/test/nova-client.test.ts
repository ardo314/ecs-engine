import { describe, expect, it, vi } from "vitest";
import { NovaApiError, NovaAppClient } from "../src/nova-client.js";

function client(respond: (url: string, init?: RequestInit) => Response) {
  const fetchImpl = vi.fn(async (url: string, init?: RequestInit) => respond(url, init));
  return { fetchImpl, nova: new NovaAppClient("https://nova.example.com", "cell a", "t0ken", fetchImpl) };
}

const json = (body: unknown) => new Response(JSON.stringify(body), { status: 200 });

describe("listAppNames", () => {
  it("reads a bare array of names", async () => {
    const { nova } = client(() => json(["ecs-engine", "ecs-editor"]));

    expect(await nova.listAppNames()).toEqual(["ecs-engine", "ecs-editor"]);
  });

  // The list response shape is not pinned by the public docs.
  it("reads an array wrapped in an object", async () => {
    const { nova } = client(() => json({ total: 1, apps: [{ name: "ecs-engine" }] }));

    expect(await nova.listAppNames()).toEqual(["ecs-engine"]);
  });

  it("skips elements that carry no name", async () => {
    const { nova } = client(() => json(["ecs-engine", { id: 7 }, { name: "" }, 42]));

    expect(await nova.listAppNames()).toEqual(["ecs-engine"]);
  });

  it("treats an empty body as no apps", async () => {
    const { nova } = client(() => new Response("", { status: 200 }));

    expect(await nova.listAppNames()).toEqual([]);
  });

  it("escapes the cell name into the path and sends the bearer token", async () => {
    const { nova, fetchImpl } = client(() => json([]));

    await nova.listAppNames();

    expect(fetchImpl.mock.calls[0][0]).toBe("https://nova.example.com/api/v2/cells/cell%20a/apps");
    expect(fetchImpl.mock.calls[0][1]?.headers).toMatchObject({ Authorization: "Bearer t0ken" });
  });

  it("sends no authorization header when there is no token", async () => {
    const fetchImpl = vi.fn(async () => json([]));
    await new NovaAppClient("https://nova.example.com", "cell", "", fetchImpl).listAppNames();

    expect(fetchImpl.mock.calls[0][1]?.headers).not.toHaveProperty("Authorization");
  });

  it("reports a failed request", async () => {
    const { nova } = client(() => new Response("nope", { status: 500, statusText: "Server Error" }));

    await expect(nova.listAppNames()).rejects.toThrow(NovaApiError);
  });

  it("reports an unreachable instance", async () => {
    const { nova } = client(() => {
      throw new TypeError("fetch failed");
    });

    await expect(nova.listAppNames()).rejects.toThrow(/Could not reach the NOVA API/);
  });
});

describe("deleteApp", () => {
  it("reports true when the app was there", async () => {
    const { nova } = client(() => new Response(null, { status: 204 }));

    expect(await nova.deleteApp("ecs-engine")).toBe(true);
  });

  it("reports false when the app was already absent", async () => {
    const { nova } = client(() => new Response(null, { status: 404 }));

    expect(await nova.deleteApp("ecs-engine")).toBe(false);
  });
});

describe("addApp", () => {
  it("posts the manifest as json", async () => {
    const { nova, fetchImpl } = client(() => new Response(null, { status: 201 }));

    await nova.addApp({ name: "ecs-engine", container_image: { image: "ghcr.io/acme/engine:1" } });

    const init = fetchImpl.mock.calls[0][1];
    expect(init?.method).toBe("POST");
    expect(JSON.parse(init?.body as string)).toEqual({
      name: "ecs-engine",
      container_image: { image: "ghcr.io/acme/engine:1" },
    });
  });
});

describe("waitUntilAbsent", () => {
  it("returns once the app has gone", async () => {
    let calls = 0;
    const { nova } = client(() => json(calls++ === 0 ? ["ecs-engine"] : []));

    await expect(nova.waitUntilAbsent("ecs-engine", 5_000)).resolves.toBeUndefined();
  });

  it("gives up when the app outlives the deadline", async () => {
    const { nova } = client(() => json(["ecs-engine"]));

    await expect(nova.waitUntilAbsent("ecs-engine", 0)).rejects.toThrow(NovaApiError);
  });
});
