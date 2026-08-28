import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig({
  plugins: [react()],
  // Relative so the same build can be served from any deploy-time base path.
  base: "./",
  server: {
    port: 3000,
  },
});
