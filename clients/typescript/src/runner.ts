import { randomUUID } from "node:crypto";
import { create, fromBinary, toBinary } from "@bufbuild/protobuf";
import type { NatsConnection } from "@nats-io/nats-core";
import {
  SystemRegistrationSchema,
  SystemUnregistrationSchema,
} from "@ecs/protos/ecs/protocol/v1/query_pb.js";
import {
  ResultRejectedSchema,
  SystemInvocationSchema,
  SystemResultSchema,
  type ComponentBatch,
} from "@ecs/protos/ecs/protocol/v1/tick_pb.js";
import { CoordinatorClient } from "./coordinator.js";
import { decodeBatch, Subjects, type ComponentColumn } from "./protocol/index.js";
import type { SystemBase } from "./system.js";

/**
 * Connects a system to the coordinator and runs its tick loop.
 *
 * Startup is ordered: register schemas, bind queries to the ids that came back, then
 * announce the system. Only after that does the coordinator have something it can
 * schedule, and only then can a query descriptor mean anything.
 *
 * Each tick is a lease. The invocation says which entities and which writable types the
 * lease covers; the result quotes the lease back. Anything late, duplicated or outside
 * the slice is refused by the world rather than silently applied.
 */
export class SystemRunner {
  readonly instanceId = randomUUID().replace(/-/g, "");
  private readonly coordinator: CoordinatorClient;

  constructor(
    private readonly system: SystemBase,
    private readonly nats: NatsConnection,
  ) {
    this.coordinator = new CoordinatorClient(nats, system.systemName);
  }

  get systemName(): string {
    return this.system.systemName;
  }

  async run(signal: AbortSignal): Promise<void> {
    await this.coordinator.waitForCoordinator(signal);
    await this.coordinator.registerSchemas(this.system.declarations());
    this.system.bindQueries(this.coordinator.bindings);

    // Subscribe before announcing, so the first invocation cannot arrive unheard.
    const invocations = this.nats.subscribe(Subjects.systemInvoke(this.systemName), {
      queue: this.systemName,
    });
    const rejections = this.nats.subscribe(Subjects.systemRejected(this.instanceId));

    const stop = () => {
      invocations.unsubscribe();
      rejections.unsubscribe();
    };
    signal.addEventListener("abort", stop, { once: true });

    void this.reportRejections(rejections);

    const announcing = new AbortController();
    void this.announce(announcing.signal);

    // OnAdd commands describe types and seed data. They go out of band because a system
    // with no matching entities is never invoked, so there may be no tick to carry them.
    const commands = this.system.getCommands();
    await this.coordinator.submit(commands.drain(), commands.schemas);

    let announced = false;

    try {
      for await (const message of invocations) {
        if (!announced) {
          announced = true;
          announcing.abort();
          console.log(`[${this.systemName}] Registered — receiving ticks.`);
        }

        await this.executeTick(fromBinary(SystemInvocationSchema, message.data));
      }
    } finally {
      announcing.abort();
      signal.removeEventListener("abort", stop);
      await this.unregister();
      console.log(`[${this.systemName}] Shut down.`);
    }
  }

  private async executeTick(
    invocation: ReturnType<typeof fromBinary<typeof SystemInvocationSchema>>,
  ): Promise<void> {
    const columns = new Map<number, ComponentColumn>();
    for (const batch of invocation.components) {
      columns.set(batch.typeId, decodeBatch(batch));
    }

    for (const query of this.system.getQueries()) query.populate(columns, invocation);

    await this.system.invokeOnUpdate(invocation.tick, invocation.deltaSeconds);

    const writes: ComponentBatch[] = [];
    for (const query of this.system.getQueries()) writes.push(...query.flushWrites());

    // A system may add a component of a type nobody has registered yet. Bind it before
    // the command that needs it goes anywhere.
    const commands = this.system.getCommands();
    if (commands.hasPendingCommands) {
      await this.coordinator.registerSchemas(commands.schemas);
    }

    const result = create(SystemResultSchema, {
      tick: invocation.tick,
      leaseId: invocation.leaseId,
      instanceId: this.instanceId,
      writes,
      commands: commands.drain(),
    });

    this.nats.publish(Subjects.systemResult, toBinary(SystemResultSchema, result));
  }

  /**
   * Republishes the registration until the first invocation arrives. The coordinator
   * holds no durable system list, so a system that starts first must keep saying so.
   */
  private async announce(signal: AbortSignal): Promise<void> {
    const registration = create(SystemRegistrationSchema, {
      name: this.systemName,
      instanceId: this.instanceId,
      queries: this.system.queryDescriptors(),
    });
    const payload = toBinary(SystemRegistrationSchema, registration);

    console.log(
      `[${this.systemName}] Registering (instance ${this.instanceId}). ` +
        `Reads: [${this.system.readNames().join(", ")}], ` +
        `Writes: [${this.system.writeNames().join(", ")}]`,
    );

    while (!signal.aborted) {
      this.nats.publish(Subjects.systemRegister, payload);
      await sleep(1000, signal);
    }
  }

  private async reportRejections(
    subscription: AsyncIterable<{ data: Uint8Array }>,
  ): Promise<void> {
    try {
      for await (const message of subscription) {
        const rejected = fromBinary(ResultRejectedSchema, message.data);
        console.error(
          `[${this.systemName}] Tick ${rejected.tick} result refused: ` +
            `${rejected.reason} — ${rejected.detail}`,
        );
      }
    } catch {
      // Unsubscribed during shutdown.
    }
  }

  private async unregister(): Promise<void> {
    try {
      this.nats.publish(
        Subjects.systemUnregister,
        toBinary(
          SystemUnregistrationSchema,
          create(SystemUnregistrationSchema, {
            name: this.systemName,
            instanceId: this.instanceId,
          }),
        ),
      );
      await this.nats.flush();
    } catch {
      // Best effort: the coordinator drops unresponsive instances anyway.
    }
  }
}

function sleep(ms: number, signal: AbortSignal): Promise<void> {
  return new Promise((resolve) => {
    const timer = setTimeout(resolve, ms);
    signal.addEventListener(
      "abort",
      () => {
        clearTimeout(timer);
        resolve();
      },
      { once: true },
    );
  });
}
