import type { NatsConnection } from "@nats-io/nats-core";
import { connect } from "@nats-io/transport-node";

export function natsOptions() {
  const servers =
    process.env.NATS_URL ?? process.env.NATS_BROKER ?? "nats://localhost:4222";
  const user = process.env.NATS_USER;
  const token = process.env.NATS_TOKEN;

  return {
    servers,
    ...(user ? { user, pass: token ?? "" } : token ? { token } : {}),
  };
}

export function redact(url: string): string {
  return url.replace(/\/\/[^@/]*@/, "//***@");
}

export async function connectToNats(): Promise<NatsConnection> {
  const options = natsOptions();
  // Read the URL before connecting: connect() normalises `servers` into an array in place.
  const url = redact(options.servers);
  const nats = await connect(options);
  console.log(`[Editor] Connected to NATS at ${url}`);
  return nats;
}
