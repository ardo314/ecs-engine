import { sha256 } from "@noble/hashes/sha256";
import { fromBinary } from "@bufbuild/protobuf";
import {
  FileDescriptorSetSchema,
  type DescriptorProto,
  type EnumDescriptorProto,
  type FieldDescriptorProto,
} from "@bufbuild/protobuf/wkt";

/**
 * The exact identity of a component type's structure, per `protocol/SPEC.md` §1.
 *
 * Computed over `FileDescriptorProto` rather than over protobuf-es's own reflection
 * API. That is not a style choice: protobuf-es models a map field as
 * `fieldKind: "map"` and hides the synthetic entry message, whereas C# reports the
 * field as repeated and exposes the entry. Hashing what either runtime *sees* would
 * mean the two could never agree. The descriptor has one answer, so this walks the
 * descriptor and resolves references itself.
 */

const LABEL_REPEATED = 3;

const TYPE_NAMES: Record<number, string> = {
  1: "TYPE_DOUBLE",
  2: "TYPE_FLOAT",
  3: "TYPE_INT64",
  4: "TYPE_UINT64",
  5: "TYPE_INT32",
  6: "TYPE_FIXED64",
  7: "TYPE_FIXED32",
  8: "TYPE_BOOL",
  9: "TYPE_STRING",
  10: "TYPE_GROUP",
  11: "TYPE_MESSAGE",
  12: "TYPE_BYTES",
  13: "TYPE_UINT32",
  14: "TYPE_ENUM",
  15: "TYPE_SFIXED32",
  16: "TYPE_SFIXED64",
  17: "TYPE_SINT32",
  18: "TYPE_SINT64",
};

const TYPE_MESSAGE = 11;
const TYPE_GROUP = 10;
const TYPE_ENUM = 14;

export class InvalidDescriptorSetError extends Error {}

interface Indexed {
  messages: Map<string, DescriptorProto>;
  enums: Map<string, EnumDescriptorProto>;
  syntax: Map<string, string>;
}

/** A flat, fully-qualified view over a FileDescriptorSet. */
function index(fileDescriptorSet: Uint8Array): Indexed {
  let set;
  try {
    set = fromBinary(FileDescriptorSetSchema, fileDescriptorSet);
  } catch (cause) {
    throw new InvalidDescriptorSetError("FileDescriptorSet is not valid protobuf.", { cause });
  }

  const indexed: Indexed = { messages: new Map(), enums: new Map(), syntax: new Map() };

  const addEnum = (scope: string, e: EnumDescriptorProto, syntax: string) => {
    const fullName = qualify(scope, e.name);
    indexed.enums.set(fullName, e);
    indexed.syntax.set(fullName, syntax);
  };

  const addMessage = (scope: string, message: DescriptorProto, syntax: string) => {
    const fullName = qualify(scope, message.name);
    indexed.messages.set(fullName, message);
    indexed.syntax.set(fullName, syntax);

    for (const nested of message.nestedType) addMessage(fullName, nested, syntax);
    for (const e of message.enumType) addEnum(fullName, e, syntax);
  };

  for (const file of set.file) {
    // An absent `syntax` means proto2, which is what descriptor.proto itself is.
    const syntax = file.syntax === "" || file.syntax === undefined ? "proto2" : file.syntax;
    for (const message of file.messageType) addMessage(file.package, message, syntax);
    for (const e of file.enumType) addEnum(file.package, e, syntax);
  }

  return indexed;
}

function qualify(scope: string, name: string): string {
  return scope === "" ? name : `${scope}.${name}`;
}

/** `type_name` is fully qualified with a leading dot; the rendering is not. */
function strip(typeName: string): string {
  return typeName.startsWith(".") ? typeName.slice(1) : typeName;
}

/**
 * Only proto3 is specified. Rejecting anything else is deliberate: proto2 presence
 * rules and editions would each need their own canonical form.
 */
function requireProto3(indexed: Indexed, fullName: string): void {
  const syntax = indexed.syntax.get(fullName);
  if (syntax === undefined || syntax === "proto3") return;

  throw new InvalidDescriptorSetError(
    `'${fullName}' is declared in a ${syntax} file. Only proto3 is supported.`,
  );
}

function hasTypeName(type: number): boolean {
  return type === TYPE_MESSAGE || type === TYPE_GROUP || type === TYPE_ENUM;
}

function label(field: FieldDescriptorProto): string {
  if (field.label === LABEL_REPEATED) return "repeated";
  return field.proto3Optional === true ? "optional" : "singular";
}

/**
 * Which oneofs exist only to carry proto3 `optional`.
 *
 * Derived from the fields, not from the oneofs. The obvious definition — "a oneof
 * declared by exactly one proto3-optional field" — needs to know whether a field
 * declares `oneof_index` at all, and protobuf-es cannot say: descriptor.proto is
 * proto2, so an unset `oneof_index` reads as 0, which is indistinguishable from
 * membership of oneof 0. Reading it only for proto3-optional fields avoids the
 * question, because those always have it set.
 */
function syntheticOneofs(message: DescriptorProto): Set<number> {
  const synthetic = new Set<number>();
  for (const field of message.field) {
    if (field.proto3Optional === true) synthetic.add(field.oneofIndex ?? 0);
  }
  return synthetic;
}

/** Byte-wise; protobuf identifiers are ASCII, so this matches every language. */
function byName(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}

function renderMessage(message: DescriptorProto, fullName: string, out: string[]): void {
  out.push(`message ${fullName}\n`);

  for (const field of [...message.field].sort((a, b) => a.number - b.number)) {
    const typeName = TYPE_NAMES[field.type];
    if (typeName === undefined) {
      throw new InvalidDescriptorSetError(`Unknown protobuf field type '${field.type}'.`);
    }

    const suffix = hasTypeName(field.type) ? ` ${strip(field.typeName)}` : "";
    out.push(`field ${field.number} ${field.name} ${label(field)} ${typeName}${suffix}\n`);
  }

  const synthetic = syntheticOneofs(message);
  for (let i = 0; i < message.oneofDecl.length; i++) {
    if (synthetic.has(i)) continue;
    out.push(`oneof ${i} ${message.oneofDecl[i]!.name}\n`);
  }
}

function renderEnum(e: EnumDescriptorProto, fullName: string, out: string[]): void {
  out.push(`enum ${fullName}\n`);

  // Aliases share a number, so the name breaks the tie.
  const values = [...e.value].sort((a, b) => a.number - b.number || byName(a.name, b.name));
  for (const value of values) out.push(`value ${value.number} ${value.name}\n`);
}

/**
 * The canonical rendering the digest is taken over. Exported because a mismatch
 * between two implementations should be a diff, not a pair of 64-bit numbers.
 */
export function canonicalize(fileDescriptorSet: Uint8Array, logicalName: string): string {
  const indexed = index(fileDescriptorSet);

  const root = indexed.messages.get(logicalName);
  if (root === undefined) {
    throw new InvalidDescriptorSetError(
      `FileDescriptorSet does not contain a message named '${logicalName}'.`,
    );
  }

  const messages = new Set<string>();
  const enums = new Set<string>();
  const pending = [logicalName];

  // The closure is a set, so mutual recursion terminates.
  while (pending.length > 0) {
    const name = pending.pop()!;
    if (messages.has(name)) continue;
    messages.add(name);
    requireProto3(indexed, name);

    const message = indexed.messages.get(name);
    if (message === undefined) {
      throw new InvalidDescriptorSetError(
        `'${name}' is referenced but not present in the FileDescriptorSet.`,
      );
    }

    for (const field of message.field) {
      if (!hasTypeName(field.type)) continue;
      const target = strip(field.typeName);

      if (field.type === TYPE_ENUM) {
        if (!indexed.enums.has(target)) {
          throw new InvalidDescriptorSetError(
            `'${name}.${field.name}' references enum '${target}', which is not in the set.`,
          );
        }
        requireProto3(indexed, target);
        enums.add(target);
      } else {
        pending.push(target);
      }
    }
  }

  // The root is rendered first, which anchors the digest to it.
  messages.delete(logicalName);

  const out: string[] = [];
  renderMessage(root, logicalName, out);
  for (const name of [...messages].sort(byName)) {
    renderMessage(indexed.messages.get(name)!, name, out);
  }
  for (const name of [...enums].sort(byName)) {
    renderEnum(indexed.enums.get(name)!, name, out);
  }

  return out.join("");
}

/**
 * The 64-bit schema hash. A bigint, because uint64 exceeds JavaScript's safe integer
 * range and rounding a type's identity would be silently catastrophic.
 */
export function schemaHash(fileDescriptorSet: Uint8Array, logicalName: string): bigint {
  const digest = sha256(new TextEncoder().encode(canonicalize(fileDescriptorSet, logicalName)));
  const view = new DataView(digest.buffer, digest.byteOffset, 8);
  return view.getBigUint64(0, false);
}

/** Hex, zero-padded to 16 digits — the form the conformance vectors use. */
export function formatSchemaHash(hash: bigint): string {
  return `0x${hash.toString(16).padStart(16, "0")}`;
}
