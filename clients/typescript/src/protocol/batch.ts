import { create } from "@bufbuild/protobuf";
import {
  BatchEncoding,
  ComponentBatchSchema,
  type ComponentBatch,
} from "@ecs/protos/ecs/protocol/v1/tick_pb.js";

/**
 * Component batch encoding, per `protocol/SPEC.md` §3.
 */

/**
 * A column of one component type over a slice of entities. `null` means the entity
 * does not carry the component — an empty protobuf message is zero bytes, so length
 * cannot say.
 */
export interface ComponentColumn {
  typeId: number;
  entities: bigint[];
  rows: (Uint8Array | null)[];
}

export function encodeBatch(column: ComponentColumn): ComponentBatch {
  const count = Math.min(column.entities.length, column.rows.length);
  const entities: bigint[] = [];
  const rows: Uint8Array[] = [];
  const present: boolean[] = [];

  for (let i = 0; i < count; i++) {
    const row = column.rows[i]!;
    entities.push(column.entities[i]!);
    present.push(row !== null);
    rows.push(row ?? new Uint8Array(0));
  }

  return create(ComponentBatchSchema, {
    typeId: column.typeId,
    encoding: BatchEncoding.PROTOBUF,
    entities,
    rows,
    present,
  });
}

export function decodeBatch(batch: ComponentBatch): ComponentColumn {
  // UNSPECIFIED can only mean protobuf: the field was added alongside that codec.
  if (batch.encoding !== BatchEncoding.PROTOBUF && batch.encoding !== BatchEncoding.UNSPECIFIED) {
    throw new Error(`No component batch codec for encoding '${batch.encoding}'.`);
  }

  const count = Math.min(batch.entities.length, batch.rows.length);
  const entities: bigint[] = [];
  const rows: (Uint8Array | null)[] = [];

  for (let i = 0; i < count; i++) {
    entities.push(batch.entities[i]!);
    // A batch written before the presence bitmap existed has none at all.
    const present = i >= batch.present.length || batch.present[i] === true;
    rows.push(present ? batch.rows[i]! : null);
  }

  return { typeId: batch.typeId, entities, rows };
}
