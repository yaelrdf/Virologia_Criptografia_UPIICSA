// Telemetry agent for the phone browser (PLAN §12.1–12.2):
// register device → confirm/pin the server fingerprint → ECDH handshake → sample → SigMix → AEAD → POST, in series.
import { api, store, toast, esc, fmtTime, fmtDateTime, ApiError } from "./api.js";
import { requireAuth, renderShell } from "./ui/shell.js";
import * as S from "./crypto/session.js";
import { fromB64 } from "./crypto/util.js";
import * as geo from "./collectors/geo.js";
import * as motion from "./collectors/motion.js";
import * as battery from "./collectors/battery.js";
import * as netinfo from "./collectors/netinfo.js";
import * as probe from "./collectors/probe.js";
import * as sim from "./collectors/simulator.js";
import * as queue from "./queue.js";

const auth = requireAuth();
renderShell("agent.html");
const $ = (id) => document.getElementById(id);
const VERSION = "1.0.0";
const deviceKey = "st.device." + auth.username;

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

const settings = Object.assign({ interval: 2, suite: S.SUITE_AES, sim: !matchMedia("(pointer: coarse)").matches, gps: true, mot: true, bat: true, net: true, probe: true },
  store.read("st.agent.settings") ?? {});

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
  if (st.device) {
    box.innerHTML = `<dl class="kv"><dt>Nombre</dt><dd>${esc(st.device.name)}</dd><dt>deviceId</dt><dd class="mono">${esc(st.device.deviceId)}</dd><dt>Tipo</dt><dd>web</dd></dl>
      <div class="row" style="margin-top:10px"><button id="forget">Olvidar este dispositivo</button></div>`;
    $("forget").onclick = () => { if (confirm("Se borrará el token de dispositivo de este navegador.")) { stop(); store.write(deviceKey, null); st.device = null; st.session = null; renderAll(); } };
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

function renderControls() {
  const ready = !!st.device && st.pin?.fingerprint === st.server?.fingerprint;
  $("start").disabled = !ready;
  $("start").textContent = st.running ? "Detener" : "Iniciar";
  $("start").className = st.running ? "danger" : "primary";
  $("ready-hint").hidden = ready;
  for (const k of ["gps", "mot", "bat", "net", "probe", "sim"]) $("src-" + k).checked = !!settings[k];
  $("interval").value = settings.interval;
  $("interval-v").textContent = settings.interval + " s";
  $("suite").value = settings.suite;
  $("suite").querySelector(`option[value="${S.SUITE_CHACHA}"]`).disabled = !st.server?.suites?.includes(S.SUITE_CHACHA);
}

async function renderLive() {
  $("s-ok").textContent = st.stats.ok;
  $("s-rej").textContent = st.stats.rejected;
  $("s-err").textContent = st.stats.errors;
  $("s-queue").textContent = await queue.size();
  $("sess").innerHTML = st.session
    ? `<dt>Sesión</dt><dd class="mono">${st.session.sid}</dd><dt>Suite</dt><dd>${st.session.keys.suite}</dd><dt>Último seq</dt><dd>${st.session.seq}</dd><dt>Expira</dt><dd>${fmtDateTime(st.session.expiresAt)}</dd>`
    : `<dt>Sesión</dt><dd class="muted">sin handshake</dd>`;
  $("sources").innerHTML = [
    ["GPS", settings.sim ? "simulado" : geo.status()], ["Movimiento", settings.sim ? "simulado (vibración de 5 Hz)" : motion.status()], ["Batería", settings.sim ? "simulada" : battery.status()],
    ["Red", netinfo.status()], ["Sondeo", probe.status()], ["Wi-Fi / celular", settings.sim ? "RSSI sintético" : "no disponible en web"],
  ].map(([k, v]) => `<dt>${k}</dt><dd>${esc(v)}</dd>`).join("");
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
  const simData = settings.sim ? sim.step(settings.interval) : null;
  if (settings.gps) { const g = simData ? simData.gps : geo.read(); if (g) r.gps = g; }
  if (settings.mot) { const m = motion.read(st.packets % 5 === 0); if (m) r.mot = m; }
  if (settings.bat) { const b = simData ? simData.bat : battery.read(); if (b) r.bat = b; }
  if (settings.net) { const n = netinfo.read(settings.probe ? probe.read() : undefined); if (n) r.net = n; }
  if (simData) r.wifi = simData.wifi;
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
    if (settings.gps) geo.start();
    if (settings.mot) await motion.start(); // must run inside the click (iOS permission)
    if (settings.bat) await battery.start();
  }
  if (settings.probe) probe.start();
  await acquireWakeLock();
  st.sampler = setInterval(sample, settings.interval * 1000);
  sample();
  log(`Telemetría iniciada (${settings.sim ? "simulador" : "sensores reales"}, cada ${settings.interval} s)`);
  renderControls();
}

function stop() {
  st.running = false;
  clearInterval(st.sampler); clearTimeout(st.retryTimer);
  sim.stop(); geo.stop(); motion.stop(); probe.stop();
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
    log("Pestaña oculta: el navegador detiene sensores y GPS", "warn");
  }
});

addEventListener("online", () => { log("Conexión restablecida: se vacía la cola"); flush(true); });

// ---------- controls
$("start").onclick = () => (st.running ? stop() : start());
for (const k of ["gps", "mot", "bat", "net", "probe", "sim"]) {
  $("src-" + k).onchange = (e) => { settings[k] = e.target.checked; store.write("st.agent.settings", settings); if (st.running) { stop(); start(); } renderLive(); };
}
$("interval").oninput = (e) => { settings.interval = Number(e.target.value); $("interval-v").textContent = settings.interval + " s"; };
$("interval").onchange = () => { store.write("st.agent.settings", settings); if (st.running) { stop(); start(); } };
$("suite").onchange = (e) => { settings.suite = e.target.value; store.write("st.agent.settings", settings); st.session = null; log("Suite cambiada: el próximo envío hace un handshake nuevo"); };
$("clearq").onclick = async () => { await queue.clear(); renderLive(); };

setInterval(renderLive, 2000);
try { st.server = await api("/api/server-key"); } catch (e) { toast(e.message); }
renderAll();
if (!isSecureContext) log("Contexto no seguro: Web Crypto, GPS y sensores requieren HTTPS o localhost", "bad");
log(`Fingerprint del servidor: ${st.server ? st.server.fingerprint.slice(0, 16) : "?"}…`);
