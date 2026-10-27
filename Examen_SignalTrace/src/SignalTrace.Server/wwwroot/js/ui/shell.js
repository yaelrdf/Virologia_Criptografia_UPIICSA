// Shared page chrome: auth guard, top bar, theme switch (auto / light / dark) and service worker.
import { api, getAuth, logout, store, esc, toast } from "../api.js";

export const LOGO = `<img src="icons/icon-192.png" alt="" width="30" height="30">`;

const PAGES = [
  { href: "app.html", label: "Dashboard" },
  { href: "agent.html", label: "Agente" },
  { href: "console.html", label: "Consola", admin: true },
  { href: "lab.html", label: "Laboratorio", admin: true },
  { href: "admin.html", label: "Usuarios", admin: true },
];

export function applyTheme(t = store.read("st.theme") || "auto") {
  if (t === "auto") delete document.documentElement.dataset.theme;
  else document.documentElement.dataset.theme = t;
  dispatchEvent(new CustomEvent("st-theme"));
}
matchMedia("(prefers-color-scheme: dark)").addEventListener("change", () => dispatchEvent(new CustomEvent("st-theme")));
applyTheme();

/** Redirects to the login when there is no valid session (or the role is insufficient). Returns the session. */
export function requireAuth({ admin = false } = {}) {
  const auth = getAuth();
  if (!auth) { location.replace("index.html?next=" + encodeURIComponent(location.pathname.split("/").pop())); throw new Error("no session"); }
  if (admin && auth.role !== "admin") { location.replace("app.html"); throw new Error("forbidden"); }
  return auth;
}

/** Top bar. minimal: only brand and theme (a phone linked by QR has no user session). */
export function renderShell(active, { minimal = false } = {}) {
  const auth = minimal ? null : getAuth();
  const bar = document.createElement("header");
  bar.className = "topbar";
  const links = minimal ? "" : PAGES.filter((p) => !p.admin || auth?.role === "admin")
    .map((p) => `<a href="${p.href}" ${p.href === active ? 'aria-current="page"' : ""}>${p.label}</a>`).join("");
  bar.innerHTML = `
    <a class="brand" href="${minimal ? "agent.html" : "app.html"}">${LOGO}<span>SignalTrace</span></a>
    <nav class="nav" aria-label="Principal">${links}
      <select id="theme" aria-label="Tema" style="width:auto;min-height:34px;padding:4px 6px">
        <option value="auto">Tema: auto</option><option value="light">Claro</option><option value="dark">Oscuro</option>
      </select>
      ${auth ? `<details class="user-menu"><summary>${esc(auth.username)} · ${esc(auth.role)}</summary>
        <div class="menu"><button type="button" id="pw-open">Cambiar contraseña</button><button type="button" id="logout">Salir</button></div></details>` : ""}
    </nav>`;
  document.body.prepend(bar);
  const sel = bar.querySelector("#theme");
  sel.value = store.read("st.theme") || "auto";
  sel.onchange = () => { store.write("st.theme", sel.value); applyTheme(sel.value); };
  bar.querySelector("#logout")?.addEventListener("click", () => logout());
  bar.querySelector("#pw-open")?.addEventListener("click", () => { bar.querySelector(".user-menu").open = false; changePasswordDialog(); });
  registerServiceWorker();
}

/** Any user changes their own password; the server verifies the current one first. */
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

export function registerServiceWorker() {
  if ("serviceWorker" in navigator && isSecureContext) navigator.serviceWorker.register("sw.js").catch(() => {});
}
