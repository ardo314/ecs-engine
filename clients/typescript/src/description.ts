import { create, getExtension, hasExtension } from "@bufbuild/protobuf";
import type { DescFile, DescMessage } from "@bufbuild/protobuf";
import type { ComponentValue } from "@ecs/protos/ecs/protocol/v1/schema_pb.js";
import { ComponentValueSchema } from "@ecs/protos/ecs/protocol/v1/schema_pb.js";
import { description } from "@ecs/protos/ecs/v1/component_pb.js";
import { fileDescriptorSetFor, schemaHash } from "./protocol/index.js";

/**
 * Reads the `ecs.v1.description` message option — the open set of contracts a component
 * type attaches to itself.
 *
 * Each attachment's own message type is resolved out of the declaring file's import
 * closure, so it carries the same exact schema identity as any other component. That is
 * what lets a domain say "this type is a Setting" without the engine knowing what a
 * Setting is.
 */
export function describedBy(schema: DescMessage): ComponentValue[] {
  const options = schema.proto.options;
  if (options === undefined || !hasExtension(options, description)) return [];

  return getExtension(options, description).map((attachment) => {
    const typeUrl = attachment.typeUrl;
    const name = typeUrl.slice(typeUrl.lastIndexOf("/") + 1);

    const target = lookup(schema.file, name, new Set());
    if (target === undefined) {
      throw new Error(
        `'${schema.typeName}' describes itself with '${name}', but that type is not ` +
          `reachable from '${schema.file.proto.name}'. Import the file that defines it.`,
      );
    }

    const set = fileDescriptorSetFor(target);
    return create(ComponentValueSchema, {
      type: { logicalName: target.typeName, schemaHash: schemaHash(set, target.typeName) },
      payload: attachment.value,
    });
  });
}

function lookup(file: DescFile, fullName: string, seen: Set<string>): DescMessage | undefined {
  if (seen.has(file.proto.name)) return undefined;
  seen.add(file.proto.name);

  const found = search(file.messages, fullName);
  if (found !== undefined) return found;

  for (const dependency of file.dependencies) {
    const nested = lookup(dependency, fullName, seen);
    if (nested !== undefined) return nested;
  }
  return undefined;
}

function search(messages: readonly DescMessage[], fullName: string): DescMessage | undefined {
  for (const message of messages) {
    if (message.typeName === fullName) return message;
    const nested = search(message.nestedMessages, fullName);
    if (nested !== undefined) return nested;
  }
  return undefined;
}
