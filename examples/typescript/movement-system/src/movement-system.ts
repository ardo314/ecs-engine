import { Query, SystemBase, type EntityQuery } from "@ecs/client";
import { PositionSchema, VelocitySchema } from "@ecs/protos/movement/v1/movement_pb.js";

/**
 * The same system as `examples/MovementSystem`, in TypeScript.
 *
 * It declares the same component types, by the same protobuf full names, and the
 * coordinator cannot tell which language it is written in — which is the point.
 */
export class MovementSystem extends SystemBase {
  private readonly moving: EntityQuery;

  constructor() {
    super();
    this.moving = this.newQuery()
      .with(Query.write(PositionSchema))
      .with(Query.read(VelocitySchema));
  }

  protected override onUpdate(): void {
    for (const entity of this.moving.entities) {
      const position = this.moving.get(entity, PositionSchema);
      const velocity = this.moving.get(entity, VelocitySchema);

      this.moving.set(entity, PositionSchema, {
        $typeName: PositionSchema.typeName,
        x: position.x + velocity.x * this.deltaTime,
        y: position.y + velocity.y * this.deltaTime,
        z: position.z + velocity.z * this.deltaTime,
      });
    }

    if (this.tickId % 20n === 0n && this.moving.entities.length > 0) {
      const first = this.moving.entities[0]!;
      const position = this.moving.get(first, PositionSchema);
      console.log(
        `[Movement/ts] Tick ${this.tickId} | Entity ${first} Position: ` +
          `(${position.x.toFixed(2)}, ${position.y.toFixed(2)}, ${position.z.toFixed(2)})`,
      );
    }
  }
}
