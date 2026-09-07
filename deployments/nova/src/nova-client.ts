import type { AppManifest } from "./manifest.js";

export class NovaApiError extends Error {}

export type FetchLike = (url: string, init?: RequestInit) => Promise<Response>;

const REQUEST_TIMEOUT_MS = 60_000;

/**
 * Talks to the NOVA app endpoints under `/api/v2/cells/{cell}/apps`. NOVA exposes no direct
 * Kubernetes access, so this REST API is the only way to place workloads on an instance.
 */
export class NovaAppClient {
  private readonly appsUrl: string;
  private readonly headers: Record<string, string>;

  constructor(
    baseUrl: string,
    cell: string,
    accessToken: string | undefined,
    private readonly fetchImpl: FetchLike = globalThis.fetch,
  ) {
    this.appsUrl = `${baseUrl.replace(/\/+$/, "")}/api/v2/cells/${encodeURIComponent(cell)}/apps`;
    this.headers = accessToken !== undefined && accessToken.trim().length > 0
      ? { Authorization: `Bearer ${accessToken}` }
      : {};
  }

  /** Names of the apps currently installed in the cell. */
  async listAppNames(): Promise<string[]> {
    const response = await this.send(this.appsUrl, {}, "list apps");
    const body = await response.text();
    if (body.trim().length === 0) return [];

    return extractNames(JSON.parse(body));
  }

  async addApp(manifest: AppManifest): Promise<void> {
    await this.send(
      this.appsUrl,
      {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(manifest),
      },
      `install app '${manifest.name}'`,
    );
  }

  /** Returns false when the app was already absent. */
  async deleteApp(name: string): Promise<boolean> {
    const url = `${this.appsUrl}/${encodeURIComponent(name)}`;
    const response = await this.send(url, { method: "DELETE" }, `delete app '${name}'`, [404]);
    return response.status !== 404;
  }

  /**
   * Deletion is asynchronous on the instance, so reinstalling immediately can collide with
   * the outgoing app.
   */
  async waitUntilAbsent(name: string, timeoutMs: number): Promise<void> {
    const deadline = Date.now() + timeoutMs;

    while (Date.now() < deadline) {
      if (!(await this.listAppNames()).includes(name)) return;
      await new Promise((resolve) => setTimeout(resolve, 1000));
    }

    throw new NovaApiError(
      `App '${name}' was still present ${Math.round(timeoutMs / 1000)}s after deletion.`,
    );
  }

  private async send(
    url: string,
    init: RequestInit,
    action: string,
    tolerate: number[] = [],
  ): Promise<Response> {
    let response: Response;
    try {
      response = await this.fetchImpl(url, {
        ...init,
        headers: { ...this.headers, ...init.headers },
        signal: AbortSignal.timeout(REQUEST_TIMEOUT_MS),
      });
    } catch (error) {
      throw new NovaApiError(`Could not reach the NOVA API: ${message(error)}`, { cause: error });
    }

    if (response.ok || tolerate.includes(response.status)) return response;

    const body = await response.text().catch(() => "");
    throw new NovaApiError(
      `Failed to ${action}: ${response.status} ${response.statusText}. ${body}`.trimEnd(),
    );
  }
}

/**
 * The list response shape is not pinned by the public docs, so accept both a bare array and
 * an object wrapping one.
 */
function extractNames(root: unknown): string[] {
  const array = Array.isArray(root) ? root : findArray(root);
  if (array === undefined) return [];

  return array
    .map((element) => {
      if (typeof element === "string") return element;
      if (typeof element === "object" && element !== null && "name" in element) {
        const name = (element as { name: unknown }).name;
        return typeof name === "string" ? name : "";
      }
      return "";
    })
    .filter((name) => name.length > 0);
}

function findArray(root: unknown): unknown[] | undefined {
  if (typeof root !== "object" || root === null) return undefined;
  return Object.values(root).find((v): v is unknown[] => Array.isArray(v));
}

function message(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
