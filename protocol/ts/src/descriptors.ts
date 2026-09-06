import { create, toBinary } from "@bufbuild/protobuf";
import { FileDescriptorSetSchema } from "@bufbuild/protobuf/wkt";
import type { DescFile, DescMessage } from "@bufbuild/protobuf";

/**
 * Building the descriptor set a schema travels in, per `protocol/SPEC.md` §2.
 */

/**
 * The transitively closed FileDescriptorSet for a message's own file, ordered
 * dependencies-first.
 *
 * The ordering is not cosmetic: C#'s `FileDescriptor.BuildFromByteStrings` and
 * Python's `DescriptorPool.Add` both walk the set in order and fail on a forward
 * reference, so a set that is merely complete is not enough.
 */
export function fileDescriptorSetFor(message: DescMessage): Uint8Array {
  const files: DescFile["proto"][] = [];
  const seen = new Set<string>();

  const collect = (file: DescFile): void => {
    if (seen.has(file.proto.name)) return;
    seen.add(file.proto.name);
    for (const dependency of file.dependencies) collect(dependency);
    files.push(file.proto);
  };

  collect(message.file);
  return toBinary(FileDescriptorSetSchema, create(FileDescriptorSetSchema, { file: files }));
}
