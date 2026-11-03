// Ayudas para la API REST. La sesión del usuario (JWT) vive en localStorage; todos los errores llegan como { code, message }.
/** Llave de localStorage donde se guarda la sesión. */
const AUTH_KEY = "st.auth";

/** Error de la API con el código HTTP y el código estable que manda el servidor. */
export class ApiError extends Error {
  /**
   * @param {number} status Código HTTP (0 si no hubo conexión).
   * @param {string} code Código de error de la API, por ejemplo "unauthorized".
   * @param {string} [message] Mensaje para la persona usuaria.
   */
  constructor(status, code, message) { super(message || code); this.status = status; this.code = code; }
}

/** Lee un valor JSON de localStorage; null si no existe o no se puede leer. */
function read(key) { try { return JSON.parse(localStorage.getItem(key) || "null"); } catch { return null; } }
/** Guarda un valor JSON en localStorage (null lo borra). En modo privado puede fallar y se ignora. */
function write(key, value) { try { value == null ? localStorage.removeItem(key) : localStorage.setItem(key, JSON.stringify(value)); } catch { /* modo privado */ } }
/** Acceso al almacenamiento local para el resto de los módulos. */
export const store = { read, write };

/** Sesión actual si todavía no vence: { token, expiresAt, role, username }, o null. */
export function getAuth() {
  const a = read(AUTH_KEY);
  return a && a.expiresAt > Date.now() ? a : null;
}
/** Guarda (o borra con null) la sesión del usuario. */
export function setAuth(a) { write(AUTH_KEY, a); }
/** Cierra la sesión y vuelve a la página de inicio. */
export function logout() { write(AUTH_KEY, null); location.href = "index.html"; }

/**
 * Envoltura de fetch: JSON de entrada y salida, token bearer (el del usuario por defecto) y errores tipados.
 * Si el servidor responde 401 con la sesión del usuario, la borra y manda a la página de inicio.
 * @param {string} path Ruta de la API, por ejemplo "/api/devices".
 * @param {object} [opts]
 * @param {string} [opts.method] Método HTTP.
 * @param {object|string|Uint8Array} [opts.body] Cuerpo; los objetos se serializan como JSON.
 * @param {string} [opts.token] Token a usar en lugar del del usuario (por ejemplo, el del dispositivo).
 * @param {boolean} [opts.raw] Si es true devuelve la Response sin procesar.
 * @param {AbortSignal} [opts.signal] Señal para cancelar la solicitud.
 * @returns {Promise<any>} El JSON de la respuesta (o el texto si no es JSON).
 * @throws {ApiError} Si no hay conexión o la respuesta no es 2xx.
 */
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
    // Una cancelación se propaga tal cual; cualquier otro fallo es un problema de red.
    if (e.name === "AbortError") throw e;
    throw new ApiError(0, "network", "Sin conexión con el servidor");
  }
  if (raw) return res;
  const text = await res.text();
  let data = null;
  try { data = text ? JSON.parse(text) : null; } catch { data = text; }
  if (!res.ok) {
    // Sesión vencida o inválida: se borra y se avisa en la página de inicio.
    if (res.status === 401 && !token && getAuth() && !path.startsWith("/api/auth")) { setAuth(null); location.href = "index.html?expired=1"; }
    throw new ApiError(res.status, data?.code ?? "http_" + res.status, data?.message ?? res.statusText);
  }
  return data;
}

/**
 * Notificación flotante apilada (como máximo 3 visibles). El contenedor se anuncia a los lectores de pantalla.
 * @param {string} msg Texto a mostrar.
 * @param {number} [ms] Milisegundos que permanece visible.
 */
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

/** Hora local en formato de 24 h ("—" si no hay valor). */
export const fmtTime = (ms) => (ms ? new Date(ms).toLocaleTimeString("es-MX", { hour12: false }) : "—");
/** Fecha y hora local en formato de 24 h ("—" si no hay valor). */
export const fmtDateTime = (ms) => (ms ? new Date(ms).toLocaleString("es-MX", { hour12: false }) : "—");
/** Escapa los caracteres especiales de HTML para insertar texto de forma segura con innerHTML. */
export const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
