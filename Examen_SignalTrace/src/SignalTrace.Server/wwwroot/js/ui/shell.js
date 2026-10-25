// Shared page chrome: auth guard, top bar, theme switch (auto / light / dark) and service worker.
import { getAuth, logout, store, esc } from "../api.js";

export const LOGO = `<svg viewBox="0 0 32 32" aria-hidden="true"><rect width="32" height="32" rx="8" fill="var(--accent)"/>
<path d="M7 22a12 12 0 0 1 0-12M11 19a7 7 0 0 1 0-6M25 22a12 12 0 0 0 0-12M21 19a7 7 0 0 0 0-6" stroke="var(--accent-text)" stroke-width="2.2" fill="none" stroke-linecap="round"/>
<circle cx="16" cy="16" r="2.6" fill="var(--accent-text)"/></svg>`;

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
}
applyTheme();

/** Redirects to the login when there is no valid session (or the role is insufficient). Returns the session. */
export function requireAuth({ admin = false } = {}) {
  const auth = getAuth();
  if (!auth) { location.replace("index.html?next=" + encodeURIComponent(location.pathname.split("/").pop())); throw new Error("no session"); }
  if (admin && auth.role !== "admin") { location.replace("app.html"); throw new Error("forbidden"); }
  return auth;
}

export function renderShell(active) {
  const auth = getAuth();
  const bar = document.createElement("header");
  bar.className = "topbar";
  const links = PAGES.filter((p) => !p.admin || auth?.role === "admin")
    .map((p) => `<a href="${p.href}" ${p.href === active ? 'aria-current="page"' : ""}>${p.label}</a>`).join("");
  bar.innerHTML = `
    <a class="brand" href="app.html">${LOGO}<span>SignalTrace</span></a>
    <nav class="nav" aria-label="Principal">${links}
      <select id="theme" aria-label="Tema" style="width:auto;min-height:34px;padding:4px 6px">
        <option value="auto">Tema: auto</option><option value="light">Claro</option><option value="dark">Oscuro</option>
      </select>
      ${auth ? `<span class="user">${esc(auth.username)} · ${esc(auth.role)}</span><a href="#" id="logout">Salir</a>` : ""}
    </nav>`;
  document.body.prepend(bar);
  const sel = bar.querySelector("#theme");
  sel.value = store.read("st.theme") || "auto";
  sel.onchange = () => { store.write("st.theme", sel.value); applyTheme(sel.value); };
  bar.querySelector("#logout")?.addEventListener("click", (e) => { e.preventDefault(); logout(); });
  registerServiceWorker();
}

export function registerServiceWorker() {
  if ("serviceWorker" in navigator && isSecureContext) navigator.serviceWorker.register("sw.js").catch(() => {});
}
