// Service worker: guarda en caché solo la estructura estática para que la PWA abra sin conexión. NUNCA guarda /api
// ni /hubs: la telemetría y las llaves siempre deben venir de la red. (Un service worker no puede leer sensores).
/** Nombre de la caché; cambiarlo obliga a descartar la versión anterior. */
const CACHE = "signaltrace-shell-v2";
/** Archivos que se descargan al instalar. */
const SHELL = ["index.html", "agent.html", "app.html", "css/base.css", "css/layout.css", "js/api.js", "js/ui/shell.js",
  "js/agent.js", "js/crypto/util.js", "js/crypto/ycm1.js", "js/crypto/session.js", "icons/favicon.png", "icons/icon-192.png", "manifest.webmanifest"];

// Instalación: guarda la estructura y se activa de inmediato.
self.addEventListener("install", (e) => e.waitUntil(caches.open(CACHE).then((c) => c.addAll(SHELL)).then(() => self.skipWaiting())));
// Activación: borra las cachés de versiones anteriores y toma el control de las pestañas abiertas.
self.addEventListener("activate", (e) => e.waitUntil(caches.keys()
  .then((keys) => Promise.all(keys.filter((k) => k !== CACHE).map((k) => caches.delete(k)))).then(() => self.clients.claim())));

self.addEventListener("fetch", (e) => {
  const url = new URL(e.request.url);
  // Solo intercepta GET del mismo origen que no sean de la API ni del hub.
  if (e.request.method !== "GET" || url.origin !== location.origin || url.pathname.startsWith("/api") || url.pathname.startsWith("/hubs")) return;
  // Primero la red (código siempre actualizado) y la caché como respaldo sin conexión.
  e.respondWith(fetch(e.request).then((res) => {
    if (res.ok) { const copy = res.clone(); caches.open(CACHE).then((c) => c.put(e.request, copy)); }
    return res;
  }).catch(() => caches.match(e.request)));
});
