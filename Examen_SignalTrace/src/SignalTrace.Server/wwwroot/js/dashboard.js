// Dashboard (PLAN §11.1): live device list, charts with SMA/EMA, DFT spectrum, battery trend, map with route,
// signal-colored points and coverage heatmap, interpolated time grid, alerts and exports.
import { api, getAuth, store, toast, esc, fmtTime, fmtDateTime } from "./api.js";
import { requireAuth, renderShell } from "./ui/shell.js";
import * as C from "./ui/charts.js";
import qrcode from "../lib/qrcode/qrcode.mjs";

const auth = requireAuth();
renderShell("app.html");
const $ = (id) => document.getElementById(id);

const st = {
  devices: new Map(),       // id -> device
  selected: null,
  readings: [],             // readings of the selected device (oldest first)
  alerts: [],
  track: [],                // [lat, lon, rssi]
  distanceM: 0,
  spectrum: null,
  last: null,
};

// ---------- math shown on the client (the server does the same in Analytics/Numeric.cs)
const R = 6_371_000;
function haversine(a, b) {
  const rad = (d) => (d * Math.PI) / 180;
  const dLat = rad(b[0] - a[0]), dLon = rad(b[1] - a[1]);
  const h = Math.sin(dLat / 2) ** 2 + Math.cos(rad(a[0])) * Math.cos(rad(b[0])) * Math.sin(dLon / 2) ** 2;
  return 2 * R * Math.asin(Math.min(1, Math.sqrt(h)));
}
function smaStep(arr, w) { const s = arr.slice(-w); return s.reduce((a, b) => a + b, 0) / s.length; }
const signalColor = (dbm) => `hsl(${Math.max(0, Math.min(120, ((dbm + 95) / 50) * 120))} 70% 45%)`;

// ---------- charts
const chRssi = C.timeChart($("c-rssi"), [{ label: "RSSI", color: C.SERIES.blue, points: 1.5 }, { label: "EMA", color: C.SERIES.orange, width: 2.2 }], { yLabel: "dBm" });
const chRtt = C.timeChart($("c-rtt"), [{ label: "RTT (ms)", color: C.SERIES.purple }, { label: "EMA RTT", color: C.SERIES.orange, width: 2.2 }, { label: "Pérdida (%)", color: C.SERIES.red, dashed: true }], { yLabel: "ms / %" });
const chMot = C.timeChart($("c-mot"), [{ label: "RMS", color: C.SERIES.teal, points: 1 }, { label: "SMA(10)", color: C.SERIES.purple, dashed: true }, { label: "EMA", color: C.SERIES.orange, width: 2.2 }], { yLabel: "m/s²" });
const chBat = C.timeChart($("c-bat"), [{ label: "Batería (%)", color: C.SERIES.green, fill: true }, { label: "Proyección (regresión)", color: C.SERIES.red, dashed: true }], { yLabel: "%", yMin: 0, yMax: 100 });
const chSpec = C.xyChart($("c-spec"), "|X(f)|", C.SERIES.teal, { xLabel: "Hz", yLabel: "|X_k| / N" });
const chGrid = C.timeChart($("c-grid"), [{ label: "Malla regular", color: C.SERIES.blue, points: 2.5 }], { legend: false });
const motWindow = [];

let pending = false;
function redraw() {
  if (pending) return;
  pending = true;
  requestAnimationFrame(() => { pending = false; [chRssi, chRtt, chMot, chBat].forEach((c) => c.update("none")); });
}

// ---------- device list
function renderDevices() {
  const list = [...st.devices.values()].sort((a, b) => Number(b.online) - Number(a.online) || a.name.localeCompare(b.name));
  $("dev-count").textContent = `${list.filter((d) => d.online).length}/${list.length} en línea`;
  $("devices").innerHTML = list.map((d) => `<li><button data-id="${d.id}" aria-pressed="${d.id === st.selected}">
      <span><b>${esc(d.name)}</b> <span class="muted small">${esc(d.kind)}</span></span>
      <span class="badge ${d.online ? "ok" : "off"}">${d.online ? "en línea" : "fuera"}</span>
      <span class="meta">${d.battery != null ? "🔋 " + Math.round(d.battery * 100) + " % · " : ""}${d.lastRssi != null ? "📶 " + d.lastRssi + " dBm · " : ""}${fmtTime(d.lastSeen)}</span>
    </button></li>`).join("") || `<li class="muted small">Sin dispositivos todavía.</li>`;
}
$("devices").addEventListener("click", (e) => { const b = e.target.closest("button[data-id]"); if (b) select(b.dataset.id); });

async function loadDevices() {
  const list = await api("/api/devices");
  st.devices = new Map(list.map((d) => [d.id, d]));
  renderDevices();
  const saved = store.read("st.dash.device");
  if (!st.selected) {
    const pick = st.devices.has(saved) ? saved : list.find((d) => d.online)?.id ?? list[0]?.id;
    if (pick) select(pick);
  }
}

// ---------- alerts
const ALERT_LABEL = {
  low_battery: "Batería baja", low_signal: "Señal débil", link_rtt: "RTT alto", link_loss: "Pérdida de paquetes", offline: "Fuera de línea",
  anomaly_signal: "Anomalía de señal (z)", anomaly_rtt: "Anomalía de RTT (z)", anomaly_motion: "Anomalía de movimiento (z)", vibration: "Vibración en banda",
};
function renderAlerts() {
  const only = $("alerts-only").checked;
  const list = st.alerts.filter((a) => !only || a.deviceId === st.selected).slice(0, 80);
  $("alerts").innerHTML = list.map((a) => `<li class="${a.acknowledged ? "acked" : ""}">
      <span><span class="k">${esc(ALERT_LABEL[a.kind] ?? a.kind)}</span> · ${esc(a.deviceName)}<br><span class="small">${esc(a.message)}</span></span>
      ${a.acknowledged ? "" : `<button data-ack="${a.id}">Atendida</button>`}
      <span class="when">${fmtDateTime(a.ts)}</span></li>`).join("") || `<li class="muted small">Sin alertas.</li>`;
}
$("alerts-only").onchange = renderAlerts;
$("alerts").addEventListener("click", async (e) => {
  const id = e.target.dataset.ack;
  if (!id) return;
  await api(`/api/alerts/${id}/ack`, { method: "POST" });
  const a = st.alerts.find((x) => String(x.id) === id);
  if (a) a.acknowledged = true;
  renderAlerts();
});

// ---------- map
// Wheel zoom only after the user clicks the map, so scrolling the page does not zoom it by accident.
const map = L.map("map", { zoomControl: true, attributionControl: true, scrollWheelZoom: false }).setView([19.3955, -99.0915], 15);
map.on("click focus", () => map.scrollWheelZoom.enable());
map.on("mouseout blur", () => map.scrollWheelZoom.disable());
L.tileLayer("https://tile.openstreetmap.org/{z}/{x}/{y}.png", { maxZoom: 19, attribution: "© OpenStreetMap" }).addTo(map);
const routeLayer = L.polyline([], { color: "#2f7fd8", weight: 3, opacity: 0.85 }).addTo(map);
const pointsLayer = L.layerGroup().addTo(map);
const heatLayer = L.layerGroup();
let marker = null;
const toggle = (layer, on) => (on ? layer.addTo(map) : layer.remove());
$("layer-route").onchange = (e) => toggle(routeLayer, e.target.checked);
$("layer-points").onchange = (e) => toggle(pointsLayer, e.target.checked);
$("layer-heat").onchange = (e) => { toggle(heatLayer, e.target.checked); if (e.target.checked) loadHeatmap(); };
$("fit").onclick = () => { if (st.track.length) map.fitBounds(routeLayer.getBounds(), { padding: [20, 20], maxZoom: 18 }); };

function addFix(r, fit = false) {
  if (r.lat == null) return;
  const p = [r.lat, r.lon];
  const prev = st.track.at(-1);
  if (prev) st.distanceM += haversine(prev, p);
  const sig = r.wifiRssi ?? r.cellDbm;
  st.track.push([r.lat, r.lon, sig]);
  routeLayer.addLatLng(p);
  if (sig != null) {
    L.circleMarker(p, { radius: 5, color: signalColor(sig), fillColor: signalColor(sig), fillOpacity: 0.85, weight: 1 })
      .bindTooltip(`${sig} dBm · ${fmtTime(r.ts)}`).addTo(pointsLayer);
  }
  if (!marker) marker = L.marker(p).addTo(map);
  marker.setLatLng(p);
  if (fit) map.fitBounds(routeLayer.getBounds(), { padding: [20, 20], maxZoom: 18 });
}

async function loadHeatmap() {
  if (!st.selected || !map.hasLayer(heatLayer)) return;
  const h = await api(`/api/heatmap?deviceId=${st.selected}&cellM=25`);
  heatLayer.clearLayers();
  const dLat = (h.cellM / R) * (180 / Math.PI);
  for (const c of h.cells) {
    const dLon = dLat / Math.cos((c.lat * Math.PI) / 180);
    L.rectangle([[c.lat - dLat / 2, c.lon - dLon / 2], [c.lat + dLat / 2, c.lon + dLon / 2]],
      { color: signalColor(c.mean), weight: 0, fillOpacity: 0.45 }).bindTooltip(`media ${c.mean} dBm · ${c.count} muestras`).addTo(heatLayer);
  }
}

// ---------- readings → charts and tiles
function addReading(r, live = true) {
  st.readings.push(r);
  if (st.readings.length > 600) st.readings.shift();
  st.last = r;
  const t = r.ts;
  const sig = r.wifiRssi ?? r.cellDbm;
  if (sig != null) {
    C.push(chRssi, 0, t, sig);
    const prev = chRssi.data.datasets[1].data.at(-1)?.y;
    C.push(chRssi, 1, t, r.rssiEma ?? (prev == null ? sig : 0.3 * sig + 0.7 * prev));
  }
  if (r.probeRtt != null) {
    C.push(chRtt, 0, t, r.probeRtt);
    const prev = chRtt.data.datasets[1].data.at(-1)?.y;
    C.push(chRtt, 1, t, r.rttEma ?? (prev == null ? r.probeRtt : 0.3 * r.probeRtt + 0.7 * prev));
  }
  if (r.probeLoss != null) C.push(chRtt, 2, t, r.probeLoss * 100);
  if (r.accRms != null) {
    motWindow.push(r.accRms);
    if (motWindow.length > 10) motWindow.shift();
    C.push(chMot, 0, t, r.accRms);
    C.push(chMot, 1, t, smaStep(motWindow, 10));
    const prev = chMot.data.datasets[2].data.at(-1)?.y;
    C.push(chMot, 2, t, r.rmsEma ?? (prev == null ? r.accRms : 0.3 * r.accRms + 0.7 * prev));
  }
  if (r.batLvl != null) {
    C.push(chBat, 0, t, r.batLvl * 100);
    if (r.batSlopeH != null) projectBattery(t, r.batLvl, r.batSlopeH);
  }
  addFix(r, false);
  if (live) { redraw(); renderTiles(); }
}

/** Least-squares slope from the server (%/h) projected forward until 0 % or 15 min. */
function projectBattery(t, lvl, slopePerHour) {
  const ds = chBat.data.datasets[1];
  if (slopePerHour >= -1e-4) { ds.data = []; $("bat-note").textContent = "estable o cargando"; return; }
  const hours = Math.min(0.25, lvl / -slopePerHour);
  ds.data = [{ x: t, y: lvl * 100 }, { x: t + hours * 3_600_000, y: Math.max(0, (lvl + slopePerHour * hours) * 100) }];
  $("bat-note").textContent = `pendiente ${(slopePerHour * 100).toFixed(1)} %/h`;
}

function tile(value, label) { return `<div class="stat"><b>${value}</b><span>${label}</span></div>`; }
function renderTiles() {
  const r = st.last;
  const d = st.devices.get(st.selected);
  if (!d) return;
  const sig = r?.wifiRssi ?? r?.cellDbm;
  const eta = r?.batEtaMin != null ? (r.batEtaMin > 120 ? `${(r.batEtaMin / 60).toFixed(1)} h` : `${Math.round(r.batEtaMin)} min`) : "—";
  $("tiles").innerHTML = [
    tile(r ? fmtTime(r.ts) : "—", "última lectura"),
    tile(sig != null ? `${sig} dBm` : "—", r?.rssiSlopeMin != null ? `señal · ${r.rssiSlopeMin >= 0 ? "+" : ""}${r.rssiSlopeMin.toFixed(2)} dBm/min` : "señal"),
    tile(r?.probeRtt != null ? `${Math.round(r.probeRtt)} ms` : "—", r?.probeJit != null ? `RTT · jitter ${r.probeJit} ms` : "RTT medido"),
    tile(r?.batLvl != null ? `${Math.round(r.batLvl * 100)} %` : "—", `batería · restante ${eta}`),
    tile(`${(st.distanceM / 1000).toFixed(2)} km`, `ruta (Haversine, ${st.track.length} puntos)`),
    tile(r?.speed != null ? `${(r.speed * 3.6).toFixed(1)} km/h` : "—", "velocidad"),
    tile(st.spectrum ? `${st.spectrum.toFixed(1)} Hz` : "—", "frecuencia dominante (DFT)"),
    tile(st.readings.length, "lecturas cargadas"),
  ].join("");
  $("d-meta").innerHTML = `${esc(d.kind)} · <span class="badge ${d.online ? "ok" : "off"}">${d.online ? "en línea" : "fuera de línea"}</span> · id <span class="mono">${d.id.slice(0, 8)}…</span>${d.intervalMs ? ` · intervalo ≈ ${(d.intervalMs / 1000).toFixed(1)} s` : ""}`;
}

function renderSpectrum(freqs, mags, dominant) {
  chSpec.data.labels = freqs.map((f) => f.toFixed(1));
  chSpec.data.datasets[0].data = mags;
  chSpec.data.datasets[0].backgroundColor = freqs.map((f) => (Math.abs(f - dominant) < 1e-6 ? C.SERIES.orange : C.SERIES.teal));
  chSpec.update("none");
  st.spectrum = dominant;
  $("spec-note").textContent = `pico en ${dominant.toFixed(2)} Hz · resolución ${(freqs[1] ?? 0).toFixed(2)} Hz`;
}

// ---------- interpolated grid
async function loadGrid() {
  if (!st.selected) return;
  const field = $("grid-field").value;
  const g = await api(`/api/readings/grid?deviceId=${st.selected}&field=${field}&from=${Date.now() - 15 * 60_000}`);
  const scale = field === "battery" || field === "loss" ? 100 : field === "speed" ? 3.6 : 1;
  const pts = g.points.map((p) => ({ x: p.t, y: p.v == null ? null : p.v * scale, i: p.interpolated }));
  const ds = chGrid.data.datasets[0];
  ds.data = pts;
  ds.pointBackgroundColor = pts.map((p) => (p.i ? C.SERIES.orange : C.SERIES.blue));
  ds.pointBorderColor = ds.pointBackgroundColor;
  ds.pointRadius = pts.map((p) => (p.i ? 3.5 : 2));
  ds.segment = { borderDash: (ctx) => (pts[ctx.p0DataIndex]?.i || pts[ctx.p1DataIndex]?.i ? [4, 3] : undefined),
    borderColor: (ctx) => (pts[ctx.p0DataIndex]?.i || pts[ctx.p1DataIndex]?.i ? C.SERIES.orange : C.SERIES.blue) };
  chGrid.update("none");
  $("grid-note").textContent = `paso ${(g.stepMs / 1000).toFixed(1)} s · ${g.interpolatedCount} interpolados · ${g.gapCount} en hueco`;
}
$("grid-field").onchange = loadGrid;

// ---------- selection
async function select(id) {
  if (st.selected === id) return;
  if (st.selected && hub.state === "Connected") hub.invoke("Unsubscribe", st.selected).catch(() => {});
  st.selected = id;
  store.write("st.dash.device", id);
  const d = st.devices.get(id);
  $("no-device").hidden = true;
  $("device-view").hidden = false;
  $("d-name").textContent = d?.name ?? id;
  $("revoke").hidden = auth.role !== "admin";
  renderDevices();
  // reset state
  st.readings = []; st.track = []; st.distanceM = 0; st.spectrum = null; st.last = null; motWindow.length = 0;
  [chRssi, chRtt, chMot, chBat, chGrid].forEach(C.clear);
  chSpec.data.labels = []; chSpec.data.datasets[0].data = []; chSpec.update();
  routeLayer.setLatLngs([]); pointsLayer.clearLayers(); heatLayer.clearLayers(); marker?.remove(); marker = null;
  if (hub.state === "Connected") await hub.invoke("Subscribe", id).catch((e) => toast(e.message));
  const rows = await api(`/api/readings?deviceId=${id}&limit=600&from=${Date.now() - 15 * 60_000}`);
  rows.forEach((r) => addReading(r, false));
  if (st.track.length) map.fitBounds(routeLayer.getBounds(), { padding: [20, 20], maxZoom: 18 });
  [chRssi, chRtt, chMot, chBat].forEach((c) => c.update("none"));
  renderTiles(); renderAlerts(); loadGrid(); loadHeatmap();
}

// ---------- exports & admin
document.querySelectorAll("[data-export]").forEach((b) => (b.onclick = async () => {
  const res = await api(`/api/export/${b.dataset.export}?deviceId=${st.selected}`, { raw: true });
  if (!res.ok) { toast("No se pudo exportar"); return; }
  const url = URL.createObjectURL(await res.blob());
  const a = Object.assign(document.createElement("a"), { href: url, download: `${st.devices.get(st.selected)?.name ?? "device"}-${b.dataset.export}` });
  a.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}));
$("revoke").onclick = async () => {
  const d = st.devices.get(st.selected);
  if (!confirm(`¿Revocar y borrar "${d?.name}"? Su token deja de servir y se eliminan sus lecturas.`)) return;
  await api(`/api/devices/${st.selected}`, { method: "DELETE" });
  st.devices.delete(st.selected); st.selected = null;
  $("device-view").hidden = true; $("no-device").hidden = false;
  renderDevices();
};

// ---------- QR enrollment
$("qr-btn").onclick = async () => {
  try {
    const e = await api("/api/enroll", { method: "POST" });
    const q = qrcode(0, "M");
    q.addData(e.qr);
    q.make();
    $("qr").innerHTML = q.createSvgTag({ cellSize: 6, margin: 3, scalable: true });
    $("qr-info").innerHTML = `<dt>Código</dt><dd class="mono"><b>${esc(e.code)}</b></dd><dt>Servidor</dt><dd class="mono">${esc(e.url)}</dd>
      <dt>Huella</dt><dd class="mono">${esc(e.fingerprint.slice(0, 32))}…</dd><dt>Expira</dt><dd>${fmtTime(e.expiresAt)}</dd>`;
    $("qr-dialog").showModal();
  } catch (err) { toast(err.message); }
};
$("qr-close").onclick = () => $("qr-dialog").close();

// ---------- SignalR
const hub = new signalR.HubConnectionBuilder()
  .withUrl("/hubs/live", { accessTokenFactory: () => getAuth()?.token ?? "" })
  .withAutomaticReconnect()
  .configureLogging(signalR.LogLevel.Warning)
  .build();
const setLive = (kind, text) => { $("live").className = "badge " + kind; $("live").textContent = text; };
hub.on("DeviceStatus", (id, s) => {
  const d = st.devices.get(id) ?? { id };
  Object.assign(d, { name: s.name, kind: s.kind, online: s.online, lastSeen: s.lastSeen, battery: s.battery, lastRssi: s.lastRssi });
  const isNew = !st.devices.has(id);
  st.devices.set(id, d);
  renderDevices();
  if (id === st.selected) renderTiles();
  if (isNew && !st.selected) select(id);
});
hub.on("Reading", (id, r) => { if (id === st.selected) addReading(r); });
hub.on("Spectrum", (id, freqs, mags, dominant) => { if (id === st.selected) { renderSpectrum(freqs, mags, dominant); renderTiles(); } });
let lastToast = 0;
hub.on("Alert", (a) => {
  st.alerts.unshift(a);
  renderAlerts();
  // Pop-up only for the selected device and at most every 8 s; the alert list shows everything.
  if (a.deviceId === st.selected && Date.now() - lastToast > 8000) {
    lastToast = Date.now();
    toast(`⚠ ${ALERT_LABEL[a.kind] ?? a.kind}: ${a.message}`);
  }
});
hub.onreconnecting(() => setLive("warn", "Reconectando…"));
hub.onreconnected(() => { setLive("ok", "En vivo"); if (st.selected) hub.invoke("Subscribe", st.selected); });
hub.onclose(() => setLive("bad", "Sin conexión en vivo"));

async function connect() {
  try {
    await hub.start();
    setLive("ok", "En vivo");
    if (st.selected) await hub.invoke("Subscribe", st.selected);
  } catch {
    setLive("bad", "Sin conexión en vivo");
    setTimeout(connect, 5000);
  }
}

// ---------- boot
st.alerts = await api("/api/alerts?limit=100").catch(() => []);
renderAlerts();
await connect();
await loadDevices();
setInterval(loadGrid, 15000);
setInterval(loadHeatmap, 30000);
// Online/offline flags come from the server monitor, but the "last seen" text must keep ticking.
setInterval(renderDevices, 5000);
