import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

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
