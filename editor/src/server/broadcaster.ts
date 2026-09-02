import type { WSContext } from "hono/ws";

/** Tracks connected WebSocket clients and broadcasts snapshots to all of them. */
export class Broadcaster {
  private readonly clients = new Set<WSContext>();
  private cached: string | null = null;

  get cachedSnapshot(): string | null {
    return this.cached;
  }

  add(client: WSContext): void {
    this.clients.add(client);
  }

  remove(client: WSContext): void {
    this.clients.delete(client);
  }

  broadcast(json: string): void {
    this.cached = json;
    for (const client of this.clients) {
      try {
        client.send(json);
      } catch {
        this.clients.delete(client);
      }
    }
  }
}
