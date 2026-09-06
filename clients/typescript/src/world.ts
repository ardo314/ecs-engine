import type { NatsConnection } from "@nats-io/nats-core";
import { CoordinatorClient } from "./coordinator.js";
import { EntityCommandBuffer } from "./commands.js";
import { SystemRunner } from "./runner.js";
import type { SystemBase } from "./system.js";

/**
 * Hosts the systems of a world. Add a constructed system to start it, remove it to shut
 * it down.
 */
export class World {
  private readonly running = new Map<SystemBase, { controller: AbortController; task: Promise<void> }>();
  private readonly coordinator: CoordinatorClient;

  /**
   * Structural changes made outside any system — seed data, fixtures, one-off edits.
   * Apply them with `flush`.
   */
  readonly commands = new EntityCommandBuffer();

  constructor(
    readonly name: string,
    private readonly nats: NatsConnection,
  ) {
    this.coordinator = new CoordinatorClient(nats, `world:${name}`);
  }

  /**
   * Applies everything buffered in `commands`, waiting for the coordinator first so the
   * commands are not published into the void.
   */
  async flush(): Promise<void> {
    if (!this.commands.hasPendingCommands) return;

    await this.coordinator.waitForCoordinator();
    await this.coordinator.submit(this.commands.drain(), this.commands.schemas);
  }

  /** Runs a system: invokes onAdd, then starts its tick loop in the background. */
  addSystem(system: SystemBase): void {
    if (this.running.has(system)) {
      throw new Error(`System '${system.systemName}' has already been added.`);
    }

    // onAdd runs here so authoring errors surface from addSystem itself.
    system.invokeOnAdd();

    const controller = new AbortController();
    const runner = new SystemRunner(system, this.nats);
    const task = runner
      .run(controller.signal)
      .catch((error: unknown) => {
        if (!controller.signal.aborted) {
          console.error(`[${system.systemName}] Faulted: ${String(error)}`);
          throw error;
        }
      })
      .finally(() => system.invokeOnRemove());

    this.running.set(system, { controller, task });
  }

  async removeSystem(system: SystemBase): Promise<void> {
    const entry = this.running.get(system);
    if (entry === undefined) return;

    this.running.delete(system);
    entry.controller.abort();
    await entry.task.catch(() => undefined);
  }

  async shutdown(): Promise<void> {
    await Promise.all([...this.running.keys()].map((s) => this.removeSystem(s)));
  }
}

/**
 * Entry point for the SDK. Owns nothing but the worlds; the caller owns the connection.
 */
export class Ecs {
  private readonly worlds = new Map<string, World>();

  constructor(readonly nats: NatsConnection) {}

  world(name = "default"): World {
    let world = this.worlds.get(name);
    if (world === undefined) {
      world = new World(name, this.nats);
      this.worlds.set(name, world);
    }
    return world;
  }

  async shutdown(): Promise<void> {
    await Promise.all([...this.worlds.values()].map((w) => w.shutdown()));
  }
}
