// Dashboard (PLAN §11.1): live device list; for the selected device the instantaneous value of every exposed sensor
// (updated by SignalR) and its history (15 min to 24 h) with SMA/EMA, z-score marks, DFT spectrum, battery trend
// and the interpolated time grid; CSV export and QR linking.
import { api, getAuth, store, toast, esc, fmtTime } from "./api.js";
import { requireAuth, renderShell } from "./ui/shell.js";
import * as C from "./ui/charts.js";
import qrcode from "../lib/qrcode/qrcode.mjs";

const auth = requireAuth();
renderShell("app.html");
const $ = (id) => document.getElementById(id);

const MAX_POINTS = 1500;
const st = {
  devices: new Map(),       // id -> device
  selected: null,
  range: store.read("st.dash.range") ?? 15,   // minutes of history
  readings: 0,
  loading: false,           // while the history loads, live readings wait in `pending` (charts need sorted x)
  pending: [],
  spectrum: null,
  last: null,
};

function smaStep(arr, w) { const s = arr.slice(-w); return s.reduce((a, b) => a + b, 0) / s.length; }

// ---------- charts
const opt = (o = {}) => ({ max: MAX_POINTS, ...o });
const Z = { label: "|z| > 3", color: C.SERIES.red, marks: true };
const chRssi = C.timeChart($("c-rssi"), [{ label: "RSSI", color: C.SERIES.blue, points: 1.2 }, { label: "EMA", color: C.SERIES.orange, width: 2.2 }, Z], opt({ yLabel: "dBm" }));
const chRtt = C.timeChart($("c-rtt"), [{ label: "RTT (ms)", color: C.SERIES.purple }, { label: "EMA RTT", color: C.SERIES.orange, width: 2.2 }, { label: "Pérdida (%)", color: C.SERIES.red, dashed: true }, Z], opt({ yLabel: "ms / %" }));
const chMot = C.timeChart($("c-mot"), [{ label: "RMS", color: C.SERIES.teal, points: 1 }, { label: "SMA(10)", color: C.SERIES.purple, dashed: true }, { label: "EMA", color: C.SERIES.orange, width: 2.2 }, Z], opt({ yLabel: "m/s²" }));
const chGyr = C.timeChart($("c-gyr"), [{ label: "|ω|", color: C.SERIES.purple, fill: true }], opt({ yLabel: "rad/s", legend: false }));
const chOri = C.timeChart($("c-ori"), [{ label: "Azimut", color: C.SERIES.blue }, { label: "Cabeceo", color: C.SERIES.orange }, { label: "Alabeo", color: C.SERIES.green }], opt({ yLabel: "°" }));
const chLux = C.timeChart($("c-lux"), [{ label: "Luz", color: C.SERIES.orange, fill: true }], opt({ yLabel: "lx", legend: false }));
const chHpa = C.timeChart($("c-hpa"), [{ label: "Presión", color: C.SERIES.teal }], opt({ yLabel: "hPa", legend: false }));
const chBat = C.timeChart($("c-bat"), [{ label: "Batería (%)", color: C.SERIES.green, fill: true }, { label: "Proyección (regresión)", color: C.SERIES.red, dashed: true }], opt({ yLabel: "%", yMin: 0, yMax: 100 }));
const chSpec = C.xyChart($("c-spec"), "|X(f)|", C.SERIES.teal, { xLabel: "Hz", yLabel: "|X_k| / N" });
const chGrid = C.timeChart($("c-grid"), [{ label: "Malla regular", color: C.SERIES.blue, points: 2.5 }], { legend: false, max: 5000 });
const LIVE_CHARTS = [chRssi, chRtt, chMot, chGyr, chOri, chLux, chHpa, chBat];
const motWindow = [];

let pending = false;
function redraw() {
  if (pending) return;
  pending = true;
  requestAnimationFrame(() => { pending = false; LIVE_CHARTS.forEach((c) => c.update("none")); });
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

// ---------- readings → charts
function addReading(r, live = true) {
  st.readings++;
  st.last = r;
  const t = r.ts, z = r.z ?? {};
  const mark = (chart, i, key, y) => { if (Math.abs(z[key] ?? 0) > 3) C.push(chart, i, t, y); };
  const sig = r.wifiRssi ?? r.cellDbm;
  if (sig != null) {
    C.push(chRssi, 0, t, sig);
    const prev = chRssi.data.datasets[1].data.at(-1)?.y;
    C.push(chRssi, 1, t, r.rssiEma ?? (prev == null ? sig : 0.3 * sig + 0.7 * prev));
    mark(chRssi, 2, "rssi", sig);
  }
  if (r.probeRtt != null) {
    C.push(chRtt, 0, t, r.probeRtt);
    const prev = chRtt.data.datasets[1].data.at(-1)?.y;
    C.push(chRtt, 1, t, r.rttEma ?? (prev == null ? r.probeRtt : 0.3 * r.probeRtt + 0.7 * prev));
    mark(chRtt, 3, "rtt", r.probeRtt);
  }
  if (r.probeLoss != null) C.push(chRtt, 2, t, r.probeLoss * 100);
  if (r.accRms != null) {
    motWindow.push(r.accRms);
    if (motWindow.length > 10) motWindow.shift();
    C.push(chMot, 0, t, r.accRms);
    C.push(chMot, 1, t, smaStep(motWindow, 10));
    const prev = chMot.data.datasets[2].data.at(-1)?.y;
    C.push(chMot, 2, t, r.rmsEma ?? (prev == null ? r.accRms : 0.3 * r.accRms + 0.7 * prev));
    mark(chMot, 3, "rms", r.accRms);
  }
  if (r.gyrNorm != null) C.push(chGyr, 0, t, r.gyrNorm);
  if (r.p?.ori) {
    // Azimuth wraps around 0°/360°: break the line instead of drawing a vertical jump across the chart.
    const prevAz = chOri.data.datasets[0].data.at(-1)?.y;
    if (prevAz != null && Math.abs(r.p.ori.az - prevAz) > 180) chOri.data.datasets[0].data.push({ x: t - 1, y: null });
    C.push(chOri, 0, t, r.p.ori.az); C.push(chOri, 1, t, r.p.ori.pi); C.push(chOri, 2, t, r.p.ori.ro);
  }
  if (r.lux != null) C.push(chLux, 0, t, r.lux);
  if (r.hpa != null) C.push(chHpa, 0, t, r.hpa);
  if (r.batLvl != null) {
    C.push(chBat, 0, t, r.batLvl * 100);
    if (r.batSlopeH != null) projectBattery(t, r.batLvl, r.batSlopeH);
  }
  if (live) {
    redraw();
    renderLive();
    const card = $("live-card");
    card.classList.add("pulse");
    setTimeout(() => card.classList.remove("pulse"), 350);
  }
}

/** Least-squares slope from the server (%/h) projected forward until 0 % or 15 min. */
function projectBattery(t, lvl, slopePerHour) {
  const ds = chBat.data.datasets[1];
  if (slopePerHour >= -1e-4) { ds.data = []; $("bat-note").textContent = "estable o cargando"; return; }
  const hours = Math.min(0.25, lvl / -slopePerHour);
  ds.data = [{ x: t, y: lvl * 100 }, { x: t + hours * 3_600_000, y: Math.max(0, (lvl + slopePerHour * hours) * 100) }];
  $("bat-note").textContent = `pendiente ${(slopePerHour * 100).toFixed(1)} %/h`;
}

// ---------- live panel: every value of the last reading, grouped
const num = (v, d = 1) => (v == null || Number.isNaN(v) ? null : Number(v).toFixed(d));
const vec = (v, d = 2) => (v ? `${num(v.x, d)}, ${num(v.y, d)}, ${num(v.z, d)}` : null);
const norm = (v) => (v ? Math.hypot(v.x, v.y, v.z) : null);
const CARDINAL = ["N", "NE", "E", "SE", "S", "SO", "O", "NO"];
const cardinal = (deg) => CARDINAL[Math.round((((deg % 360) + 360) % 360) / 45) % 8];
/** Barometric altitude (international standard atmosphere): h = 44330 · (1 − (p / 1013.25)^0.1903). */
const altitude = (hpa) => 44330 * (1 - (hpa / 1013.25) ** 0.1903);

function renderLive() {
  const r = st.last, d = st.devices.get(st.selected);
  if (!d) return;
  $("d-meta").innerHTML = `${esc(d.kind)} · <span class="badge ${d.online ? "ok" : "off"}">${d.online ? "en línea" : "fuera de línea"}</span> · id <span class="mono">${d.id.slice(0, 8)}…</span>${d.intervalMs ? ` · intervalo ≈ ${(d.intervalMs / 1000).toFixed(1)} s` : ""}`;
  if (!r) { $("live-panel").innerHTML = `<p class="muted">Sin lecturas en el rango elegido.</p>`; return; }
  const p = r.p ?? {}, z = r.z ?? {};
  const eta = r.batEtaMin != null ? (r.batEtaMin > 120 ? `${(r.batEtaMin / 60).toFixed(1)} h` : `${Math.round(r.batEtaMin)} min`) : null;
  const groups = [
    ["Movimiento", [
      [num(p.mot?.rms, 3), "acelerómetro · RMS m/s²", z.rms], [num(p.mot?.peak, 3), "acelerómetro · pico m/s²"],
      [vec(p.mot && { x: p.mot.ax, y: p.mot.ay, z: p.mot.az }), "a (x, y, z) m/s²"], [vec(p.lin), "aceleración lineal m/s²"],
      [vec(p.grv), "gravedad m/s²"], [vec(p.gyr, 3), "giroscopio (x, y, z) rad/s"], [num(norm(p.gyr), 3), "|ω| rad/s"],
    ]],
    ["Orientación", [
      [p.ori ? `${num(p.ori.az)}° ${cardinal(p.ori.az)}` : null, "azimut"], [p.ori ? `${num(p.ori.pi)}°` : null, "cabeceo"],
      [p.ori ? `${num(p.ori.ro)}°` : null, "alabeo"], [vec(p.mfl, 1), "campo magnético µT"], [num(norm(p.mfl)), "|B| µT"],
    ]],
    ["Entorno", [
      [num(p.env?.lux, 0), "luz ambiental lx"], [num(p.env?.prx, 0), "proximidad cm"], [num(p.env?.hpa, 2), "presión hPa"],
      [p.env?.hpa != null ? `${Math.round(altitude(p.env.hpa))} m` : null, "altitud barométrica"],
      [num(p.env?.tmp), "temperatura °C"], [num(p.env?.hum), "humedad %"], [p.stp?.n, "pasos"],
    ]],
    ["Batería", [
      [p.bat ? `${Math.round(p.bat.lvl * 100)} %` : null, p.bat?.chg ? "nivel · cargando" : "nivel"], [eta, "tiempo restante (regresión)"],
      [num(p.bat?.temp), "temperatura °C"], [num(p.bat?.volt, 2), "voltaje V"], [p.bat?.health, "salud"], [p.bat?.plug, "alimentación"],
    ]],
    ["Red y señales", [
      [p.wifi?.rssi != null ? `${p.wifi.rssi} dBm` : null, "Wi-Fi · RSSI", z.rssi], [p.wifi?.freq != null ? `${p.wifi.freq} MHz` : null, "Wi-Fi · frecuencia"],
      [p.wifi?.link != null ? `${p.wifi.link} Mbps` : null, "Wi-Fi · enlace"],
      [p.cell?.dbm != null ? `${p.cell.dbm} dBm` : null, `celular${p.cell?.type ? " · " + p.cell.type.toUpperCase() : ""}`, p.wifi ? undefined : z.rssi],
      [p.cell?.level != null ? `${p.cell.level}/4` : null, "celular · barras"],
      [num(p.net?.probe?.rtt), "RTT ms (sondeo)", z.rtt], [num(p.net?.probe?.jit), "jitter ms"],
      [p.net?.probe?.loss != null ? `${Math.round(p.net.probe.loss * 100)} %` : null, "pérdida"], [num(p.net?.probe?.kbps, 0), "descarga kbps"],
      [p.net?.type ?? p.net?.eff, "tipo de red"],
    ]],
    ["Analítica", [
      [st.spectrum != null ? `${st.spectrum.toFixed(2)} Hz` : null, "frecuencia dominante (DFT)"],
      [r.rssiSlopeMin != null ? `${r.rssiSlopeMin >= 0 ? "+" : ""}${r.rssiSlopeMin.toFixed(2)}` : null, "tendencia señal dBm/min"],
      [num(r.rssiEma), "EMA señal dBm"], [num(r.rttEma), "EMA RTT ms"], [st.readings, "lecturas en el historial"],
      [r.seq, "último seq"],
    ]],
  ];
  $("live-panel").innerHTML = groups.map(([title, vals]) => {
    const shown = vals.filter(([v]) => v != null && v !== "");
    if (!shown.length) return "";
    return `<div class="live-group"><h4>${title}</h4><div class="vals">${shown.map(([v, label, zs]) =>
      `<div class="stat ${Math.abs(zs ?? 0) > 3 ? "z" : ""}"><b>${esc(v)}</b><span>${esc(label)}${zs != null ? ` · z = ${zs.toFixed(1)}` : ""}</span></div>`).join("")}</div></div>`;
  }).join("");
  renderAge();
}

function renderAge() {
  if (!st.last) { $("live-age").textContent = ""; return; }
  const s = Math.max(0, Math.round((Date.now() - st.last.ts) / 1000));
  $("live-age").textContent = `· lectura de las ${fmtTime(st.last.ts)} (hace ${s < 90 ? s + " s" : Math.round(s / 60) + " min"})`;
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
  const g = await api(`/api/readings/grid?deviceId=${st.selected}&field=${field}&from=${Date.now() - Math.min(st.range, 60) * 60_000}`);
  const scale = field === "battery" || field === "loss" ? 100 : 1;
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

// ---------- history
function renderRange() {
  $("range").querySelectorAll("button").forEach((b) => b.setAttribute("aria-pressed", String(Number(b.dataset.min) === st.range)));
}
$("range").addEventListener("click", (e) => {
  const b = e.target.closest("button[data-min]");
  if (!b) return;
  st.range = Number(b.dataset.min);
  store.write("st.dash.range", st.range);
  renderRange();
  loadHistory();
});

async function loadHistory() {
  const id = st.selected;
  st.loading = true; st.pending = [];
  st.readings = 0; st.last = null; motWindow.length = 0;
  [...LIVE_CHARTS, chGrid].forEach(C.clear);
  const from = Date.now() - st.range * 60_000;
  const rows = await api(`/api/readings?deviceId=${id}&from=${from}&limit=100000&maxPoints=${MAX_POINTS}`).finally(() => { st.loading = false; });
  if (id !== st.selected) return;
  rows.forEach((r) => addReading(r, false));
  const lastTs = rows.at(-1)?.ts ?? 0;
  st.pending.filter((r) => r.ts > lastTs).forEach((r) => addReading(r, false));
  st.pending = [];
  LIVE_CHARTS.forEach((c) => c.update("none"));
  const label = st.range < 60 ? `${st.range} min` : `${st.range / 60} h`;
  $("history-note").textContent = rows.length
    ? `Últimas ${label}: ${rows.length} lecturas${rows.length >= MAX_POINTS ? " (muestreadas: una de cada k para no saturar las gráficas)" : ""}. Las nuevas se agregan en vivo.`
    : `Sin lecturas en las últimas ${label}.`;
  renderLive();
  loadGrid();
}

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
  st.spectrum = null;
  chSpec.data.labels = []; chSpec.data.datasets[0].data = []; chSpec.update();
  if (hub.state === "Connected") await hub.invoke("Subscribe", id).catch((e) => toast(e.message));
  await loadHistory();
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

// ---------- QR linking: an ordinary URL (phone camera → web agent; the Android app reads the same link)
$("qr-btn").onclick = async () => {
  try {
    const e = await api("/api/enroll", { method: "POST" });
    const q = qrcode(0, "M");
    q.addData(e.qr);
    q.make();
    $("qr").innerHTML = q.createSvgTag({ cellSize: 6, margin: 3, scalable: true });
    $("qr-link").textContent = e.qr;
    $("qr-info").innerHTML = `<dt>Código</dt><dd class="mono"><b>${esc(e.code)}</b></dd><dt>Servidor</dt><dd class="mono">${esc(e.url)}</dd>
      <dt>Huella</dt><dd class="mono">${esc(e.fingerprint.slice(0, 32))}…</dd>${e.tls ? `<dt>Certificado TLS</dt><dd class="mono">${esc(e.tls.slice(0, 32))}… (autofirmado: el navegador avisará una vez)</dd>` : ""}
      <dt>Expira</dt><dd>${fmtTime(e.expiresAt)}</dd>`;
    $("qr-dialog").showModal();
  } catch (err) { toast(err.message); }
};
$("qr-copy").onclick = async () => {
  try { await navigator.clipboard.writeText($("qr-link").textContent); toast("Enlace copiado"); } catch { toast("Selecciona el enlace y cópialo"); }
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
  if (isNew && !st.selected) select(id);
});
hub.on("Reading", (id, r) => {
  if (id !== st.selected) return;
  if (st.loading) st.pending.push(r); else addReading(r);
});
hub.on("Spectrum", (id, freqs, mags, dominant) => { if (id === st.selected) renderSpectrum(freqs, mags, dominant); });
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
renderRange();
await connect();
await loadDevices();
setInterval(loadGrid, 15000);
// Online/offline flags come from the server monitor, but the "last seen" texts must keep ticking.
setInterval(renderDevices, 5000);
setInterval(renderAge, 1000);
