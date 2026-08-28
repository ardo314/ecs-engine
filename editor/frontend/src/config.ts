// Injected at container start by docker/25-editor-config.sh; see public/config.js for the dev default.
const configured =
  import.meta.env.VITE_EDITOR_BACKEND_URL ||
  window.__EDITOR_CONFIG__?.backendUrl ||
  "http://localhost:5000";

export const BACKEND_URL = configured.replace(/\/+$/, "");
