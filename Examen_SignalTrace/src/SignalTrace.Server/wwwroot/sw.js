// Service worker: caches only the static shell so the PWA opens offline. It NEVER caches /api or /hubs:
// telemetry and keys must always come from the network. (Service workers cannot read sensors or GPS.)
const CACHE = "signaltrace-shell-v1";
const SHELL = ["index.html", "agent.html", "app.html", "css/base.css", "css/layout.css", "js/api.js", "js/ui/shell.js",
  "js/agent.js", "js/crypto/util.js", "js/crypto/ycm1.js", "js/crypto/session.js", "icons/icon.svg", "manifest.webmanifest"];

self.addEventListener("install", (e) => e.waitUntil(caches.open(CACHE).then((c) => c.addAll(SHELL)).then(() => self.skipWaiting())));
self.addEventListener("activate", (e) => e.waitUntil(caches.keys()
  .then((keys) => Promise.all(keys.filter((k) => k !== CACHE).map((k) => caches.delete(k)))).then(() => self.clients.claim())));

self.addEventListener("fetch", (e) => {
  const url = new URL(e.request.url);
  if (e.request.method !== "GET" || url.origin !== location.origin || url.pathname.startsWith("/api") || url.pathname.startsWith("/hubs")) return;
  // Network first (always fresh code), cache as offline fallback.
  e.respondWith(fetch(e.request).then((res) => {
    if (res.ok) { const copy = res.clone(); caches.open(CACHE).then((c) => c.put(e.request, copy)); }
    return res;
  }).catch(() => caches.match(e.request)));
});
