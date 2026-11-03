// Estructura común de las páginas: protección por sesión, barra superior, selector de tema (auto / claro / oscuro)
// y registro del service worker.
import { api, getAuth, logout, store, esc, toast } from "../api.js";

/** Logo de la barra superior. */
export const LOGO = `<img src="icons/icon-192.png" alt="" width="30" height="30">`;

/** Páginas del menú; las marcadas con admin solo las ve un administrador. */
const PAGES = [
  { href: "app.html", label: "Dashboard" },
  { href: "agent.html", label: "Agente" },
  { href: "console.html", label: "Consola", admin: true },
  { href: "lab.html", label: "Laboratorio", admin: true },
  { href: "admin.html", label: "Usuarios", admin: true },
];

/**
 * Aplica el tema guardado (o el indicado) y avisa con el evento "st-theme" para que las gráficas cambien de color.
 * @param {"auto"|"light"|"dark"} [t] Tema; "auto" sigue la preferencia del sistema.
 */
export function applyTheme(t = store.read("st.theme") || "auto") {
  if (t === "auto") delete document.documentElement.dataset.theme;
  else document.documentElement.dataset.theme = t;
  dispatchEvent(new CustomEvent("st-theme"));
}
// Si el sistema cambia entre claro y oscuro, las gráficas también se recolorean.
matchMedia("(prefers-color-scheme: dark)").addEventListener("change", () => dispatchEvent(new CustomEvent("st-theme")));
applyTheme();

/**
 * Redirige al inicio de sesión cuando no hay una sesión válida (o el rol no alcanza). Devuelve la sesión.
 * Lanza una excepción después de redirigir para detener el resto del script de la página.
 * @param {{admin?: boolean}} [opts] Si admin es true, exige el rol de administrador.
 */
export function requireAuth({ admin = false } = {}) {
  const auth = getAuth();
  if (!auth) { location.replace("index.html?next=" + encodeURIComponent(location.pathname.split("/").pop())); throw new Error("no session"); }
  if (admin && auth.role !== "admin") { location.replace("app.html"); throw new Error("forbidden"); }
  return auth;
}

/**
 * Barra superior. En teléfonos (< 760 px) la navegación se pliega en un panel con botón de hamburguesa.
 * minimal: solo la marca y el tema (un teléfono vinculado por QR no tiene sesión de usuario).
 * @param {string} active Página actual, para marcarla en el menú.
 * @param {{minimal?: boolean}} [opts]
 */
export function renderShell(active, { minimal = false } = {}) {
  const auth = minimal ? null : getAuth();
  const bar = document.createElement("header");
  bar.className = "topbar";
  const links = minimal ? "" : PAGES.filter((p) => !p.admin || auth?.role === "admin")
    .map((p) => `<a href="${p.href}" ${p.href === active ? 'aria-current="page"' : ""}>${p.label}</a>`).join("");
  bar.innerHTML = `
    <a class="brand" href="${minimal ? "agent.html" : "app.html"}">${LOGO}<span>SignalTrace</span></a>
    ${minimal ? "" : `<button type="button" class="nav-toggle" aria-label="Menú" aria-expanded="false" aria-controls="main-nav">☰</button>`}
    <nav class="nav${minimal ? " minimal" : ""}" id="main-nav" aria-label="Principal">${links}
      <select id="theme" aria-label="Tema" style="width:auto;min-height:34px;padding:4px 6px">
        <option value="auto">Tema: auto</option><option value="light">Claro</option><option value="dark">Oscuro</option>
      </select>
      ${auth ? `<details class="user-menu"><summary>${esc(auth.username)} · ${esc(auth.role)}</summary>
        <div class="menu"><button type="button" id="pw-open">Cambiar contraseña</button><button type="button" id="logout">Salir</button></div></details>` : ""}
    </nav>`;
  document.body.prepend(bar);
  // Selector de tema: guarda la elección del usuario.
  const sel = bar.querySelector("#theme");
  sel.value = store.read("st.theme") || "auto";
  sel.onchange = () => { store.write("st.theme", sel.value); applyTheme(sel.value); };
  bar.querySelector("#logout")?.addEventListener("click", () => logout());
  bar.querySelector("#pw-open")?.addEventListener("click", () => { closeMenus(); changePasswordDialog(); });

  // Panel de hamburguesa (teléfonos) y menú desplegable del usuario: se cierran con Escape, al elegir un enlace o al pulsar fuera.
  const toggle = bar.querySelector(".nav-toggle");
  const userMenu = bar.querySelector(".user-menu");
  /** Cierra el panel de navegación y el menú del usuario. */
  function closeMenus() {
    bar.classList.remove("nav-open");
    toggle?.setAttribute("aria-expanded", "false");
    if (userMenu) userMenu.open = false;
  }
  toggle?.addEventListener("click", () => {
    const open = bar.classList.toggle("nav-open");
    toggle.setAttribute("aria-expanded", String(open));
  });
  bar.querySelectorAll(".nav a").forEach((a) => a.addEventListener("click", closeMenus));
  document.addEventListener("click", (e) => {
    if (!bar.contains(e.target)) closeMenus();
    else if (userMenu?.open && !userMenu.contains(e.target)) userMenu.open = false;
  });
  document.addEventListener("keydown", (e) => { if (e.key === "Escape") closeMenus(); });
  registerServiceWorker();
}

/** Diálogo para que cualquier usuario cambie su propia contraseña; el servidor verifica primero la actual. */
function changePasswordDialog() {
  const dlg = document.createElement("dialog");
  dlg.className = "modal";
  dlg.setAttribute("aria-labelledby", "pw-title");
  dlg.innerHTML = `<form method="dialog" id="pw-form">
      <h2 id="pw-title">Cambiar contraseña</h2>
      <div class="field"><label for="pw-cur">Contraseña actual</label><input id="pw-cur" type="password" autocomplete="current-password" required></div>
      <div class="field"><label for="pw-new">Contraseña nueva (8 a 128 caracteres)</label><input id="pw-new" type="password" autocomplete="new-password" minlength="8" maxlength="128" required></div>
      <div class="field"><label for="pw-rep">Repite la contraseña nueva</label><input id="pw-rep" type="password" autocomplete="new-password" required></div>
      <p class="small muted">Se guarda solo su derivación PBKDF2-HMAC-SHA256 con una sal nueva.</p>
      <p id="pw-err" class="small" style="color:var(--bad)" role="alert"></p>
      <div class="row" style="justify-content:flex-end"><button type="button" id="pw-cancel">Cancelar</button><button class="primary" id="pw-save">Guardar</button></div>
    </form>`;
  document.body.append(dlg);
  const q = (id) => dlg.querySelector(id);
  q("#pw-cancel").onclick = () => dlg.close();
  // El diálogo se elimina del documento al cerrarse.
  dlg.addEventListener("close", () => dlg.remove());
  q("#pw-form").onsubmit = async (e) => {
    e.preventDefault();
    if (q("#pw-new").value !== q("#pw-rep").value) { q("#pw-err").textContent = "Las contraseñas nuevas no coinciden"; return; }
    q("#pw-save").disabled = true;
    try {
      await api("/api/auth/change-password", { method: "POST", body: { current: q("#pw-cur").value, new: q("#pw-new").value } });
      dlg.close();
      toast("Contraseña actualizada");
    } catch (err) { q("#pw-err").textContent = err.message; }
    finally { q("#pw-save").disabled = false; }
  };
  dlg.showModal();
  q("#pw-cur").focus();
}

/** Registra el service worker (solo en contextos seguros, donde el navegador lo permite). */
export function registerServiceWorker() {
  if ("serviceWorker" in navigator && isSecureContext) navigator.serviceWorker.register("sw.js").catch(() => {});
}
