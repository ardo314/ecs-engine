// Injected at container start by docker/25-editor-config.sh; see public/config.js for the dev default.
const configured =
  import.meta.env.VITE_EDITOR_BACKEND_URL ||
  window.__EDITOR_CONFIG__?.backendUrl ||
  "http://localhost:5000";

// Resolved against the page origin so a path-only value works behind a reverse proxy.
export const BACKEND_URL = new URL(configured, window.location.origin).href.replace(
  /\/+$/,
  "",
);
