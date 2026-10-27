// Telemetry agent for the phone browser (PLAN §12.1–12.2):
// register or link the device → confirm/pin the server fingerprint → ECDH handshake → sample the chosen sensors →
// Yael Crypto March1 → AEAD → POST, in series.
// Two ways in: a signed-in user registers this browser, or the QR of the dashboard opens
// agent.html#enroll=CODE&fp=FINGERPRINT and the one-time code links the phone without a user session.
import { api, store, toast, esc, fmtTime, fmtDateTime, getAuth, ApiError } from "./api.js";
import { requireAuth, renderShell } from "./ui/shell.js";
import * as S from "./crypto/session.js";
import { fromB64 } from "./crypto/util.js";
import * as motion from "./collectors/motion.js";
import * as sensors from "./collectors/sensors.js";
import * as battery from "./collectors/battery.js";
import * as netinfo from "./collectors/netinfo.js";
import * as probe from "./collectors/probe.js";
import * as sim from "./collectors/simulator.js";
import * as queue from "./queue.js";

const enroll = Object.fromEntries(new URLSearchParams(location.hash.slice(1)));   // { enroll, fp, tls }
const linked = !!enroll.enroll || (!getAuth() && !!store.read("st.device.enrolled"));
const auth = linked ? getAuth() : requireAuth();
renderShell("agent.html", { minimal: linked && !auth });
const $ = (id) => document.getElementById(id);
const VERSION = "2.0.0";
const deviceKey = linked ? "st.device.enrolled" : "st.device." + auth.username;

/** The sensor bank. web: whether this browser offers it; androidOnly: no web API exists (app or simulator only). */
const sup = sensors.support();
const BANK = [
  { id: "mot", group: "Movimiento", label: "Acelerómetro (RMS, pico y espectro)", web: typeof DeviceMotionEvent !== "undefined" },
  { id: "lin", group: "Movimiento", label: "Aceleración lineal", web: sup.lin },
  { id: "grv", group: "Movimiento", label: "Gravedad", web: sup.grv },
  { id: "gyr", group: "Movimiento", label: "Giroscopio", web: sup.gyr },
  { id: "mfl", group: "Orientación", label: "Magnetómetro", web: sup.mfl },
  { id: "ori", group: "Orientación", label: "Orientación (azimut, cabeceo, alabeo)", web: sup.ori },
  { id: "lux", group: "Entorno", label: "Luz ambiental", web: sup.lux },
  { id: "prx", group: "Entorno", label: "Proximidad", web: false, androidOnly: true },
  { id: "hpa", group: "Entorno", label: "Presión barométrica", web: false, androidOnly: true },
  { id: "tmp", group: "Entorno", label: "Temperatura ambiente", web: false, androidOnly: true },
  { id: "hum", group: "Entorno", label: "Humedad relativa", web: false, androidOnly: true },
  { id: "stp", group: "Entorno", label: "Podómetro (pasos)", web: false, androidOnly: true },
  { id: "bat", group: "Dispositivo", label: "Batería", web: !!navigator.getBattery },
  { id: "net", group: "Señales", label: "Red (tipo y calidad estimada)", web: !!navigator.connection },
  { id: "probe", group: "Señales", label: "Sondeo de enlace (RTT, jitter, pérdida)", web: true },
  { id: "wifi", group: "Señales", label: "Señal Wi-Fi (RSSI)", web: false, androidOnly: true },
  { id: "cell", group: "Señales", label: "Señal celular (dBm)", web: false, androidOnly: true },
];
const ENV = ["lux", "prx", "hpa", "tmp", "hum"];

const st = {
  device: store.read(deviceKey),            // { deviceId, deviceToken, name }
  pin: store.read("st.pin"),                // { publicKey (b64), fingerprint }
  server: null,                             // GET /api/server-key
  session: null,                            // { sid, did, keys, seq }
  running: false,
  sampler: null,
  flushing: false,
  failures: 0,
  retryTimer: null,
  waitingRetry: false,
  wakeLock: null,
  packets: 0,
  stats: { ok: 0, rejected: 0, errors: 0 },
  lastPayload: null,
  lastEnvelope: null,
  status: ["off", "Detenido"],
};

const saved = store.read("st.agent.settings") ?? {};
const settings = { interval: 2, suite: S.SUITE_AES, sim: !matchMedia("(pointer: coarse)").matches, ...saved,
  sensors: Object.fromEntries(BANK.map((b) => [b.id, saved.sensors?.[b.id] ?? true])) };
const saveSettings = () => store.write("st.agent.settings", settings);
const available = (id) => settings.sim || !!BANK.find((b) => b.id === id)?.web;
const exposed = (id) => settings.sensors[id] && available(id);

// ---------- UI
function setStatus(kind, text) {
  st.status = [kind, text];
  $("status").className = "badge " + kind;
  $("status").textContent = text;
}

function log(msg, kind = "") {
  const li = document.createElement("li");
  li.innerHTML = `<span class="muted mono">${fmtTime(Date.now())}</span> <span class="${kind}">${esc(msg)}</span>`;
  $("log").prepend(li);
  while ($("log").children.length > 30) $("log").lastChild.remove();
}

const groupHex = (h) => h.match(/.{1,4}/g).join(" ");

async function renderDevice() {
  const box = $("device");
  if (enroll.enroll && (!st.device || st.device.code !== enroll.enroll)) {
    // Opened from the QR: link with the one-time code (no user session needed).
    const guess = /Android/i.test(navigator.userAgent) ? "Teléfono Android" : /iPhone|iPad/i.test(navigator.userAgent) ? "iPhone" : "Navegador";
    box.innerHTML = `<p>Código de vinculación <b class="mono">${esc(enroll.enroll)}</b> (un solo uso, 10 minutos).</p>
      ${st.device ? `<p class="alert-box small">Este navegador ya está vinculado como <b>${esc(st.device.name)}</b>; al vincular se reemplaza.</p>` : ""}
      <div class="field"><label for="dname">Nombre del dispositivo</label><input id="dname" value="${esc(guess)}" maxlength="60"></div>
      <button class="primary" id="link">Vincular este teléfono</button>
      <p class="muted small" style="margin-top:8px">Antes de canjear el código se comprueba que la huella de la llave del servidor coincida con la del QR.</p>`;
    $("link").onclick = linkWithCode;
  } else if (st.device) {
    box.innerHTML = `<dl class="kv"><dt>Nombre</dt><dd>${esc(st.device.name)}</dd><dt>deviceId</dt><dd class="mono">${esc(st.device.deviceId)}</dd>
      <dt>Tipo</dt><dd>web${linked ? " · vinculado con código QR" : ""}</dd></dl>
      <div class="row" style="margin-top:10px"><button id="forget">${linked ? "Desvincular" : "Olvidar este dispositivo"}</button></div>`;
    $("forget").onclick = () => { if (confirm("Se borrará el token de dispositivo de este navegador.")) { stop(); store.write(deviceKey, null); st.device = null; st.session = null; renderAll(); } };
  } else if (linked) {
    box.innerHTML = `<p>Este navegador no está vinculado. Escanea el QR de «Vincular teléfono» del dashboard o <a href="index.html?next=agent.html">inicia sesión</a>.</p>`;
  } else {
    const guess = /Android/i.test(navigator.userAgent) ? "Teléfono Android" : /iPhone|iPad/i.test(navigator.userAgent) ? "iPhone" : "Navegador de escritorio";
    box.innerHTML = `<div class="field"><label for="dname">Nombre del dispositivo</label><input id="dname" value="${esc(guess)}" maxlength="60"></div>
      <button class="primary" id="register">Registrar este navegador</button>
      <p class="muted small" style="margin-top:8px">El servidor emite un token de dispositivo (JWT de 30 días) que solo autoriza el handshake y el envío de telemetría.</p>`;
    $("register").onclick = async () => {
      try {
        const r = await api("/api/devices", { method: "POST", body: { name: $("dname").value.trim() || guess, kind: "web" } });
        st.device = { deviceId: r.deviceId, deviceToken: r.deviceToken, name: $("dname").value.trim() || guess };
        store.write(deviceKey, st.device);
        log("Dispositivo registrado: " + r.deviceId, "ok");
        renderAll();
      } catch (e) { toast(e.message); }
    };
  }
}

/** QR flow: the fingerprint in the QR must match the key the server presents; then it is pinned and the code redeemed. */
async function linkWithCode() {
  if (!st.server) return toast("Sin conexión con el servidor");
  if (st.server.fingerprint !== enroll.fp) {
    setStatus("bad", "Huella distinta a la del QR");
    log("La huella del servidor no coincide con la del QR: posible intermediario. No se canjea el código.", "bad");
    return;
  }
  const name = $("dname").value.trim() || "Teléfono";
  try {
    const r = await api("/api/enroll/redeem", { method: "POST", body: { code: enroll.enroll, name, kind: "web" } });
    stop();
    st.pin = { publicKey: st.server.publicKey, fingerprint: st.server.fingerprint };
    store.write("st.pin", st.pin);
    st.device = { deviceId: r.deviceId, deviceToken: r.deviceToken, name, code: enroll.enroll };
    store.write(deviceKey, st.device);
    st.session = null;
    history.replaceState(null, "", location.pathname);
    delete enroll.enroll;
    log("Teléfono vinculado y huella fijada desde el QR: " + r.deviceId, "ok");
    renderAll();
  } catch (e) { toast(e.message); log("No se pudo vincular: " + e.message, "bad"); }
}

function renderPin() {
  const box = $("pin");
  if (!st.server) { box.innerHTML = `<p class="muted">Cargando llave del servidor…</p>`; return; }
  const fp = st.server.fingerprint;
  const pinned = st.pin?.fingerprint;
  let html = `<p class="small muted">Huella SHA-256 de la llave ECDSA P-256 de largo plazo del servidor. Compárala con la que el servidor imprime al arrancar:</p>
    <pre class="mono" style="font-size:.95rem">${groupHex(fp)}</pre>`;
  if (pinned && pinned === fp) {
    html += `<p style="margin-top:8px"><span class="badge ok">Huella fijada</span></p>`;
  } else if (pinned && pinned !== fp) {
    html += `<div class="alert-box bad"><b>¡La llave del servidor cambió!</b> La huella fijada era <span class="mono">${groupHex(pinned).slice(0, 39)}…</span>.
      Podría ser un ataque de intermediario. Solo vuelve a fijarla si regeneraste la llave del servidor a propósito.</div>
      <button id="repin" class="danger">Volver a fijar (confío en la nueva llave)</button>`;
  } else if (enroll.fp) {
    html += enroll.fp === fp
      ? `<p style="margin-top:8px"><span class="badge ok">Coincide con la huella del QR</span> Se fija al vincular.</p>`
      : `<div class="alert-box bad"><b>La huella no coincide con la del QR.</b> Posible intermediario: no vincules este teléfono.</div>`;
  } else {
    html += `<label class="check" style="margin:10px 0"><input type="checkbox" id="pinok"> Comparé la huella y coincide (confianza en el primer uso)</label>
      <button class="primary" id="dopin" disabled>Fijar huella</button>`;
  }
  box.innerHTML = html;
  $("pinok")?.addEventListener("change", (e) => ($("dopin").disabled = !e.target.checked));
  const doPin = () => { st.pin = { publicKey: st.server.publicKey, fingerprint: fp }; store.write("st.pin", st.pin); st.session = null; log("Huella fijada", "ok"); renderAll(); };
  $("dopin")?.addEventListener("click", doPin);
  $("repin")?.addEventListener("click", () => confirm("¿Seguro? Esto acepta una llave de servidor distinta.") && doPin());
}

function sensorStatus(b) {
  if (!available(b.id)) return b.androidOnly ? "solo en la app Android" : "no soportado por este navegador";
  if (settings.sim) return st.running ? "simulado" : "disponible (simulado)";
  if (!st.running) return "disponible";
  if (!settings.sensors[b.id]) return "no expuesto";
  if (b.id === "mot") return motion.status();
  if (b.id === "bat") return battery.status();
  if (b.id === "net") return netinfo.status();
  if (b.id === "probe") return probe.status();
  return sensors.status(b.id === "lux" ? "lux" : b.id);
}

function renderSensors() {
  const rows = BANK.map((b, i) => {
    const head = i === 0 || BANK[i - 1].group !== b.group ? `<tr class="group"><th colspan="3">${b.group}</th></tr>` : "";
    return `${head}<tr class="${available(b.id) ? "" : "muted"}">
      <td><input type="checkbox" data-sensor="${b.id}" id="sn-${b.id}" ${exposed(b.id) ? "checked" : ""} ${available(b.id) && !st.running ? "" : "disabled"}></td>
      <td><label for="sn-${b.id}">${b.label}</label></td><td class="small">${esc(sensorStatus(b))}</td></tr>`;
  }).join("");
  $("sensors").innerHTML = `<thead><tr><th scope="col"><span class="sr-only">Exponer</span></th><th scope="col">Sensor</th><th scope="col">Estado</th></tr></thead><tbody>${rows}</tbody>`;
  $("sensors-count").textContent = `${BANK.filter((b) => exposed(b.id)).length} de ${BANK.length} expuestos`;
  $("sensors-lock").hidden = !st.running;
  $("sensors").querySelectorAll("input[data-sensor]").forEach((el) => {
    el.onchange = () => { settings.sensors[el.dataset.sensor] = el.checked; saveSettings(); renderSensors(); };
  });
}

function renderControls() {
  const ready = !!st.device && st.pin?.fingerprint === st.server?.fingerprint;
  $("start").disabled = !ready;
  $("start").textContent = st.running ? "Detener" : "Iniciar";
  $("start").className = st.running ? "danger" : "primary";
  $("ready-hint").hidden = ready;
  $("src-sim").checked = !!settings.sim;
  $("src-sim").disabled = st.running;
  $("all-on").disabled = $("all-off").disabled = st.running;
  $("interval").value = settings.interval;
  $("interval-v").textContent = settings.interval + " s";
  $("suite").value = settings.suite;
  $("suite").querySelector(`option[value="${S.SUITE_CHACHA}"]`).disabled = !st.server?.suites?.includes(S.SUITE_CHACHA);
  renderSensors();
}

async function renderLive() {
  $("s-ok").textContent = st.stats.ok;
  $("s-rej").textContent = st.stats.rejected;
  $("s-err").textContent = st.stats.errors;
  $("s-queue").textContent = await queue.size();
  $("sess").innerHTML = st.session
    ? `<dt>Sesión</dt><dd class="mono">${st.session.sid}</dd><dt>Suite</dt><dd>${st.session.keys.suite}</dd><dt>Último seq</dt><dd>${st.session.seq}</dd><dt>Expira</dt><dd>${fmtDateTime(st.session.expiresAt)}</dd>`
    : `<dt>Sesión</dt><dd class="muted">sin handshake</dd>`;
  if (st.running) renderSensors();
  if (st.lastPayload) $("payload").textContent = JSON.stringify(st.lastPayload, null, 2);
  if (st.lastEnvelope) $("wire").textContent = JSON.stringify(st.lastEnvelope, null, 2);
}

function renderAll() { renderDevice(); renderPin(); renderControls(); renderLive(); }

// ---------- protocol
async function handshake() {
  setStatus("info", "Handshake…");
  const { state, request } = await S.beginHandshake(st.device.deviceId, settings.suite);
  const resp = await api("/api/handshake", { method: "POST", body: request, token: st.device.deviceToken });
  try {
    const s = await S.completeHandshake(state, resp, fromB64(st.pin.publicKey));
    st.session = { ...s, seq: 0 };
    log(`Handshake OK · sesión ${s.sid} · ${s.keys.suite}`, "ok");
  } catch (e) {
    stop();
    setStatus("bad", "Firma del servidor inválida");
    log(e.message, "bad");
    throw e;
  }
}

async function send(reading) {
  if (!st.session) await handshake();
  const seq = ++st.session.seq;
  const env = await S.envelope(st.session, seq, reading.t, reading);
  const res = await api("/api/telemetry", { method: "POST", body: JSON.stringify(env), token: st.device.deviceToken, raw: true });
  st.lastEnvelope = env;
  if (res.status === 202) return "ok";
  const err = await res.json().catch(() => ({}));
  return err.code || "http_" + res.status;
}

/** Sends the queue in series. While a retry is pending, new samples only enqueue (force = retry timer / online). */
async function flush(force = false) {
  if (st.flushing || !st.device || (st.waitingRetry && !force)) return;
  st.waitingRetry = false;
  st.flushing = true;
  try {
    for (;;) {
      const [item] = await queue.peek(1);
      if (!item) break;
      let code;
      try { code = await send(item.reading); }
      catch (e) {
        if (e instanceof ApiError && e.status === 401) code = "unauthorized";
        else if (e instanceof ApiError && e.status && e.status !== 429) { code = e.code; }
        else { scheduleRetry(); return; }
      }
      if (code === "ok") {
        await queue.remove(item.id);
        st.stats.ok++; st.failures = 0;
        if (st.running) setStatus(document.visibilityState === "visible" ? "ok" : "warn", document.visibilityState === "visible" ? "Enviando" : "En pausa (pestaña oculta)");
      } else if (code === "unknown_session") {
        log("Sesión desconocida en el servidor (¿reinició?): nuevo handshake", "warn");
        st.session = null;
      } else if (code === "replay") {
        log("seq repetido; se reintenta con el siguiente", "warn");
      } else if (code === "unauthorized") {
        stop(); setStatus("bad", "Token de dispositivo inválido o revocado"); log("401: registra de nuevo el dispositivo", "bad"); return;
      } else if (code === "rate_limited") {
        scheduleRetry(); return;
      } else {
        await queue.remove(item.id);
        st.stats.rejected++;
        log(`Paquete rechazado: ${code}`, "bad");
      }
    }
  } finally {
    st.flushing = false;
    renderLive();
  }
}

function scheduleRetry() {
  st.stats.errors++;
  const delay = Math.min(30000, 1000 * 2 ** st.failures++);
  queue.size().then((n) => setStatus("warn", `Sin conexión (${n} en cola) · reintento en ${Math.round(delay / 1000)} s`));
  clearTimeout(st.retryTimer);
  st.waitingRetry = true;
  st.retryTimer = setTimeout(() => flush(true), delay);
}

// ---------- sampling
function sample() {
  if (document.visibilityState !== "visible") { setStatus("warn", "En pausa (pantalla bloqueada / pestaña oculta)"); return; }
  st.packets++;
  const r = { t: Date.now(), src: settings.sim ? "sim" : "web" };
  const src = settings.sim ? sim.step(settings.interval) : sensors.read();
  if (exposed("mot")) { const m = motion.read(st.packets % 5 === 0); if (m) r.mot = m; }
  for (const k of ["lin", "grv", "gyr", "mfl", "ori"]) if (exposed(k) && src[k]) r[k] = src[k];
  const env = Object.fromEntries(ENV.filter((k) => exposed(k) && src.env?.[k] != null).map((k) => [k, src.env[k]]));
  if (Object.keys(env).length) r.env = env;
  if (exposed("stp") && src.stp) r.stp = src.stp;
  if (exposed("bat")) { const b = settings.sim ? src.bat : battery.read(); if (b) r.bat = b; }
  const pr = exposed("probe") ? probe.read() : undefined;
  if (exposed("net")) { const n = netinfo.read(pr); if (n) r.net = n; }
  else if (pr) r.net = { probe: pr };
  if (exposed("wifi") && src.wifi) r.wifi = src.wifi;
  if (exposed("cell") && src.cell) r.cell = src.cell;
  r.app = { vis: true, ver: VERSION };
  st.lastPayload = r;
  queue.push(r).then(flush);
}

async function acquireWakeLock() {
  try { if (st.running && navigator.wakeLock && document.visibilityState === "visible") st.wakeLock = await navigator.wakeLock.request("screen"); }
  catch { /* not granted: the screen may turn off, which pauses telemetry */ }
}

async function start() {
  st.running = true;
  setStatus("info", "Iniciando…");
  if (settings.sim) sim.start();
  else {
    // Permission prompts must run inside the click that starts the agent (iOS).
    if (exposed("mot")) await motion.start();
    await sensors.start(Object.fromEntries(["lin", "grv", "gyr", "mfl", "ori", "lux"].map((k) => [k, exposed(k)])));
    if (exposed("bat")) await battery.start();
  }
  if (exposed("probe")) probe.start();
  await acquireWakeLock();
  st.sampler = setInterval(sample, settings.interval * 1000);
  sample();
  log(`Telemetría iniciada (${settings.sim ? "simulador" : "sensores reales"}, ${BANK.filter((b) => exposed(b.id)).length} sensores, cada ${settings.interval} s)`);
  renderControls();
}

function stop() {
  st.running = false;
  clearInterval(st.sampler); clearTimeout(st.retryTimer);
  sim.stop(); motion.stop(); sensors.stop(); probe.stop();
  st.wakeLock?.release?.().catch(() => {}); st.wakeLock = null;
  setStatus("off", "Detenido");
  renderControls();
}

document.addEventListener("visibilitychange", () => {
  if (!st.running) return;
  if (document.visibilityState === "visible") {
    setStatus("ok", "Enviando");
    log("Pestaña visible: se reanuda la telemetría");
    acquireWakeLock();
    flush(true);
  } else {
    setStatus("warn", "En pausa (pantalla bloqueada / pestaña oculta)");
    log("Pestaña oculta: el navegador detiene los sensores", "warn");
  }
});

addEventListener("online", () => { log("Conexión restablecida: se vacía la cola"); flush(true); });

// ---------- controls
$("start").onclick = () => (st.running ? stop() : start());
$("src-sim").onchange = (e) => { settings.sim = e.target.checked; saveSettings(); renderControls(); };
for (const [id, on] of [["all-on", true], ["all-off", false]]) {
  $(id).onclick = () => { for (const b of BANK) if (available(b.id)) settings.sensors[b.id] = on; saveSettings(); renderSensors(); };
}
$("interval").oninput = (e) => { settings.interval = Number(e.target.value); $("interval-v").textContent = settings.interval + " s"; };
$("interval").onchange = () => { saveSettings(); if (st.running) { stop(); start(); } };
$("suite").onchange = (e) => { settings.suite = e.target.value; saveSettings(); st.session = null; log("Suite cambiada: el próximo envío hace un handshake nuevo"); };
$("clearq").onclick = async () => { await queue.clear(); renderLive(); };

setInterval(renderLive, 2000);
try { st.server = await api("/api/server-key"); } catch (e) { toast(e.message); }
renderAll();
if (!isSecureContext) log("Contexto no seguro: Web Crypto y los sensores requieren HTTPS o localhost (usa el QR de vinculación)", "bad");
log(`Fingerprint del servidor: ${st.server ? st.server.fingerprint.slice(0, 16) : "?"}…`);
