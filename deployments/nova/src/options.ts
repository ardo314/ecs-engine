export class InstallerConfigurationError extends Error {}

/** A system image to install, plus the app name it is deployed under. */
export interface SystemImage {
  name: string;
  image: string;
}

/** Installer configuration, read entirely from environment variables. */
export interface InstallerOptions {
  novaBaseUrl: string;
  accessToken: string;
  cell: string;
  appPrefix: string;
  engineImage: string;
  editorImage: string;
  systemImages: SystemImage[];
  installEditor: boolean;
  /**
   * The broker address passed to every app as NATS_URL. Defaults to the NATS_BROKER value
   * NOVA injects into the installer's own container — nothing else in the stack knows that
   * variable exists.
   */
  natsUrl?: string;
  tickRate: number;
  registryUser?: string;
  registryPassword?: string;
  dryRun: boolean;
}

export const DEFAULT_REGISTRY = "ghcr.io/ardo314/ecs-engine";

/**
 * Baked into the image by the Dockerfile so the installer deploys the images built from its
 * own revision instead of whatever `latest` currently points at.
 */
export const DEFAULT_IMAGE_TAG = process.env.ECS_DEFAULT_IMAGE_TAG?.trim() || "latest";

export type ReadEnv = (key: string) => string | undefined;

export function readOptions(read: ReadEnv): InstallerOptions {
  const registry = value(read, "ECS_IMAGE_REGISTRY") ?? DEFAULT_REGISTRY;
  const tag = value(read, "ECS_IMAGE_TAG") ?? DEFAULT_IMAGE_TAG;
  const appPrefix = sanitizeAppName(value(read, "ECS_APP_PREFIX") ?? "ecs");
  if (appPrefix.length === 0)
    throw new InstallerConfigurationError("ECS_APP_PREFIX must contain at least one alphanumeric character.");

  return {
    novaBaseUrl: normalizeBaseUrl(value(read, "NOVA_BASE_URL") ?? value(read, "NOVA_API") ?? "http://localhost:80"),
    accessToken: value(read, "NOVA_ACCESS_TOKEN") ?? "",
    cell: value(read, "NOVA_CELL") ?? value(read, "CELL_NAME") ?? "cell",
    appPrefix,
    engineImage: value(read, "ECS_ENGINE_IMAGE") ?? `${registry}/engine:${tag}`,
    editorImage: value(read, "ECS_EDITOR_IMAGE") ?? `${registry}/editor:${tag}`,
    systemImages: parseSystemImages(value(read, "ECS_SYSTEM_IMAGES")),
    installEditor: parseBool(value(read, "ECS_INSTALL_EDITOR"), true),
    natsUrl: value(read, "ECS_NATS_URL") ?? value(read, "NATS_BROKER"),
    tickRate: parseTickRate(value(read, "ECS_TICK_RATE")),
    registryUser: value(read, "ECS_REGISTRY_USER"),
    registryPassword: value(read, "ECS_REGISTRY_PASSWORD"),
    dryRun: parseBool(value(read, "ECS_DRY_RUN"), false),
  };
}

/**
 * Parses a comma-separated list where each entry is `name=image` or a bare image reference
 * whose repository segment becomes the app name.
 */
export function parseSystemImages(raw: string | undefined): SystemImage[] {
  if (raw === undefined || raw.trim().length === 0) return [];

  const result: SystemImage[] = [];
  const seen = new Set<string>();

  for (const entry of raw.split(",").map((e) => e.trim()).filter((e) => e.length > 0)) {
    const separator = entry.indexOf("=");
    const image = separator < 0 ? entry : entry.slice(separator + 1).trim();
    const rawName = separator < 0 ? deriveName(image) : entry.slice(0, separator).trim();

    if (image.length === 0)
      throw new InstallerConfigurationError(`ECS_SYSTEM_IMAGES entry '${entry}' has no image reference.`);

    const name = sanitizeAppName(rawName);
    if (name.length === 0)
      throw new InstallerConfigurationError(`ECS_SYSTEM_IMAGES entry '${entry}' yields an empty app name.`);

    if (seen.has(name))
      throw new InstallerConfigurationError(`ECS_SYSTEM_IMAGES contains duplicate system name '${name}'.`);

    seen.add(name);
    result.push({ name, image });
  }

  return result;
}

/**
 * Reduces a NOVA address to the instance root. NOVA injects `NOVA_API` pointing at the API
 * root (`…/api/v1`) and sometimes without a scheme, while NovaAppClient appends its own
 * versioned path.
 */
export function normalizeBaseUrl(raw: string): string {
  const trimmed = raw.trim();
  const withScheme = trimmed.includes("://") ? trimmed : `http://${trimmed}`;

  let url: URL;
  try {
    url = new URL(withScheme);
  } catch {
    throw new InstallerConfigurationError(`'${raw}' is not a valid NOVA base URL.`);
  }

  if (url.protocol !== "http:" && url.protocol !== "https:")
    throw new InstallerConfigurationError(`'${raw}' is not a valid NOVA base URL.`);

  url.pathname = url.pathname.replace(/\/+$/, "").replace(/\/api(\/v\d+)?$/i, "");
  url.search = "";
  url.hash = "";

  return url.toString().replace(/\/+$/, "");
}

/** Takes the repository segment of an image reference, minus tag or digest. */
export function deriveName(image: string): string {
  const withoutDigest = image.split("@")[0];
  const lastSegment = withoutDigest.slice(withoutDigest.lastIndexOf("/") + 1);
  const colon = lastSegment.lastIndexOf(":");
  return colon < 0 ? lastSegment : lastSegment.slice(0, colon);
}

const MAX_LABEL_LENGTH = 63;

/** Reduces a string to an RFC 1035 label, as required for NOVA app names. */
export function sanitizeAppName(value: string): string {
  let built = "";

  for (const c of value.toLowerCase()) {
    if ((c >= "a" && c <= "z") || (c >= "0" && c <= "9")) built += c;
    else if (built.length > 0 && !built.endsWith("-")) built += "-";
  }

  let result = built.replace(/^-+/, "").replace(/-+$/, "");

  // A label must start with a letter.
  if (result.length > 0 && !(result[0] >= "a" && result[0] <= "z")) result = `a${result}`;

  return result.length > MAX_LABEL_LENGTH
    ? result.slice(0, MAX_LABEL_LENGTH).replace(/-+$/, "")
    : result;
}

function value(read: ReadEnv, key: string): string | undefined {
  const raw = read(key);
  return raw === undefined || raw.trim().length === 0 ? undefined : raw.trim();
}

function parseBool(raw: string | undefined, fallback: boolean): boolean {
  if (raw === undefined) return fallback;
  if (raw.toLowerCase() === "true" || raw === "1") return true;
  if (raw.toLowerCase() === "false" || raw === "0") return false;
  throw new InstallerConfigurationError(`Expected a boolean but got '${raw}'.`);
}

function parseTickRate(raw: string | undefined): number {
  if (raw === undefined) return 20;
  const parsed = Number(raw);
  if (!Number.isInteger(parsed) || parsed <= 0)
    throw new InstallerConfigurationError(`ECS_TICK_RATE must be a positive integer but was '${raw}'.`);
  return parsed;
}
