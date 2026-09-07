import { create, toBinary, type DescMessage, type MessageShape } from "@bufbuild/protobuf";
import type {
  ComponentTypeDeclaration,
  ComponentTypeRef,
  ComponentValue,
} from "@ecs/protos/ecs/protocol/v1/schema_pb.js";
import {
  ComponentTypeDeclarationSchema,
  ComponentTypeRefSchema,
  ComponentValueSchema,
} from "@ecs/protos/ecs/protocol/v1/schema_pb.js";
import { fileDescriptorSetFor, schemaHash } from "./protocol/index.js";
import { describedBy } from "./description.js";

/**
 * Everything the SDK knows about a component type, resolved once.
 *
 * This is where a generated protobuf message becomes an ECS component. Identity is
 * derived from the descriptors the compiler already produced, so there is nothing to
 * hand-maintain and nothing to get out of step: the name, the schema hash and the
 * descriptor set are all functions of the `.proto` file.
 */
export interface ComponentType<Desc extends DescMessage> {
  readonly schema: Desc;
  readonly name: string;
  readonly schemaHash: bigint;
  readonly fileDescriptorSet: Uint8Array;
  readonly ref: ComponentTypeRef;
  readonly declaration: ComponentTypeDeclaration;
  value(component: MessageShape<Desc>): ComponentValue;
}

const cache = new Map<string, ComponentType<DescMessage>>();

/** Resolves a generated message schema into a component type, once per process. */
export function componentType<Desc extends DescMessage>(schema: Desc): ComponentType<Desc> {
  const cached = cache.get(schema.typeName);
  if (cached !== undefined) return cached as ComponentType<Desc>;

  const fileDescriptorSet = fileDescriptorSetFor(schema);
  const hash = schemaHash(fileDescriptorSet, schema.typeName);

  const ref = create(ComponentTypeRefSchema, {
    logicalName: schema.typeName,
    schemaHash: hash,
  });

  const declaration = create(ComponentTypeDeclarationSchema, {
    type: ref,
    fileDescriptorSet,
    description: describedBy(schema),
  });

  const resolved: ComponentType<Desc> = {
    schema,
    name: schema.typeName,
    schemaHash: hash,
    fileDescriptorSet,
    ref,
    declaration,
    value: (component) =>
      create(ComponentValueSchema, { type: ref, payload: toBinary(schema, component) }),
  };

  cache.set(schema.typeName, resolved as ComponentType<DescMessage>);
  return resolved;
}
