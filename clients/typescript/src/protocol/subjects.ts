/**
 * The NATS subject hierarchy, per `protocol/SPEC.md` §4.
 *
 * Written down once per language rather than inlined at call sites: a subject typo
 * is a message that silently goes nowhere.
 */

const PREFIX = "engine";

export const Subjects = {
  /** Request/reply. Binds component schemas to dense type ids. */
  schemaRegister: `${PREFIX}.schema.register`,

  systemRegister: `${PREFIX}.system.register`,
  systemUnregister: `${PREFIX}.system.unregister`,

  /** Queue group `systemName`, so exactly one instance receives each lease. */
  systemInvoke: (systemName: string) => `${PREFIX}.system.invoke.${systemName}`,

  systemResult: `${PREFIX}.system.result`,
  systemRejected: (instanceId: string) => `${PREFIX}.system.rejected.${instanceId}`,

  /** Request/reply. Structural commands submitted outside a tick. */
  worldCommand: `${PREFIX}.world.command`,

  querySystems: `${PREFIX}.world.query.systems`,
  queryEntities: `${PREFIX}.world.query.entities`,

  watchSubscribe: `${PREFIX}.world.watch.subscribe`,
  watchCancel: `${PREFIX}.world.watch.cancel`,
  watchData: (watchId: string) => `${PREFIX}.world.watch.data.${watchId}`,
} as const;
