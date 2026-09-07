import { create, fromBinary, toBinary } from "@bufbuild/protobuf";
import type { NatsConnection } from "@nats-io/nats-core";
import {
  CommandBatchSchema,
  type StructuralCommand,
} from "@ecs/protos/ecs/protocol/v1/tick_pb.js";
import {
  RegisterSchemasRequestSchema,
  RegisterSchemasResponseSchema,
  type ComponentTypeDeclaration,
} from "@ecs/protos/ecs/protocol/v1/schema_pb.js";
import { QuerySystemsRequestSchema } from "@ecs/protos/ecs/protocol/v1/world_pb.js";
import { Subjects } from "./protocol/index.js";
import { SchemaBindings } from "./query-types.js";

const REQUEST_TIMEOUT_MS = 5000;

export class SchemaRegistrationError extends Error {}

/**
 * The client half of the control plane: the schema handshake and out-of-band commands.
 *
 * Nothing else may talk to the coordinator until schemas are registered. That ordering
 * is the point — the world assigns the dense type ids, so a query, a batch or a command
 * that has not been through here has nothing meaningful to say.
 */
export class CoordinatorClient {
  readonly bindings = new SchemaBindings();

  constructor(
    private readonly nats: NatsConnection,
    private readonly label: string,
  ) {}

  /** Polls the coordinator until it answers, so nothing published afterwards is lost. */
  async waitForCoordinator(signal?: AbortSignal): Promise<void> {
    let announced = false;

    while (signal?.aborted !== true) {
      try {
        await this.nats.request(
          Subjects.querySystems,
          toBinary(QuerySystemsRequestSchema, create(QuerySystemsRequestSchema)),
          { timeout: 2000 },
        );
        return;
      } catch {
        if (!announced) {
          announced = true;
          console.log(`[${this.label}] Waiting for coordinator...`);
        }
        await delay(250, signal);
      }
    }
  }

  /**
   * Registers schemas the coordinator has not already bound and records the ids it
   * assigns. A rejection is fatal: the alternative is a system quietly reading or
   * writing something other than what it was built against.
   */
  async registerSchemas(declarations: ComponentTypeDeclaration[]): Promise<void> {
    const pending: ComponentTypeDeclaration[] = [];
    const seen = new Set<string>();

    for (const declaration of declarations) {
      const name = declaration.type?.logicalName;
      if (name === undefined) continue;
      if (this.bindings.tryGetId(name) !== undefined || seen.has(name)) continue;
      seen.add(name);
      pending.push(declaration);
    }

    if (pending.length === 0) return;

    const request = create(RegisterSchemasRequestSchema, { declarations: pending });
    const reply = await this.nats.request(
      Subjects.schemaRegister,
      toBinary(RegisterSchemasRequestSchema, request),
      { timeout: REQUEST_TIMEOUT_MS },
    );

    const response = fromBinary(RegisterSchemasResponseSchema, reply.data);

    if (response.rejections.length > 0) {
      const detail = response.rejections
        .map((r) => `${r.type?.logicalName}: ${r.reason} (${r.detail})`)
        .join("; ");
      throw new SchemaRegistrationError(
        `The coordinator refused ${response.rejections.length} schema(s) — ${detail}`,
      );
    }

    for (const binding of response.bindings) {
      if (binding.type !== undefined) this.bindings.add(binding.type.logicalName, binding.typeId);
    }

    console.log(
      `[${this.label}] Registered ${response.bindings.length} schema(s): ` +
        response.bindings.map((b) => `${b.type?.logicalName}=${b.typeId}`).join(", "),
    );
  }

  /**
   * Sends commands out of band, registering any schema they touch first. Used for
   * seeding and one-off edits; a system's own commands ride back inside its tick result.
   */
  async submit(commands: StructuralCommand[], schemas: ComponentTypeDeclaration[]): Promise<void> {
    if (commands.length === 0) return;

    await this.registerSchemas(schemas);

    const batch = create(CommandBatchSchema, { commands });
    await this.nats.request(Subjects.worldCommand, toBinary(CommandBatchSchema, batch), {
      timeout: REQUEST_TIMEOUT_MS,
    });
  }
}

function delay(ms: number, signal?: AbortSignal): Promise<void> {
  return new Promise((resolve) => {
    const timer = setTimeout(resolve, ms);
    signal?.addEventListener(
      "abort",
      () => {
        clearTimeout(timer);
        resolve();
      },
      { once: true },
    );
  });
}
