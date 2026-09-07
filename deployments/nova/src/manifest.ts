/**
 * Mirrors the body accepted by `POST /api/v2/cells/{cell}/apps`. Fields are named as the
 * API sends them, and optional ones are left `undefined` so `JSON.stringify` drops them.
 */
export interface AppManifest {
  name: string;
  app_icon?: string;
  container_image: ContainerImage;
  port?: number;
  health_path?: string;
  environment?: EnvVar[];
  storage?: AppStorage;
  resources?: AppResources;
}

export interface ContainerImage {
  image: string;
  credentials?: RegistryCredentials;
}

export interface RegistryCredentials {
  registry: string;
  user: string;
  password: string;
}

export interface EnvVar {
  name: string;
  value: string;
}

export interface AppStorage {
  mount_path: string;
  capacity: string;
}

export interface AppResources {
  memory_limit?: string;
  intel_gpu?: number;
}
