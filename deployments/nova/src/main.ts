import { tryStartHealthEndpoint } from "./health.js";
import type { AppManifest } from "./manifest.js";
import { NovaApiError, NovaAppClient } from "./nova-client.js";
import { InstallerConfigurationError, type InstallerOptions, readOptions } from "./options.js";
import { planStack } from "./plan.js";

const DELETE_TIMEOUT_MS = 60_000;

process.on("SIGINT", () => {
  console.error("Cancelled.");
  process.exit(130);
});

try {
  process.exitCode = await main();
} catch (error) {
  if (error instanceof InstallerConfigurationError) {
    console.error(`Configuration error: ${error.message}`);
    process.exitCode = 2;
  } else if (error instanceof NovaApiError) {
    console.error(error.message);
    process.exitCode = 1;
  } else {
    throw error;
  }
}

async function main(): Promise<number> {
  const options = readOptions((key) => process.env[key]);
  const manifests = planStack(options);

  console.log(`NOVA instance : ${options.novaBaseUrl}`);
  console.log(`Cell          : ${options.cell}`);
  console.log(`Apps          : ${manifests.map((m) => m.name).join(", ")}`);

  if (options.dryRun) {
    console.log();
    for (const manifest of manifests) console.log(JSON.stringify(redact(manifest), null, 2));
    return 0;
  }

  if (options.accessToken.length === 0)
    console.log("Warning: NOVA_ACCESS_TOKEN is empty; requests will be unauthenticated.");

  // Started before the install so the probe is already answering while apps go in.
  const health = await tryStartHealthEndpoint();
  if (health !== null) console.log(`Health endpoint listening on http://localhost:${health.port}`);

  // An open listener keeps Node alive, so it has to go before the process can report a failure.
  try {
    await install(options, manifests);
  } catch (error) {
    await health?.close();
    throw error;
  }

  // NOVA injects BASE_PATH into every app it runs; a plain CLI run has none and exits.
  if ((process.env.BASE_PATH ?? "").length === 0) {
    await health?.close();
    return 0;
  }

  // NOVA restarts an app that stops answering its probe, which would reinstall the stack.
  console.log("Install complete; serving health probes until stopped.");
  await new Promise(() => {});
  return 0;
}

async function install(options: InstallerOptions, manifests: AppManifest[]): Promise<void> {
  const client = new NovaAppClient(options.novaBaseUrl, options.cell, options.accessToken);
  const existing = new Set(await client.listAppNames());

  console.log();
  for (const manifest of manifests) {
    if (existing.has(manifest.name)) {
      console.log(`replacing  ${manifest.name}`);
      await client.deleteApp(manifest.name);
      await client.waitUntilAbsent(manifest.name, DELETE_TIMEOUT_MS);
    } else {
      console.log(`installing ${manifest.name}`);
    }

    await client.addApp(manifest);
  }

  console.log(`\nDone. ${manifests.length} apps installed in cell '${options.cell}'.`);
}

function redact(manifest: AppManifest): AppManifest {
  const credentials = manifest.container_image.credentials;
  if (credentials === undefined) return manifest;

  return {
    ...manifest,
    container_image: {
      ...manifest.container_image,
      credentials: { ...credentials, password: "***" },
    },
  };
}
