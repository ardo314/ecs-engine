import type { AppManifest, ContainerImage, EnvVar } from "./manifest.js";
import type { InstallerOptions, SystemImage } from "./options.js";

// NOVA joins these onto the app's BASE_PATH, which already ends in a slash, so both
// are relative: a leading slash makes the probe arrive as /<cell>/<app>//health.
export const ICON_PATH = "app_icon.png";
export const PROBE_PATH = "health";

/** Every deployment target probes this port, so it is the default everywhere. */
export const DEFAULT_PORT = 8080;

/**
 * Turns installer options into the ordered list of NOVA apps that make up the ECS stack.
 * Order matters: the coordinator goes in before anything that talks to it. NATS is provided
 * by the NOVA instance, so no broker is installed.
 */
export function planStack(options: InstallerOptions): AppManifest[] {
  const apps: AppManifest[] = [engine(options)];

  if (options.installEditor) apps.push(editor(options));

  apps.push(...options.systemImages.map((system) => systemApp(options, system)));
  return apps;
}

function engine(options: InstallerOptions): AppManifest {
  return {
    name: `${options.appPrefix}-engine`,
    app_icon: ICON_PATH,
    container_image: image(options, options.engineImage),
    port: DEFAULT_PORT,
    health_path: PROBE_PATH,
    environment: [...natsUrl(options), { name: "TICK_RATE", value: String(options.tickRate) }],
  };
}

// One app: the editor serves its own UI and API from the same origin, so there is
// no backend URL to wire up.
function editor(options: InstallerOptions): AppManifest {
  return {
    name: `${options.appPrefix}-editor`,
    app_icon: ICON_PATH,
    container_image: image(options, options.editorImage),
    port: DEFAULT_PORT,
    health_path: PROBE_PATH,
    environment: [...natsUrl(options)],
  };
}

function systemApp(options: InstallerOptions, system: SystemImage): AppManifest {
  return {
    name: `${options.appPrefix}-${system.name}`,
    app_icon: ICON_PATH,
    container_image: image(options, system.image),
    port: DEFAULT_PORT,
    health_path: PROBE_PATH,
    environment: [...natsUrl(options)],
  };
}

/**
 * NOVA's NATS_BROKER stops here: the installer resolves it and passes the address on as
 * NATS_URL, the only broker variable the rest of the stack reads. Omitted when unknown,
 * leaving containers on their local default.
 */
function natsUrl(options: InstallerOptions): EnvVar[] {
  return options.natsUrl === undefined ? [] : [{ name: "NATS_URL", value: options.natsUrl }];
}

function image(options: InstallerOptions, reference: string): ContainerImage {
  if (options.registryUser === undefined || options.registryPassword === undefined)
    return { image: reference };

  return {
    image: reference,
    credentials: {
      registry: reference.split("/")[0],
      user: options.registryUser,
      password: options.registryPassword,
    },
  };
}
