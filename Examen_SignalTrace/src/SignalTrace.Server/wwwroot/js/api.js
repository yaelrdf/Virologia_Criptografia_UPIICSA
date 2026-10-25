// REST helper. The user session (JWT) lives in localStorage; all errors come back as { code, message }.
const AUTH_KEY = "st.auth";

export class ApiError extends Error {
  constructor(status, code, message) { super(message || code); this.status = status; this.code = code; }
}

function read(key) { try { return JSON.parse(localStorage.getItem(key) || "null"); } catch { return null; } }
function write(key, value) { try { value == null ? localStorage.removeItem(key) : localStorage.setItem(key, JSON.stringify(value)); } catch { /* private mode */ } }
export const store = { read, write };

export function getAuth() {
  const a = read(AUTH_KEY);
  return a && a.expiresAt > Date.now() ? a : null;
}
export function setAuth(a) { write(AUTH_KEY, a); }
export function logout() { write(AUTH_KEY, null); location.href = "index.html"; }

/** fetch wrapper: JSON in/out, bearer token (user token by default), typed errors. */
export async function api(path, { method = "GET", body, token, raw = false, signal } = {}) {
  const headers = {};
  const bearer = token ?? getAuth()?.token;
  if (bearer) headers.Authorization = "Bearer " + bearer;
  if (body !== undefined && !(body instanceof Uint8Array) && typeof body !== "string") headers["Content-Type"] = "application/json";
  if (typeof body === "string") headers["Content-Type"] = "application/json";
  let res;
  try {
    res = await fetch(path, { method, headers, signal, body: body === undefined ? undefined : body instanceof Uint8Array || typeof body === "string" ? body : JSON.stringify(body) });
  } catch (e) {
    if (e.name === "AbortError") throw e;
    throw new ApiError(0, "network", "Sin conexión con el servidor");
  }
  if (raw) return res;
  const text = await res.text();
  let data = null;
  try { data = text ? JSON.parse(text) : null; } catch { data = text; }
  if (!res.ok) {
    if (res.status === 401 && !token && getAuth() && !path.startsWith("/api/auth")) { setAuth(null); location.href = "index.html?expired=1"; }
    throw new ApiError(res.status, data?.code ?? "http_" + res.status, data?.message ?? res.statusText);
  }
  return data;
}

/** Stacked notifications (at most 3 visible). */
export function toast(msg, ms = 3500) {
  let box = document.getElementById("toasts");
  if (!box) {
    box = Object.assign(document.createElement("div"), { id: "toasts", className: "toasts" });
    box.setAttribute("role", "status");
    box.setAttribute("aria-live", "polite");
    document.body.append(box);
  }
  const el = Object.assign(document.createElement("div"), { className: "toast", textContent: msg });
  box.append(el);
  while (box.children.length > 3) box.firstChild.remove();
  setTimeout(() => el.remove(), ms);
}

export const fmtTime = (ms) => (ms ? new Date(ms).toLocaleTimeString("es-MX", { hour12: false }) : "—");
export const fmtDateTime = (ms) => (ms ? new Date(ms).toLocaleString("es-MX", { hour12: false }) : "—");
export const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
