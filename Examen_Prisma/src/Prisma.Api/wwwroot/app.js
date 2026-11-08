"use strict";
/**
 * Cliente web de ABCO: SPA (aplicación de una sola página) sin framework que consume la API REST
 * del servidor (/api/...). Se sirve desde wwwroot junto con index.html y styles.css.
 *
 * Contenido:
 *  - Sesión del usuario en sessionStorage y la función `api()`, que llama al backend con el token JWT
 *    y centraliza el manejo de errores (sesión expirada, contraseña temporal, límite de intentos).
 *  - Tema automático/claro/oscuro, campo de contraseña con Mostrar/Ocultar, diálogos modales y avisos.
 *  - Selector de imagen portadora: catálogo del servidor o archivo propio.
 *  - Vistas: login (en dos pasos si hay 2FA), registro, cambio obligatorio de contraseña, ocultar,
 *    revelar, laboratorio César, comparador, cuenta y consola de administración.
 *  - Enrutador por hash (#/vista) que aplica las reglas de acceso.
 *
 * Nota sobre nombres: los identificadores internos con "prisma" (el modo "prisma", las cabeceras
 * X-Prisma-*, la clave "prisma.session", el archivo "prisma.png") vienen del nombre anterior del
 * algoritmo (PRISMA-L) y se refieren a ABCO; se conservan por compatibilidad con la API y la app Android.
 */

/**
 * Atajo de `querySelector`.
 * @param {string} sel Selector CSS.
 * @param {ParentNode} [root=document] Nodo dentro del cual se busca.
 * @returns {Element|null} El primer elemento que coincide, o null.
 */
const $ = (sel, root = document) => root.querySelector(sel);
/** Contenedor `<main id="app">` donde cada vista pinta su contenido. */
const app = $("#app");

// ---------- Sesión ----------
// La sesión ({ token, username, role, twoFactorEnabled, mustChangePassword }) se guarda en
// sessionStorage y no en localStorage: dura lo que la pestaña, no se comparte con otras pestañas y
// desaparece al cerrar el navegador, lo que acorta la vida del token JWT en el equipo.
// Todos los accesos van en try/catch porque el almacenamiento puede estar bloqueado (modo privado,
// políticas del navegador, cookies desactivadas) o contener JSON dañado; en ese caso la app sigue
// funcionando con la copia en memoria (`memory`), aunque la sesión se pierde al recargar.
/**
 * Acceso a la sesión persistida.
 * - `get()`: lee y parsea la sesión; devuelve null si no hay o no se puede leer.
 * - `set(s)`: guarda la sesión `s` y actualiza la copia en memoria.
 * - `clear()`: borra la sesión (cierre de sesión o token expirado).
 */
const session = {
  get() { try { return JSON.parse(sessionStorage.getItem("prisma.session") || "null"); } catch { return null; } },
  set(s) { try { sessionStorage.setItem("prisma.session", JSON.stringify(s)); } catch { /* sin almacenamiento */ } memory = s; },
  clear() { try { sessionStorage.removeItem("prisma.session"); } catch { } memory = null; },
};
/** Copia en memoria de la sesión; es la fuente de verdad mientras la página está abierta. */
let memory = session.get();
/**
 * Sesión del usuario autenticado.
 * @returns {{token: string, username: string, role: string, twoFactorEnabled: boolean, mustChangePassword: boolean}|null}
 *   La sesión actual, o null si nadie ha iniciado sesión.
 */
const current = () => memory;

// ---------- Utilidades ----------
/**
 * Escapa los caracteres especiales de HTML. Todo texto que viene del usuario o del servidor pasa por
 * aquí antes de insertarse en una plantilla de `innerHTML`, para evitar inyección de HTML (XSS).
 * @param {*} s Valor a escapar (null y undefined se convierten en cadena vacía).
 * @returns {string} Texto seguro para insertar como HTML.
 */
const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
/**
 * Formatea un número con convenciones de es-MX y exactamente `d` decimales.
 * @param {*} n Valor a formatear; si no es número se devuelve tal cual.
 * @param {number} [d=2] Número de decimales.
 * @returns {string|*} El número formateado, o el valor original.
 */
const fmt = (n, d = 2) => (typeof n === "number" ? n.toLocaleString("es-MX", { maximumFractionDigits: d, minimumFractionDigits: d }) : n);
/**
 * Longitud de un texto en bytes UTF-8, que es lo que cuenta para la capacidad de la imagen
 * (una "ñ" o un emoji ocupan más de un byte).
 * @param {string} s Texto.
 * @returns {number} Número de bytes.
 */
const bytes = (s) => new TextEncoder().encode(s).length;

/**
 * HTML de un campo de contraseña con botón Mostrar/Ocultar (oculto por defecto).
 * @param {string} id Id del `<input>`; el botón lo referencia con `data-pw`.
 * @param {string} [attrs=""] Atributos HTML extra para el input (autocomplete, required, minlength…).
 * @returns {string} Fragmento HTML para insertar en una plantilla.
 */
const pwInput = (id, attrs = "") => `<div class="pw-field"><input id="${id}" type="password" ${attrs}><button type="button" class="pw-toggle" data-pw="${id}" aria-label="Mostrar">Mostrar</button></div>`;
// Delegación de eventos: un solo listener global atiende todos los botones Mostrar/Ocultar, incluidos
// los que las vistas crean después. Alterna el tipo del input entre password y text.
document.addEventListener("click", (e) => {
  const b = e.target.closest(".pw-toggle");
  if (!b) return;
  const input = document.getElementById(b.dataset.pw);
  const show = input.type === "password";
  input.type = show ? "text" : "password";
  b.textContent = show ? "Ocultar" : "Mostrar";
  b.setAttribute("aria-label", b.textContent);
});

/**
 * Diálogo modal simple sobre el `<dialog id="dialog">` nativo (showModal).
 * El `html` se inserta sin escapar: quien llama debe escapar con `esc()` los datos variables.
 * @param {string} html Contenido del diálogo.
 * @param {{label: string, value: *, primary?: boolean}[]} [buttons] Botones; el primario usa el estilo
 *   principal y los demás el secundario. Por defecto, un solo botón "Entendido".
 * @returns {Promise<*>} Se resuelve con el `value` del botón pulsado, o undefined si se cierra con Esc.
 */
function showDialog(html, buttons = [{ label: "Entendido", value: true, primary: true }]) {
  const d = $("#dialog");
  d.innerHTML = `${html}<div class="actions">${buttons.map((b, i) => `<button type="button" data-i="${i}" class="${b.primary ? "" : "secondary"}">${esc(b.label)}</button>`).join("")}</div>`;
  return new Promise((resolve) => {
    d.onclick = (e) => { const b = e.target.closest("button[data-i]"); if (b) { d.close(); resolve(buttons[+b.dataset.i].value); } };
    d.oncancel = () => resolve(undefined);
    d.showModal();
  });
}

// ---------- Tema: automático (sigue al sistema) → claro → oscuro ----------
// La preferencia de tema sí va en localStorage porque debe recordarse entre visitas; index.html la
// aplica antes de pintar para evitar un parpadeo. Los accesos van en try/catch porque el
// almacenamiento puede no estar disponible; entonces se usa "auto" y la elección no se recuerda.
/** Ciclo de temas del botón y su etiqueta visible en español. */
const THEMES = ["auto", "light", "dark"], THEME_LABEL = { auto: "automático", light: "claro", dark: "oscuro" };
/**
 * Lee el tema guardado.
 * @returns {"auto"|"light"|"dark"} El tema elegido, o "auto" si no hay o no se puede leer.
 */
function getTheme() { try { return localStorage.getItem("abco.theme") || "auto"; } catch { return "auto"; } }
/**
 * Aplica y recuerda un tema. Con "auto" se quita `data-theme` del `<html>` y la hoja de estilos sigue
 * a `prefers-color-scheme` del sistema; con "light" o "dark" se fuerza esa paleta.
 * @param {"auto"|"light"|"dark"} t Tema a aplicar.
 */
function applyTheme(t) {
  if (t === "auto") delete document.documentElement.dataset.theme; else document.documentElement.dataset.theme = t;
  try { localStorage.setItem("abco.theme", t); } catch { }
  $("#themeBtn").textContent = `Tema: ${THEME_LABEL[t]}`;
}
$("#themeBtn").addEventListener("click", () => applyTheme(THEMES[(THEMES.indexOf(getTheme()) + 1) % THEMES.length]));
applyTheme(getTheme());

/**
 * Muestra un aviso breve (3.8 s) en la esquina. Un aviso nuevo reemplaza al anterior y reinicia el
 * temporizador, que se guarda en la propiedad `toast.timer` de la propia función.
 * @param {string} msg Texto del aviso.
 * @param {boolean} [error=false] true para el estilo de error.
 */
function toast(msg, error = false) {
  const t = $("#toast");
  t.textContent = msg;
  t.className = "toast show" + (error ? " err" : "");
  clearTimeout(toast.timer);
  toast.timer = setTimeout(() => (t.className = "toast"), 3800);
}

/**
 * Ejecuta una tarea asíncrona con el botón deshabilitado y un indicador "Procesando…", para evitar
 * envíos dobles. Al terminar (con éxito o con error) restaura el botón.
 * @param {HTMLButtonElement} button Botón que disparó la acción.
 * @param {() => Promise<*>} fn Tarea a ejecutar.
 * @returns {Promise<*>} Lo que devuelva `fn`; sus errores se propagan.
 */
async function busy(button, fn) {
  const label = button.innerHTML;
  button.disabled = true;
  button.innerHTML = `<span class="spinner" aria-hidden="true"></span> Procesando…`;
  try { return await fn(); }
  finally {
    button.disabled = false;
    // Una vista puede pedir otra etiqueta para el siguiente paso (p. ej., el login con 2FA).
    button.innerHTML = button.dataset.nextLabel ?? label;
    delete button.dataset.nextLabel;
  }
}

/**
 * Error de una llamada a la API.
 * `status` es el código HTTP (0 si no hubo conexión) y `body` el cuerpo JSON de error, normalmente
 * `{ error }`; el mensaje del Error es `body.error` o "Error <status>".
 */
class ApiError extends Error {
  constructor(status, body) { super(body?.error || `Error ${status}`); this.status = status; this.body = body; }
}

/**
 * Llama a la API del servidor agregando el token de la sesión y traduce los errores a `ApiError`.
 * @param {string} path Ruta de la API (p. ej., "/api/stego/embed").
 * @param {object} [options]
 * @param {string} [options.method="GET"] Método HTTP.
 * @param {*} [options.json] Cuerpo que se envía como JSON.
 * @param {FormData} [options.form] Cuerpo multipart (archivos e imágenes).
 * @param {"json"|"blob"|"raw"} [options.as="json"] Cómo devolver la respuesta: JSON parseado, Blob, o la
 *   Response cruda (útil para leer cabeceras como Content-Disposition o X-Prisma-*).
 * @returns {Promise<*>} El JSON (null si la respuesta es 204), el Blob o la Response.
 * @throws {ApiError} Sin conexión (status 0), sesión expirada (401), contraseña temporal pendiente
 *   (403), demasiados intentos (429) o cualquier otra respuesta que no sea 2xx.
 */
async function api(path, { method = "GET", json, form, as = "json" } = {}) {
  const headers = {};
  const s = current();
  // El token JWT de la sesión viaja como encabezado Bearer.
  if (s?.token) headers.Authorization = `Bearer ${s.token}`;
  let body;
  if (json !== undefined) { headers["Content-Type"] = "application/json"; body = JSON.stringify(json); }
  // Con FormData no se fija Content-Type: el navegador lo pone con el boundary del multipart.
  if (form) body = form;
  let res;
  // fetch solo rechaza por fallas de red (servidor caído, sin conexión); los errores HTTP llegan como res.
  try { res = await fetch(path, { method, headers, body }); }
  catch { throw new ApiError(0, { error: "No hay conexión con el servidor." }); }
  // 401 con un token puesto: el token expiró o ya no es válido. Se cierra la sesión y se vuelve al
  // login. Las rutas /api/auth se excluyen porque ahí 401 significa credenciales incorrectas, y ese
  // mensaje debe llegar al formulario sin cerrar nada.
  if (res.status === 401 && s?.token && !path.startsWith("/api/auth")) {
    session.clear(); updateNav(); location.hash = "#/login";
    throw new ApiError(401, { error: "Tu sesión expiró. Inicia sesión de nuevo." });
  }
  // 429: el servidor limita las peticiones de autenticación (10 por minuto por IP) para frenar
  // los ataques de fuerza bruta a las contraseñas.
  if (res.status === 429) throw new ApiError(429, { error: "Demasiados intentos. Espera un minuto." });
  // Con contraseña temporal, el token solo sirve para /api/account/me y /api/account/password;
  // cualquier otra ruta responde 403. Se manda al usuario a la vista de cambio obligatorio.
  if (res.status === 403 && s?.mustChangePassword) { location.hash = "#/cambiar"; throw new ApiError(403, { error: "Primero cambia tu contraseña temporal." }); }
  if (!res.ok) {
    // El cuerpo de error suele ser { error }; si no es JSON se usa un mensaje genérico con el código.
    let data = null;
    try { data = await res.json(); } catch { }
    throw new ApiError(res.status, data || { error: `Error ${res.status}` });
  }
  if (as === "raw") return res;
  if (as === "blob") return res.blob();
  if (res.status === 204) return null;
  return res.json();
}

/**
 * Descarga un Blob como archivo. Crea un enlace temporal con atributo `download` y lo pulsa por
 * código. El object URL se revoca después de un minuto, no de inmediato, para que la descarga alcance
 * a empezar en todos los navegadores (y para que siga sirviendo si se reutiliza en una vista previa).
 * @param {Blob} blob Contenido del archivo.
 * @param {string} name Nombre sugerido del archivo.
 */
function download(blob, name) {
  const url = URL.createObjectURL(blob);
  const a = Object.assign(document.createElement("a"), { href: url, download: name });
  document.body.append(a); a.click(); a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 60_000);
}

/**
 * Obtiene el nombre de archivo del encabezado Content-Disposition de una respuesta. Acepta
 * `filename="…"` y la forma codificada `filename*=UTF-8''…`.
 * @param {Response} res Respuesta del servidor.
 * @param {string} fallback Nombre a usar si el encabezado no trae uno.
 * @returns {string} Nombre del archivo.
 */
function filenameFrom(res, fallback) {
  const cd = res.headers.get("Content-Disposition") || "";
  const m = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(cd);
  return m ? decodeURIComponent(m[1]) : fallback;
}

// ---------- Catálogo (compartido por Ocultar y Cuenta) ----------
/** Catálogo de imágenes del servidor ([{ id, name }]); se pide una sola vez por carga de la página. */
let catalogCache = null;
/**
 * Pinta la cuadrícula de imágenes del catálogo y avisa cuál se eligió. La elegida se marca con
 * `aria-pressed="true"` (estado accesible y gancho de estilo) y las demás con "false".
 * @param {HTMLElement} root Contenedor de la cuadrícula.
 * @param {(id: string) => void} onChange Se llama con el id de la imagen elegida.
 * @returns {Promise<void>}
 * @throws {ApiError} Si no se pudo obtener el catálogo.
 */
async function catalogPicker(root, onChange) {
  catalogCache ??= await api("/api/catalog");
  root.innerHTML = catalogCache.map((c) => `
    <button type="button" data-id="${esc(c.id)}" aria-pressed="false">
      <img src="/api/catalog/${encodeURIComponent(c.id)}" alt="${esc(c.name)}" loading="lazy"><span>${esc(c.name)}</span>
    </button>`).join("");
  root.addEventListener("click", (e) => {
    const b = e.target.closest("button[data-id]");
    if (!b) return;
    root.querySelectorAll("button").forEach((x) => x.setAttribute("aria-pressed", String(x === b)));
    onChange(b.dataset.id);
  });
}

/**
 * Bloque "elige imagen" con pestañas Catálogo / Mi imagen, compartido por Ocultar y Cuenta (2FA).
 * @param {HTMLElement} root Contenedor donde se pinta el bloque.
 * @param {{onChange?: () => void}} [options] `onChange` se llama al cambiar de pestaña, de imagen del
 *   catálogo o de archivo (Ocultar lo usa para recalcular la capacidad).
 * @returns {{mode: () => string, append: (form: FormData) => FormData, ready: () => boolean}}
 *   `mode()` devuelve "catalogo" o "archivo"; `append(form)` agrega al FormData el campo `catalogId` o
 *   el archivo `image` y lanza Error si falta la elección; `ready()` dice si ya hay una imagen elegida.
 */
function imageSource(root, { onChange = () => {} } = {}) {
  root.innerHTML = `
    <div class="seg" role="radiogroup" aria-label="Origen de la imagen">
      <label><input type="radio" name="src" value="catalogo" checked><span>Catálogo</span></label>
      <label><input type="radio" name="src" value="archivo"><span>Mi imagen</span></label>
    </div>
    <div data-pane="catalogo" style="margin-top:12px"><div class="catalog"></div></div>
    <div data-pane="archivo" style="margin-top:12px" hidden>
      <input type="file" accept="image/png,image/jpeg,image/webp,image/bmp">
      <small>PNG, JPEG, WebP o BMP de hasta 15 MB. Las fotos de más de 1280 px por lado se reducen.</small>
    </div>`;
  let catalogId = null;
  const file = $("input[type=file]", root);
  // Si el catálogo no carga solo se avisa: la pestaña "Mi imagen" sigue funcionando.
  catalogPicker($(".catalog", root), (id) => { catalogId = id; onChange(); }).catch((e) => toast(e.message, true));
  root.addEventListener("change", (e) => {
    // Cambiar de pestaña muestra su panel y oculta el otro.
    if (e.target.name === "src") {
      root.querySelectorAll("[data-pane]").forEach((p) => (p.hidden = p.dataset.pane !== e.target.value));
      onChange();
    }
    if (e.target === file) onChange();
  });
  return {
    mode: () => $("input[name=src]:checked", root).value,
    append(form) {
      if (this.mode() === "archivo") {
        if (!file.files[0]) throw new Error("Selecciona un archivo de imagen.");
        form.append("image", file.files[0]);
      } else {
        if (!catalogId) throw new Error("Elige una imagen del catálogo.");
        form.append("catalogId", catalogId);
      }
      return form;
    },
    ready() { return this.mode() === "archivo" ? !!file.files[0] : !!catalogId; },
  };
}

// ---------- Vistas ----------
/**
 * Vistas de la SPA indexadas por el nombre de la ruta (#/nombre). Cada vista reemplaza el contenido
 * de #app y conecta sus propios eventos; el enrutador elige cuál ejecutar.
 * @type {Object<string, () => (void|Promise<void>)>}
 */
const views = {};

/**
 * Vista de inicio de sesión. El login es en dos pasos cuando la cuenta tiene 2FA:
 * 1. Usuario y contraseña en JSON a /api/auth/login. Sin 2FA, el servidor responde con la sesión.
 *    Con 2FA responde `{ requires2fa: true }` sin token.
 * 2. Entonces aparece el campo de la imagen-llave y se envía, en multipart, usuario + contraseña +
 *    imagen a /api/auth/login/2fa. El servidor revela con ABCO el secreto oculto en la imagen
 *    (usando la contraseña como frase) y compara su huella SHA-256 con la guardada.
 * Incluye el aviso "¿Olvidaste tu contraseña?", que remite al administrador.
 */
views.login = () => {
  app.innerHTML = `
    <div class="auth-wrap">
      <h1>Iniciar sesión</h1>
      <p class="lead">Si activaste el segundo factor, después de tu contraseña te pediremos tu <b>imagen-llave</b>.</p>
      <form class="card" id="f1" autocomplete="on">
        <label for="u">Usuario</label><input id="u" type="text" autocomplete="username" required>
        <label for="p">Contraseña</label>${pwInput("p", 'autocomplete="current-password" required')}
        <div id="step2" hidden>
          <label for="k">Imagen-llave (llave.png)</label>
          <input id="k" type="file" accept="image/png">
          <small>La imagen que descargaste al activar el 2FA. Sin ella no es posible entrar.</small>
        </div>
        <div class="actions"><button id="go">Entrar</button><a class="btn secondary" href="#/registro">Crear cuenta</a></div>
        <div class="forgot"><button type="button" class="link" id="forgot">¿Olvidaste tu contraseña?</button></div>
      </form>
    </div>`;
  $("#forgot").addEventListener("click", () =>
    showDialog(`<h2>¿Olvidaste tu contraseña?</h2><p>Contacta al administrador para que restablezca tu contraseña. Te dará una contraseña temporal que deberás cambiar al entrar.</p>`));
  // Estado del flujo: false = falta el paso 1; true = el servidor ya pidió la imagen-llave.
  let needs2fa = false;
  $("#f1").addEventListener("submit", (e) => {
    e.preventDefault();
    busy($("#go"), async () => {
      const username = $("#u").value.trim(), password = $("#p").value;
      try {
        let res;
        if (!needs2fa) {
          res = await api("/api/auth/login", { method: "POST", json: { username, password } });
          // Se pasa al paso 2 sin recargar: el usuario queda de solo lectura para que no cambie entre
          // pasos, y el mismo botón pasa a llamarse "Verificar imagen-llave" (ver busy()).
          if (res.requires2fa) {
            needs2fa = true;
            $("#step2").hidden = false;
            $("#u").readOnly = true;
            $("#go").dataset.nextLabel = "Verificar imagen-llave";
            $("#k").focus();
            toast("Contraseña correcta. Ahora carga tu imagen-llave.");
            return;
          }
        } else {
          const key = $("#k").files[0];
          if (!key) return toast("Selecciona tu imagen-llave.", true);
          const form = new FormData();
          form.append("username", username); form.append("password", password); form.append("keyImage", key);
          res = await api("/api/auth/login/2fa", { method: "POST", form });
        }
        // Sesión iniciada. Si la contraseña es temporal (la restableció un administrador), se fuerza
        // la vista de cambio antes de dejar usar el resto del sistema.
        session.set(res);
        updateNav();
        toast(`Bienvenido, ${res.username}.`);
        location.hash = res.mustChangePassword ? "#/cambiar" : "#/ocultar";
      } catch (err) { toast(err.message, true); }
    });
  });
};

/**
 * Vista de registro de una cuenta nueva (rol usuario). El formulario valida en el cliente el formato
 * del usuario, la longitud mínima y que las dos contraseñas coincidan; el servidor vuelve a validar.
 * Al terminar manda al login.
 */
views.registro = () => {
  app.innerHTML = `
    <div class="auth-wrap">
      <h1>Crear cuenta</h1>
      <p class="lead">Después podrás activar el segundo factor con una imagen-llave en <b>Cuenta</b>.</p>
      <form class="card" id="f">
        <label for="u">Usuario</label><input id="u" type="text" autocomplete="username" pattern="[A-Za-z0-9_.\\-]{3,32}" required>
        <small>3 a 32 caracteres: letras, números, punto, guion o guion bajo.</small>
        <label for="p">Contraseña</label>${pwInput("p", 'autocomplete="new-password" minlength="8" required')}
        <label for="p2">Repite la contraseña</label>${pwInput("p2", 'autocomplete="new-password" minlength="8" required')}
        <div class="actions"><button id="go">Registrarme</button><a class="btn secondary" href="#/login">Ya tengo cuenta</a></div>
      </form>
    </div>`;
  $("#f").addEventListener("submit", (e) => {
    e.preventDefault();
    if ($("#p").value !== $("#p2").value) return toast("Las contraseñas no coinciden.", true);
    busy($("#go"), async () => {
      try {
        await api("/api/auth/register", { method: "POST", json: { username: $("#u").value.trim(), password: $("#p").value } });
        toast("Cuenta creada. Ya puedes iniciar sesión.");
        location.hash = "#/login";
      } catch (err) { toast(err.message, true); }
    });
  });
};

/**
 * Vista "Ocultar un mensaje": elige la imagen portadora (catálogo o archivo), escribe el mensaje y
 * elige el algoritmo, ABCO (valor interno "prisma", con frase secreta) o esteganografía clásica
 * (bits menos significativos en orden y sin clave). Muestra la capacidad de la imagen en cada modo,
 * y en el resultado la imagen generada con su PSNR, los bytes del mensaje, el porcentaje de la
 * capacidad usado y un botón para descargar el PNG.
 */
views.ocultar = () => {
  app.innerHTML = `
    <h1>Ocultar un mensaje</h1>
    <p class="lead">ABCO cifra el mensaje con su "César matricial" y lo esconde en los bits menos significativos siguiendo un recorrido barajado con tu frase. La esteganografía clásica escribe los bits en orden y sin clave.</p>
    <div class="grid two">
      <form class="card" id="f">
        <h2>1. Imagen portadora</h2>
        <div id="src"></div>
        <p id="cap" class="muted" style="margin:10px 0 0"></p>
        <h2 style="margin-top:18px">2. Mensaje</h2>
        <label for="msg">Mensaje secreto</label>
        <textarea id="msg" required placeholder="Escribe aquí lo que quieres ocultar…"></textarea>
        <small id="count">0 bytes</small>
        <label>Algoritmo</label>
        <div class="seg" role="radiogroup">
          <label><input type="radio" name="mode" value="prisma" checked><span>ABCO</span></label>
          <label><input type="radio" name="mode" value="clasico"><span>Esteganografía clásica</span></label>
        </div>
        <div id="passWrap">
          <label for="pass">Frase secreta</label>
          ${pwInput("pass", 'minlength="4" autocomplete="off" placeholder="Compártela con el destinatario por otro medio"')}
        </div>
        <div class="actions"><button id="go">Ocultar en la imagen</button></div>
      </form>
      <section class="card" id="out" aria-live="polite"><h2>Resultado</h2><div class="empty">Aquí aparecerá la imagen con el mensaje oculto.</div></section>
    </div>`;
  const src = imageSource($("#src"), { onChange: refreshCapacity });
  const msg = $("#msg");
  // Contador en bytes UTF-8 del mensaje. La frase solo se pide en modo ABCO ("prisma"): la
  // esteganografía clásica no usa clave.
  msg.addEventListener("input", () => ($("#count").textContent = `${bytes(msg.value)} bytes`));
  $("#f").addEventListener("change", (e) => { if (e.target.name === "mode") $("#passWrap").hidden = e.target.value !== "prisma"; });

  /**
   * Pide al servidor las dimensiones y la capacidad de la imagen elegida (en bytes de mensaje para
   * ABCO y para el modo clásico) e indica si se redujo por ser mayor de 1280 px por lado.
   * Se llama cada vez que cambia la imagen; los errores se muestran en el mismo renglón.
   */
  async function refreshCapacity() {
    $("#cap").textContent = "";
    if (!src.ready()) return;
    try {
      const c = await api("/api/stego/capacity", { method: "POST", form: src.append(new FormData()) });
      $("#cap").innerHTML = `Imagen ${c.width}×${c.height}${c.resized ? ` (reducida desde ${c.originalWidth}×${c.originalHeight})` : ""} · capacidad ABCO <b>${c.prismaCapacity.toLocaleString("es-MX")} B</b> · clásico ${c.classicCapacity.toLocaleString("es-MX")} B`;
    } catch (err) { $("#cap").textContent = err.message; }
  }

  $("#f").addEventListener("submit", (e) => {
    e.preventDefault();
    busy($("#go"), async () => {
      try {
        const mode = $("input[name=mode]:checked").value;
        const form = src.append(new FormData());
        form.append("message", msg.value); form.append("mode", mode); form.append("passphrase", $("#pass").value);
        // Se pide la Response cruda para leer, además del PNG, el nombre del archivo y las cabeceras
        // X-Prisma-* (PSNR, bytes del mensaje y capacidad). El mismo blob alimenta la vista previa
        // (object URL) y el botón de descarga, sin volver a pedirlo al servidor.
        const res = await api("/api/stego/embed", { method: "POST", form, as: "raw" });
        const blob = await res.blob();
        const name = filenameFrom(res, "prisma.png");
        const url = URL.createObjectURL(blob);
        const psnr = res.headers.get("X-Prisma-Psnr"), used = +res.headers.get("X-Prisma-Payload-Bytes"), cap = +res.headers.get("X-Prisma-Capacity");
        $("#out").innerHTML = `
          <h2>Resultado <span class="badge info">${mode === "prisma" ? "ABCO" : "Esteganografía clásica"}</span></h2>
          <img class="preview" src="${url}" alt="Imagen con el mensaje oculto">
          <div class="stats">
            <div class="stat"><b>${psnr === "inf" ? "∞" : fmt(+psnr)} dB</b><small>PSNR (calidad; > 40 dB es imperceptible)</small></div>
            <div class="stat"><b>${used.toLocaleString("es-MX")} B</b><small>mensaje</small></div>
            <div class="stat"><b>${fmt((100 * used) / cap, 3)} %</b><small>de la capacidad</small></div>
          </div>
          <div class="actions"><button class="secondary" id="dl">Descargar PNG</button></div>
          <p class="muted"><small>Envíala <b>como archivo</b>. Si una app de mensajería la recomprime "como foto", el mensaje se destruye.</small></p>`;
        $("#dl").addEventListener("click", () => download(blob, name));
        toast("Mensaje oculto.");
      } catch (err) { toast(err.message, true); }
    });
  });
};

/**
 * Vista "Revelar un mensaje": recibe el PNG, el algoritmo y, en modo ABCO, la frase. Si todo es
 * correcto muestra el mensaje y su tamaño. Si la frase es incorrecta o la imagen fue alterada, ABCO
 * responde siempre con el mismo error genérico (el HMAC no coincide) para no dar pistas al atacante.
 */
views.revelar = () => {
  app.innerHTML = `
    <h1>Revelar un mensaje</h1>
    <p class="lead">Carga la imagen PNG que recibiste. Con ABCO necesitas la frase; si es incorrecta o la imagen fue alterada, el sistema responde con el mismo error genérico.</p>
    <div class="grid two">
      <form class="card" id="f">
        <label for="img">Imagen PNG</label>
        <input id="img" type="file" accept="image/png" required>
        <label>Algoritmo</label>
        <div class="seg">
          <label><input type="radio" name="mode" value="prisma" checked><span>ABCO</span></label>
          <label><input type="radio" name="mode" value="clasico"><span>Esteganografía clásica</span></label>
        </div>
        <div id="passWrap"><label for="pass">Frase secreta</label>${pwInput("pass", 'autocomplete="off"')}</div>
        <div class="actions"><button id="go">Revelar</button></div>
      </form>
      <section class="card" id="out" aria-live="polite"><h2>Mensaje</h2><div class="empty">Aquí aparecerá el mensaje oculto.</div></section>
    </div>`;
  $("#f").addEventListener("change", (e) => { if (e.target.name === "mode") $("#passWrap").hidden = e.target.value !== "prisma"; });
  $("#f").addEventListener("submit", (e) => {
    e.preventDefault();
    busy($("#go"), async () => {
      const form = new FormData();
      form.append("image", $("#img").files[0]);
      form.append("mode", $("input[name=mode]:checked").value);
      form.append("passphrase", $("#pass").value);
      try {
        const r = await api("/api/stego/extract", { method: "POST", form });
        $("#out").innerHTML = `<h2>Mensaje <span class="badge ok">${r.bytes} B</span></h2><div class="message-box">${esc(r.message)}</div>`;
      } catch (err) {
        // En el error 422 el servidor incluye `rawSequentialHex`: los primeros bytes que se obtienen leyendo
        // los bits menos significativos en orden, como haría un atacante. Con ABCO parecen ruido.
        const raw = err.body?.rawSequentialHex;
        $("#out").innerHTML = `<h2>Mensaje</h2><div class="error-box">${esc(err.message)}</div>
          ${raw ? `<h3>Lo que vería un atacante leyendo los bits menos significativos en orden</h3><p class="hex">${esc(raw)}</p><small>Sin la frase, los bits parecen ruido aleatorio.</small>` : ""}`;
      }
    });
  });
};

/**
 * Laboratorio César: cifra y descifra con C = (P + k) mod n, donde n = 26 letras, o 27 si se incluye
 * la Ñ (el control deslizante ajusta su máximo), y rompe el cifrado por fuerza bruta. Al romper
 * muestra la clave encontrada, el tiempo y la tabla de las n claves ordenadas por χ² frente a las
 * frecuencias del español, con una barra relativa por clave.
 */
views.cesar = () => {
  app.innerHTML = `
    <h1>Laboratorio César</h1>
    <p class="lead">C = (P + k) mod n. Con solo n claves posibles, se rompe probando todas y eligiendo la que mejor se parece al español con la prueba χ².</p>
    <div class="grid two">
      <form class="card" id="f">
        <label for="t">Texto</label>
        <textarea id="t">La criptografía estudia cómo proteger la información de los mensajes que se envían entre dos personas</textarea>
        <label for="k">Clave k = <output id="kv">3</output></label>
        <input id="k" type="range" min="0" max="25" value="3">
        <label class="check"><input id="enye" type="checkbox"> Incluir Ñ (alfabeto de 27 letras)</label>
        <div class="actions">
          <button type="button" data-op="encrypt">Cifrar</button>
          <button type="button" class="secondary" data-op="decrypt">Descifrar</button>
          <button type="button" class="secondary" data-op="crack">Romper (χ²)</button>
        </div>
      </form>
      <section class="card" id="out" aria-live="polite"><h2>Resultado</h2><div class="empty">Cifra un texto y luego intenta romperlo.</div></section>
    </div>`;
  const k = $("#k");
  k.addEventListener("input", () => ($("#kv").textContent = k.value));
  $("#enye").addEventListener("change", (e) => { k.max = e.target.checked ? 26 : 25; $("#kv").textContent = k.value; });
  $("#f").addEventListener("click", (e) => {
    const b = e.target.closest("button[data-op]");
    if (!b) return;
    busy(b, async () => {
      const op = b.dataset.op, text = $("#t").value, enye = $("#enye").checked;
      try {
        if (op === "crack") {
          const r = await api("/api/caesar/crack", { method: "POST", json: { text, enye } });
          // Barra = χ²(mejor) / χ²(k): la clave correcta llena la barra y las demás quedan casi vacías.
          const best = Math.max(r.best.chiSquare, 0.01);
          $("#out").innerHTML = `
            <h2>Clave encontrada: k = ${r.best.key} <span class="badge ok">${fmt(r.ms)} ms</span></h2>
            <div class="message-box">${esc(r.best.preview)}</div>
            <h3>Las ${r.candidates.length} claves ordenadas por χ² (menor = más parecido al español)</h3>
            <div class="table-wrap"><table><thead><tr><th>k</th><th>χ²</th><th></th><th>Vista previa</th></tr></thead><tbody>
            ${r.candidates.map((c, i) => `<tr class="${i === 0 ? "best" : ""}"><td class="num">${c.key}</td><td class="num">${fmt(c.chiSquare)}</td>
              <td><div class="bar"><i style="width:${Math.max(2, Math.min(100, (100 * best) / Math.max(c.chiSquare, 0.01)))}%"></i></div></td><td>${esc(c.preview.slice(0, 48))}</td></tr>`).join("")}
            </tbody></table></div>`;
        } else {
          const r = await api(`/api/caesar/${op}`, { method: "POST", json: { text, key: +k.value, enye } });
          $("#out").innerHTML = `<h2>${op === "encrypt" ? "Texto cifrado" : "Texto descifrado"} <span class="badge info">k = ${k.value}, n = ${r.alphabet.length}</span></h2>
            <div class="message-box">${esc(r.result)}</div>
            <div class="actions"><button type="button" class="secondary" id="use">Usar como entrada</button></div>`;
          // "Usar como entrada" copia el resultado al texto para encadenar operaciones (cifrar y luego romper).
          $("#use").addEventListener("click", () => ($("#t").value = r.result));
        }
      } catch (err) { toast(err.message, true); }
    });
  });
};

/**
 * Comparador de algoritmos: aplica César, esteganografía clásica y ABCO al mismo mensaje y muestra
 * por cada uno el tamaño de la salida, el tiempo, la entropía en bits por byte (máximo 8), si detecta
 * la alteración de un solo bit, notas y la salida en hexadecimal. Arriba, el tamaño y la entropía del
 * texto claro como referencia.
 */
views.comparador = () => {
  app.innerHTML = `
    <h1>Comparador de algoritmos</h1>
    <p class="lead">El mismo mensaje con los tres algoritmos del sistema: César, esteganografía clásica y ABCO. La entropía (bits por byte, máximo 8) mide qué tanto se borran los patrones del idioma; la última columna dice si una alteración de un solo bit se detecta.</p>
    <form class="card" id="f">
      <div class="grid two">
        <div><label for="m">Mensaje</label><textarea id="m">la criptografia estudia como proteger la informacion de los mensajes</textarea></div>
        <div>
          <label for="p">Frase (ABCO)</label><input id="p" type="text" value="frase-secreta">
          <label for="k">Clave César</label><input id="k" type="number" min="0" max="25" value="3">
          <div class="actions"><button id="go">Comparar</button></div>
        </div>
      </div>
    </form>
    <div id="out" style="margin-top:16px" aria-live="polite"></div>`;
  $("#f").addEventListener("submit", (e) => {
    e.preventDefault();
    busy($("#go"), async () => {
      try {
        const r = await api("/api/compare", { method: "POST", json: { message: $("#m").value, passphrase: $("#p").value, caesarKey: +$("#k").value } });
        $("#out").innerHTML = `
          <p class="muted">Texto claro: ${r.plainBytes} B · entropía ${fmt(r.plainEntropy, 3)} bits/byte</p>
          <div class="compare">${r.results.map((x) => `
            <article class="card">
              <h2>${esc(x.algorithm)} <span class="badge info">${esc(x.kind)}</span></h2>
              <div class="stats">
                <div class="stat"><b>${x.outputBytes} B</b><small>salida</small></div>
                <div class="stat"><b>${fmt(x.ms)} ms</b><small>tiempo</small></div>
                <div class="stat"><b>${fmt(x.entropy, 3)}</b><small>entropía (bits/byte)</small><div class="bar"><i style="width:${(100 * x.entropy) / 8}%"></i></div></div>
                <div class="stat"><b><span class="badge ${x.detectsTampering ? "ok" : "no"}">${x.detectsTampering ? "Sí" : "No"}</span></b><small>detecta alteración</small></div>
              </div>
              <p class="muted"><small>${esc(x.notes)}</small></p>
              <p class="hex">${esc(x.outputHex)}</p>
            </article>`).join("")}</div>`;
      } catch (err) { toast(err.message, true); }
    });
  });
};

/**
 * Vista "Cuenta y seguridad": datos del usuario (rol, estado del 2FA, fecha de alta) y la explicación
 * de la imagen-llave; activar el 2FA (elegir imagen y confirmar la contraseña, se descarga llave.png)
 * o desactivarlo (con la contraseña); y el formulario de cambio de contraseña.
 * @returns {Promise<void>}
 */
views.cuenta = async () => {
  app.innerHTML = `<h1>Cuenta y seguridad</h1>
    <div class="grid two"><section class="card" id="me">Cargando…</section><section class="card" id="tfa"></section></div>
    <div class="grid two" style="margin-top:16px"><section class="card" id="pwc"></section></div>`;
  passwordForm($("#pwc"), false);
  let me;
  try { me = await api("/api/account/me"); } catch (err) { $("#me").textContent = err.message; return; }
  // Las fechas del servidor pueden llegar sin zona horaria; se les agrega "Z" para interpretarlas
  // como UTC y mostrarlas en la hora local.
  $("#me").innerHTML = `
    <h2>${esc(me.username)} <span class="badge info">${esc(me.role)}</span></h2>
    <p>Segundo factor (imagen-llave): <span class="badge ${me.twoFactorEnabled ? "ok" : "no"}">${me.twoFactorEnabled ? "activado" : "desactivado"}</span></p>
    <p class="muted"><small>Cuenta creada el ${new Date(me.createdUtc + (me.createdUtc.endsWith("Z") ? "" : "Z")).toLocaleString("es-MX")}.</small></p>
    <h3>¿Cómo funciona la imagen-llave?</h3>
    <p class="muted"><small>El servidor genera un secreto aleatorio de 32 bytes, lo oculta con ABCO en la imagen que elijas usando tu contraseña como frase, y solo guarda su huella SHA-256. Al iniciar sesión, revela el secreto con tu contraseña y compara la huella. Sin la imagen no se entra, y la imagen sola no sirve sin la contraseña. Si cambias de contraseña, tendrás que generar una nueva.</small></p>`;
  if (me.twoFactorEnabled) {
    $("#tfa").innerHTML = `
      <h2>Desactivar 2FA</h2>
      <form id="off"><label for="pw">Contraseña</label>${pwInput("pw", 'autocomplete="current-password" required')}
      <div class="actions"><button class="danger" id="go">Desactivar</button></div></form>
      <h3>¿Perdiste tu imagen-llave?</h3><p class="muted"><small>Un administrador puede restablecer tu 2FA desde la consola de administración.</small></p>`;
    $("#off").addEventListener("submit", (e) => {
      e.preventDefault();
      busy($("#go"), async () => {
        // Tras desactivar se vuelve a pintar la vista (ahora con el formulario de activación) y se
        // actualiza la sesión local.
        try { await api("/api/account/2fa/disable", { method: "POST", json: { password: $("#pw").value } }); toast("2FA desactivado."); views.cuenta(); updateSession({ twoFactorEnabled: false }); }
        catch (err) { toast(err.message, true); }
      });
    });
  } else {
    $("#tfa").innerHTML = `
      <h2>Activar 2FA con imagen-llave</h2>
      <form id="on">
        <label>Imagen para tu llave</label><div id="src"></div>
        <label for="pw">Confirma tu contraseña</label>${pwInput("pw", 'autocomplete="current-password" required')}
        <div class="actions"><button id="go">Generar mi imagen-llave</button></div>
      </form>
      <div id="key" aria-live="polite"></div>`;
    const src = imageSource($("#src"));
    $("#on").addEventListener("submit", (e) => {
      e.preventDefault();
      busy($("#go"), async () => {
        try {
          const form = src.append(new FormData());
          form.append("password", $("#pw").value);
          // El servidor devuelve la imagen-llave como PNG. Se descarga en el acto y se muestra con la
          // opción de descargarla de nuevo; la insignia de "Segundo factor" se actualiza sin recargar.
          const res = await api("/api/account/2fa/enable", { method: "POST", form, as: "raw" });
          const blob = await res.blob(), name = filenameFrom(res, "llave.png");
          download(blob, name);
          updateSession({ twoFactorEnabled: true });
          const badge = $("#me .badge.no");
          if (badge) { badge.className = "badge ok"; badge.textContent = "activado"; }
          $("#on").remove();
          $("#key").innerHTML = `
            <div class="message-box">2FA activado. Se descargó <b>${esc(name)}</b>. Guárdala en un lugar seguro: la necesitarás cada vez que inicies sesión.</div>
            <img class="preview" style="margin-top:12px" src="${URL.createObjectURL(blob)}" alt="Tu imagen-llave">
            <div class="actions"><button class="secondary" id="again">Descargar de nuevo</button></div>`;
          $("#again").addEventListener("click", () => download(blob, name));
        } catch (err) { toast(err.message, true); }
      });
    });
  }
};

/**
 * Formulario de cambio de contraseña. forced = true en la vista obligatoria tras un restablecimiento.
 * El servidor entrega una sesión nueva; si había 2FA, se desactiva (la llave dependía de la contraseña anterior).
 * @param {HTMLElement} root Contenedor donde se pinta el formulario.
 * @param {boolean} forced true en la vista obligatoria (pide la contraseña temporal y al terminar
 *   lleva a Ocultar); false en Cuenta (al terminar vuelve a pintar la vista).
 */
function passwordForm(root, forced) {
  root.innerHTML = `
    <h2>${forced ? "Cambia tu contraseña" : "Cambiar contraseña"}</h2>
    ${forced ? `<p class="lead">Entraste con una contraseña temporal. Elige una nueva para continuar.</p>` : ""}
    <form id="pwf">
      <label for="cur">${forced ? "Contraseña temporal" : "Contraseña actual"}</label>${pwInput("cur", 'autocomplete="current-password" required')}
      <label for="np">Nueva contraseña</label>${pwInput("np", 'autocomplete="new-password" minlength="8" required')}
      <label for="np2">Repite la nueva contraseña</label>${pwInput("np2", 'autocomplete="new-password" minlength="8" required')}
      <div class="actions"><button id="pwgo">Guardar contraseña</button></div>
    </form>`;
  $("#pwf", root).addEventListener("submit", (e) => {
    e.preventDefault();
    if ($("#np", root).value !== $("#np2", root).value) return toast("Las contraseñas nuevas no coinciden.", true);
    busy($("#pwgo", root), async () => {
      try {
        const r = await api("/api/account/password", { method: "POST", json: { currentPassword: $("#cur", root).value, newPassword: $("#np", root).value } });
        // El token anterior deja de servir: se reemplaza la sesión completa por la que devuelve el
        // servidor (token nuevo y mustChangePassword en false).
        session.set(r.session);
        updateNav();
        toast(r.twoFactorDisabled ? "Contraseña cambiada. Tu 2FA se desactivó: genera una nueva imagen-llave en Cuenta." : "Contraseña cambiada.");
        if (forced) location.hash = "#/ocultar"; else views.cuenta();
      } catch (err) { toast(err.message, true); }
    });
  });
}

/**
 * Vista de cambio obligatorio de contraseña, a la que el enrutador lleva a quien entró con una
 * contraseña temporal. Solo ofrece el formulario y un enlace para cerrar sesión, que reutiliza el
 * botón "Salir" de la barra (oculta mientras el cambio esté pendiente).
 */
views.cambiar = () => {
  app.innerHTML = `<div class="auth-wrap"><section class="card" id="pwc"></section>
    <p><button type="button" class="link" id="out">Cerrar sesión</button></p></div>`;
  passwordForm($("#pwc"), true);
  $("#out").addEventListener("click", () => $("#logoutBtn").click());
};

/**
 * Consola de administración: traza paso a paso de la última operación ABCO, mapa de diferencias
 * entre la imagen original y la estego, tabla de usuarios (nombres ofuscados con César y acciones:
 * revelar el nombre, restablecer contraseña, cambiar rol, restablecer 2FA) y la auditoría reciente.
 * La comprobación del rol aquí solo decide qué se pinta; el servidor la vuelve a aplicar en cada ruta.
 * @returns {Promise<void>}
 */
views.admin = async () => {
  if (current()?.role !== "admin") { app.innerHTML = `<div class="card error-box">Solo el rol administrador puede ver esta consola.</div>`; return; }
  app.innerHTML = `
    <h1>Consola de administración</h1>
    <p class="lead">Los nombres de usuario se muestran <b>ofuscados con César</b> (ASCII imprimible, k = 13). Es ofuscación, no cifrado: el botón "Revelar" la deshace.</p>
    <div class="grid two">
      <section class="card"><h2>Traza de la última operación ABCO</h2><div id="trace" class="muted">Cargando…</div>
        <div class="actions"><button class="secondary" id="reload">Actualizar</button></div></section>
      <section class="card"><h2>Mapa de diferencias |original − estego|</h2>
        <p class="muted"><small>Cada punto blanco es un píxel cuyo bit menos significativo cambió. Con ABCO quedan dispersos por el recorrido; con la esteganografía clásica se ven en franja al inicio.</small></p>
        <div id="diff" class="empty">Sin datos todavía.</div></section>
    </div>
    <section class="card" style="margin-top:16px"><h2>Usuarios</h2><div id="users">Cargando…</div></section>
    <section class="card" style="margin-top:16px"><h2>Auditoría</h2><div id="audit">Cargando…</div></section>`;

  /**
   * Carga la traza de la última operación ABCO y su mapa de diferencias (PNG). Si aún no hay ninguna
   * operación, el mapa falla en silencio y se queda el aviso "Sin datos todavía".
   */
  const loadTrace = async () => {
    try {
      const t = await api("/api/admin/trace/last");
      $("#trace").innerHTML = `<p><small>Usuario: <code>${esc(t.user)}</code> · ${new Date(t.trace.utc).toLocaleString("es-MX")}</small></p><pre>${esc(t.text)}</pre>`;
    } catch (err) { $("#trace").textContent = err.message; }
    try {
      const blob = await api("/api/admin/diffmap/last", { as: "blob" });
      $("#diff").outerHTML = `<img id="diff" class="preview" src="${URL.createObjectURL(blob)}" alt="Mapa de diferencias">`;
    } catch { }
  };
  /**
   * Pinta la tabla de usuarios. El nombre llega ofuscado con César; el valor ofuscado se guarda en
   * `data-obf` para que "Revelar" pueda alternar entre ambas formas.
   */
  const loadUsers = async () => {
    const users = await api("/api/admin/users");
    $("#users").innerHTML = `<div class="table-wrap"><table><thead><tr><th>Id</th><th>Usuario (ofuscado)</th><th>Rol</th><th>2FA</th><th>Acciones</th></tr></thead><tbody>
      ${users.map((u) => `<tr>
        <td class="num">${u.id}</td>
        <td><div class="cell-actions"><code data-obf="${esc(u.username)}">${esc(u.username)}</code><button class="link" data-reveal>Revelar</button></div></td>
        <td><span class="badge ${u.role === "admin" ? "info" : "neutral"}">${u.role === "admin" ? "administrador" : "usuario"}</span>
          ${u.mustChangePassword ? `<br><small class="muted">contraseña temporal pendiente</small>` : ""}</td>
        <td><div class="cell-actions">${u.twoFactorEnabled ? `<span class="badge ok">sí</span><button class="link" data-reset2fa="${u.id}">Restablecer 2FA</button>` : `<span class="badge no">no</span>`}</div></td>
        <td><div class="cell-actions">
          <button class="link" data-resetpw="${u.id}">Restablecer contraseña</button>
          <button class="link" data-role="${u.id}" data-to="${u.role === "admin" ? "user" : "admin"}">${u.role === "admin" ? "Quitar administrador" : "Hacer administrador"}</button>
        </div></td></tr>`).join("")}
      </tbody></table></div>`;
  };
  /** Pinta las últimas 60 entradas de la auditoría (fecha, usuario, acción, modo, bytes, tiempo y resultado). */
  const loadAudit = async () => {
    const rows = await api("/api/admin/audit?take=60");
    $("#audit").innerHTML = `<div class="table-wrap"><table><thead><tr><th>Fecha</th><th>Usuario</th><th>Acción</th><th>Modo</th><th>Bytes</th><th>ms</th><th>Resultado</th></tr></thead><tbody>
      ${rows.map((a) => `<tr><td>${new Date(a.utc + (a.utc.endsWith("Z") ? "" : "Z")).toLocaleString("es-MX")}</td><td><code>${esc(a.user)}</code></td><td>${esc(a.action)}</td>
        <td>${esc(a.mode ?? "")}</td><td class="num">${a.bytes}</td><td class="num">${a.ms}</td><td><span class="badge ${a.ok ? "ok" : "no"}">${a.ok ? "ok" : "falló"}</span></td></tr>`).join("")}
      </tbody></table></div>`;
  };

  $("#reload").addEventListener("click", loadTrace);
  // Delegación de eventos para los botones de cada fila (la tabla se vuelve a pintar tras cada acción).
  $("#users").addEventListener("click", async (e) => {
    const rev = e.target.closest("[data-reveal]"), reset = e.target.closest("[data-reset2fa]");
    const resetPw = e.target.closest("[data-resetpw]"), role = e.target.closest("[data-role]");
    try {
      if (resetPw) {
        const ok = await showDialog(`<h2>Restablecer contraseña</h2><p>Se generará una contraseña temporal y el usuario deberá cambiarla al entrar. Si tenía 2FA, se desactivará.</p>`,
          [{ label: "Restablecer", value: true, primary: true }, { label: "Cancelar", value: false }]);
        if (!ok) return;
        // La contraseña temporal solo se muestra esta vez; se ofrece copiarla al portapapeles.
        const r = await api(`/api/admin/users/${resetPw.dataset.resetpw}/reset-password`, { method: "POST" });
        const copy = await showDialog(`<h2>Contraseña temporal</h2><p class="temp-pass">${esc(r.temporaryPassword)}</p>
          <p class="muted"><small>Se muestra una sola vez. Entrégala al usuario por un medio seguro.</small></p>`,
          [{ label: "Copiar y cerrar", value: true, primary: true }, { label: "Cerrar", value: false }]);
        if (copy) { try { await navigator.clipboard.writeText(r.temporaryPassword); toast("Copiada al portapapeles."); } catch { toast("No se pudo copiar; anótala antes de cerrar.", true); } }
        loadUsers();
      }
      if (role) {
        await api(`/api/admin/users/${role.dataset.role}/role`, { method: "POST", json: { role: role.dataset.to } });
        toast(role.dataset.to === "admin" ? "Ahora es administrador." : "Ya no es administrador.");
        loadUsers();
      }
      if (rev) {
        // Alterna entre el nombre ofuscado y el real; el servidor deshace el César (k = 13).
        const code = rev.previousElementSibling;
        const r = await api("/api/admin/deobfuscate", { method: "POST", json: { text: code.dataset.obf } });
        code.textContent = code.textContent === r.text ? code.dataset.obf : r.text;
      }
      if (reset && await showDialog(`<h2>Restablecer 2FA</h2><p>El usuario podrá entrar solo con su contraseña y deberá generar otra imagen-llave.</p>`,
          [{ label: "Restablecer", value: true, primary: true }, { label: "Cancelar", value: false }])) {
        await api(`/api/admin/users/${reset.dataset.reset2fa}/reset-2fa`, { method: "POST" });
        toast("2FA restablecido."); loadUsers();
      }
    } catch (err) { toast(err.message, true); }
  });
  // Las tres secciones cargan en paralelo y el fallo de una no impide mostrar las demás.
  await Promise.allSettled([loadTrace(), loadUsers().catch((e) => ($("#users").textContent = e.message)), loadAudit().catch((e) => ($("#audit").textContent = e.message))]);
};

// ---------- Navegación ----------
/**
 * Mezcla cambios en la sesión guardada sin tocar el token (p. ej., el estado del 2FA).
 * @param {object} patch Campos a actualizar.
 */
function updateSession(patch) { const s = current(); if (s) session.set({ ...s, ...patch }); }

/**
 * Ajusta la barra de navegación a la sesión: se oculta sin sesión o con la contraseña temporal
 * pendiente, el enlace Admin solo aparece para el rol administrador y se resalta la ruta actual.
 */
function updateNav() {
  const s = current();
  const nav = $("#nav");
  nav.hidden = !s || !!s.mustChangePassword;
  $("#menuBtn").hidden = !s;
  nav.querySelectorAll("[data-admin]").forEach((a) => (a.hidden = s?.role !== "admin"));
  nav.querySelectorAll("a").forEach((a) => a.classList.toggle("active", a.getAttribute("href") === location.hash));
}

/** Vistas accesibles sin sesión. */
const publicViews = new Set(["login", "registro"]);
/**
 * Enrutador por hash: lee la vista de `#/nombre` (ignora lo que siga a "?") y aplica, en orden:
 * 1. Sin sesión, solo se permiten las vistas públicas; lo demás lleva al login.
 * 2. Con contraseña temporal pendiente, todo lleva a #/cambiar.
 * 3. Con sesión, las vistas públicas, las desconocidas y #/cambiar sin cambio pendiente llevan a #/ocultar.
 * Cada redirección cambia el hash, lo que vuelve a disparar el enrutador. Si la vista procede,
 * cierra el menú móvil, actualiza la barra, pinta la vista y mueve el foco a #app (accesibilidad).
 */
function route() {
  const name = (location.hash.replace(/^#\//, "") || "").split("?")[0];
  const s = current();
  if (!s && !publicViews.has(name)) { location.hash = "#/login"; return; }
  if (s?.mustChangePassword && name !== "cambiar") { location.hash = "#/cambiar"; return; }
  if (s && (publicViews.has(name) || !views[name] || (name === "cambiar" && !s.mustChangePassword))) { location.hash = "#/ocultar"; return; }
  $("#nav").classList.remove("open");
  $("#menuBtn").setAttribute("aria-expanded", "false");
  updateNav();
  views[name]();
  app.focus({ preventScroll: true });
  window.scrollTo(0, 0);
}

// Botón de menú en pantallas angostas: abre o cierra la barra de navegación.
$("#menuBtn").addEventListener("click", () => {
  const open = $("#nav").classList.toggle("open");
  $("#menuBtn").setAttribute("aria-expanded", String(open));
});
// Cerrar sesión borra la sesión local (el token deja de enviarse) y vuelve al login.
$("#logoutBtn").addEventListener("click", () => { session.clear(); toast("Sesión cerrada."); location.hash = "#/login"; updateNav(); });
// Arranque: se enruta cada cambio de hash y una vez al cargar la página.
window.addEventListener("hashchange", route);
route();
