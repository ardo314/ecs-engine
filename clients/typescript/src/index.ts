export {
  canonicalize,
  schemaHash,
  formatSchemaHash,
  fileDescriptorSetFor,
  encodeBatch,
  decodeBatch,
  Subjects,
  InvalidDescriptorSetError,
  type ComponentColumn,
} from "./protocol/index.js";

export { componentType, type ComponentType } from "./component-type.js";
export { describedBy } from "./description.js";
export { Query, SchemaBindings, type ComponentAccess } from "./query-types.js";
export { EntityQuery, TaggedComponent } from "./entity-query.js";
export {
  EntityCommandBuffer,
  Target,
  component,
  type ComponentInstance,
} from "./commands.js";
export { CoordinatorClient, SchemaRegistrationError } from "./coordinator.js";
export { SystemBase } from "./system.js";
export { SystemRunner } from "./runner.js";
export { Ecs, World } from "./world.js";
export { SchemaRegistry } from "./schema-registry.js";
