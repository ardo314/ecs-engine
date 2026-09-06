import { connect } from "@nats-io/transport-node";
import { Ecs, component } from "@ecs/client";
import { PositionSchema, VelocitySchema } from "@ecs/protos/movement/v1/movement_pb.js";
import { MovementSystem } from "./movement-system.js";

const servers = process.env.NATS_URL ?? process.env.NATS_BROKER ?? "nats://localhost:4222";
const nats = await connect({ servers });

const ecs = new Ecs(nats);
const world = ecs.world();

// Seeding is not system logic, so it happens outside any system.
if (process.env.SEED !== "0") {
  for (let i = 0; i < 5; i++) {
    world.commands.createEntity(
      component(PositionSchema, { $typeName: PositionSchema.typeName, x: 0, y: 0, z: 0 }),
      component(VelocitySchema, {
        $typeName: VelocitySchema.typeName,
        x: 1,
        y: 0.5,
        z: 0.25,
      }),
    );
  }
  await world.flush();
  console.log("[Movement/ts] Seeded 5 entities.");
}

world.addSystem(new MovementSystem());

const shutdown = async () => {
  await ecs.shutdown();
  await nats.close();
  process.exit(0);
};

process.on("SIGINT", () => void shutdown());
process.on("SIGTERM", () => void shutdown());
