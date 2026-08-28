import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// Declared locally so the config type-checks without pulling in @types/node.
declare const process: { env: Record<string, string | undefined> };

function normalizeBase(raw?: string): string {
  if (!raw || raw === "/") return "/";
  const trimmed = raw.replace(/^\/+|\/+$/g, "");
  return trimmed ? `/${trimmed}/` : "/";
}

export default defineConfig({
  plugins: [react()],
  base: normalizeBase(process.env.BASE_PATH),
  server: {
    port: 3000,
  },
});
