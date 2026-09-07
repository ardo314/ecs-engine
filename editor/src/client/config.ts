// The client is served from the same origin as the API, so everything is relative.
export const API_BASE = new URL(".", document.baseURI).pathname.replace(/\/$/, "");

export const WS_URL = `${window.location.origin}${API_BASE}/ws`.replace(/^http/, "ws");
