import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

const BACKEND = process.env.EDITOR_DEV_BACKEND ?? "http://localhost:8080";

export default defineConfig({
  plugins: [react()],
  // Relative so the same build can be served from any deploy-time base path.
  base: "./",
  build: {
    outDir: "dist/client",
    emptyOutDir: true,
  },
  server: {
    port: 3000,
    proxy: {
      "/api": BACKEND,
      "/ws": { target: BACKEND, ws: true },
    },
  },
});
