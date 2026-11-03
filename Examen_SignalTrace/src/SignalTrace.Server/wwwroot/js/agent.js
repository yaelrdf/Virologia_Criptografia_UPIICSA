// Agente de telemetría para el navegador del teléfono (PLAN §12.1–12.2):
// registrar o vincular el dispositivo → confirmar y fijar la huella del servidor → handshake ECDH → muestrear los
// sensores elegidos → Yael Crypto March1 → AEAD → POST, un paquete después de otro.
// Hay dos formas de entrar: un usuario con sesión registra este navegador, o el QR del dashboard abre
// agent.html#enroll=CODIGO&fp=HUELLA y el código de un solo uso vincula el teléfono sin sesión de usuario.
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

/** Parámetros del fragmento de la URL que pone el QR. */
const enroll = Object.fromEntries(new URLSearchParams(location.hash.slice(1)));   // { enroll, fp, tls }
/** Modo vinculado por QR: llegó un código, o no hay sesión pero ya hay un dispositivo vinculado guardado. */
const linked = !!enroll.enroll || (!getAuth() && !!store.read("st.device.enrolled"));
const auth = linked ? getAuth() : requireAuth();
renderShell("agent.html", { minimal: linked && !auth });
/** Atajo para document.getElementById. */
const $ = (id) => document.getElementById(id);
/** Versión del agente que viaja en app.ver. */
const VERSION = "2.0.0";
/** Llave de localStorage del dispositivo: una por usuario, o una compartida para el modo vinculado. */
const deviceKey = linked ? "st.device.enrolled" : "st.device." + auth.username;

/** El banco de sensores. web: si este navegador lo ofrece; androidOnly: no existe API web (solo la app o el simulador). */
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
/** Sensores que van dentro del bloque env. */
const ENV = ["lux", "prx", "hpa", "tmp", "hum"];

/** Estado del agente. */
const st = {
  device: store.read(deviceKey),            // { deviceId, deviceToken, name }
  pin: store.read("st.pin"),                // { publicKey (b64), fingerprint }
  server: null,                             // respuesta de GET /api/server-key
  session: null,                            // { sid, did, keys, seq }
  running: false,
  sampler: null,                            // temporizador del muestreo
  flushing: false,                          // si se está vaciando la cola
  failures: 0,                              // fallos seguidos, para el retroceso exponencial
  retryTimer: null,
  waitingRetry: false,
  wakeLock: null,                           // bloqueo para que la pantalla no se apague
  packets: 0,
  stats: { ok: 0, rejected: 0, errors: 0 },
  lastPayload: null,
  lastEnvelope: null,
  status: ["off", "Detenido"],
};

// Ajustes guardados; el simulador se activa por defecto en equipos sin pantalla táctil.
const saved = store.read("st.agent.settings") ?? {};
const settings = { interval: 2, suite: S.SUITE_AES, sim: !matchMedia("(pointer: coarse)").matches, ...saved,
  sensors: Object.fromEntries(BANK.map((b) => [b.id, saved.sensors?.[b.id] ?? true])) };
const saveSettings = () => store.write("st.agent.settings", settings);
/** Un sensor está disponible si el simulador está activo o el navegador lo ofrece. */
const available = (id) => settings.sim || !!BANK.find((b) => b.id === id)?.web;
/** Un sensor se envía si el usuario lo marcó y está disponible. */
const exposed = (id) => settings.sensors[id] && available(id);

// ---------- Interfaz
/** Cambia la insignia de estado (clase de color y texto). */
function setStatus(kind, text) {
  st.status = [kind, text];
  $("status").className = "badge " + kind;
  $("status").textContent = text;
}

/** Agrega una línea a la bitácora de la página (conserva las 30 más recientes). */
function log(msg, kind = "") {
  const li = document.createElement("li");
  li.innerHTML = `<span class="muted mono">${fmtTime(Date.now())}</span> <span class="${kind}">${esc(msg)}</span>`;
  $("log").prepend(li);
  while ($("log").children.length > 30) $("log").lastChild.remove();
}

/** Agrupa una huella hexadecimal de 4 en 4 caracteres para compararla a simple vista. */
const groupHex = (h) => h.match(/.{1,4}/g).join(" ");

/**
 * Tarjeta del dispositivo. Según el caso muestra: el formulario de vinculación por código (llegó del QR),
 * los datos del dispositivo ya registrado, un aviso para vincular, o el formulario de registro con sesión.
 */
async function renderDevice() {
  const box = $("device");
  if (enroll.enroll && (!st.device || st.device.code !== enroll.enroll)) {
    // Se abrió desde el QR: vincula con el código de un solo uso (no hace falta sesión de usuario).
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
    // Registro con la sesión del usuario: guarda el id y el token del dispositivo.
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

/** Flujo del QR: la huella del QR debe coincidir con la llave que presenta el servidor; entonces se fija y se canjea el código. */
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
    // Quita el código de la URL para que recargar la página no intente canjearlo otra vez.
    history.replaceState(null, "", location.pathname);
    delete enroll.enroll;
    log("Teléfono vinculado y huella fijada desde el QR: " + r.deviceId, "ok");
    renderAll();
  } catch (e) { toast(e.message); log("No se pudo vincular: " + e.message, "bad"); }
}

/**
 * Tarjeta de la huella del servidor: la muestra y permite fijarla la primera vez (confianza en el primer uso).
 * Si cambia respecto a la fijada, alerta de un posible intermediario.
 */
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

/** Texto de estado de un sensor del banco. */
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

/** Tabla del banco de sensores agrupada por categoría; las casillas se bloquean mientras se transmite. */
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

/** Botones y ajustes. Iniciar solo se habilita con un dispositivo registrado y la huella fijada. */
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
  // ChaCha20 solo se puede elegir si el servidor la ofrece.
  $("suite").querySelector(`option[value="${S.SUITE_CHACHA}"]`).disabled = !st.server?.suites?.includes(S.SUITE_CHACHA);
  renderSensors();
}

/** Contadores, datos de la sesión, último JSON en claro y último sobre transmitido. */
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

/** Vuelve a dibujar todas las tarjetas. */
function renderAll() { renderDevice(); renderPin(); renderControls(); renderLive(); }

// ---------- Protocolo
/** Handshake con el servidor; si la firma no coincide con la llave fijada, detiene el agente. */
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

/**
 * Cifra y envía una lectura con el siguiente número de secuencia (abre sesión si no hay).
 * @returns {Promise<string>} "ok" o el código de error del servidor.
 */
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

/**
 * Envía la cola en orden, una lectura a la vez. Mientras hay un reintento pendiente, las muestras nuevas solo se
 * encolan (force = lo llama el temporizador de reintento o el evento online).
 * Cada código del servidor tiene su reacción: sesión desconocida → nuevo handshake; reenvío → siguiente seq;
 * token inválido → se detiene; límite de solicitudes o red → reintento; otro rechazo → se descarta la lectura.
 */
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

/** Programa un reintento con retroceso exponencial: 1, 2, 4, … hasta 30 s. */
function scheduleRetry() {
  st.stats.errors++;
  const delay = Math.min(30000, 1000 * 2 ** st.failures++);
  queue.size().then((n) => setStatus("warn", `Sin conexión (${n} en cola) · reintento en ${Math.round(delay / 1000)} s`));
  clearTimeout(st.retryTimer);
  st.waitingRetry = true;
  st.retryTimer = setTimeout(() => flush(true), delay);
}

// ---------- Muestreo
/**
 * Arma una lectura con los sensores expuestos, la encola y vacía la cola. Con la pestaña oculta no muestrea,
 * porque el navegador detiene los sensores.
 */
function sample() {
  if (document.visibilityState !== "visible") { setStatus("warn", "En pausa (pantalla bloqueada / pestaña oculta)"); return; }
  st.packets++;
  const r = { t: Date.now(), src: settings.sim ? "sim" : "web" };
  const src = settings.sim ? sim.step(settings.interval) : sensors.read();
  // La ventana para el espectro va en uno de cada 5 paquetes.
  if (exposed("mot")) { const m = motion.read(st.packets % 5 === 0); if (m) r.mot = m; }
  for (const k of ["lin", "grv", "gyr", "mfl", "ori"]) if (exposed(k) && src[k]) r[k] = src[k];
  const env = Object.fromEntries(ENV.filter((k) => exposed(k) && src.env?.[k] != null).map((k) => [k, src.env[k]]));
  if (Object.keys(env).length) r.env = env;
  if (exposed("stp") && src.stp) r.stp = src.stp;
  if (exposed("bat")) { const b = settings.sim ? src.bat : battery.read(); if (b) r.bat = b; }
  // El sondeo de enlace viaja dentro de net aunque el tipo de red no esté expuesto.
  const pr = exposed("probe") ? probe.read() : undefined;
  if (exposed("net")) { const n = netinfo.read(pr); if (n) r.net = n; }
  else if (pr) r.net = { probe: pr };
  if (exposed("wifi") && src.wifi) r.wifi = src.wifi;
  if (exposed("cell") && src.cell) r.cell = src.cell;
  r.app = { vis: true, ver: VERSION };
  st.lastPayload = r;
  queue.push(r).then(flush);
}

/** Pide que la pantalla no se apague mientras se transmite (Screen Wake Lock API). */
async function acquireWakeLock() {
  try { if (st.running && navigator.wakeLock && document.visibilityState === "visible") st.wakeLock = await navigator.wakeLock.request("screen"); }
  catch { /* no se concedió: la pantalla puede apagarse, lo que pausa la telemetría */ }
}

/** Inicia los colectores elegidos y el muestreo periódico. */
async function start() {
  st.running = true;
  setStatus("info", "Iniciando…");
  if (settings.sim) sim.start();
  else {
    // Las peticiones de permiso deben ocurrir dentro del clic que inicia el agente (iOS).
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

/** Detiene el muestreo, los colectores y los temporizadores. Las lecturas en cola se conservan. */
function stop() {
  st.running = false;
  clearInterval(st.sampler); clearTimeout(st.retryTimer);
  sim.stop(); motion.stop(); sensors.stop(); probe.stop();
  st.wakeLock?.release?.().catch(() => {}); st.wakeLock = null;
  setStatus("off", "Detenido");
  renderControls();
}

// Pausa y reanuda según la visibilidad de la pestaña.
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

// Al recuperar la conexión, vacía de inmediato la cola acumulada.
addEventListener("online", () => { log("Conexión restablecida: se vacía la cola"); flush(true); });

// ---------- Controles
$("start").onclick = () => (st.running ? stop() : start());
$("src-sim").onchange = (e) => { settings.sim = e.target.checked; saveSettings(); renderControls(); };
// Botones «todos» y «ninguno» del banco de sensores.
for (const [id, on] of [["all-on", true], ["all-off", false]]) {
  $(id).onclick = () => { for (const b of BANK) if (available(b.id)) settings.sensors[b.id] = on; saveSettings(); renderSensors(); };
}
$("interval").oninput = (e) => { settings.interval = Number(e.target.value); $("interval-v").textContent = settings.interval + " s"; };
// Cambiar el intervalo mientras se transmite reinicia el muestreo.
$("interval").onchange = () => { saveSettings(); if (st.running) { stop(); start(); } };
$("suite").onchange = (e) => { settings.suite = e.target.value; saveSettings(); st.session = null; log("Suite cambiada: el próximo envío hace un handshake nuevo"); };
$("clearq").onclick = async () => { await queue.clear(); renderLive(); };

// Arranque: refresco periódico, llave del servidor y primer dibujo.
setInterval(renderLive, 2000);
try { st.server = await api("/api/server-key"); } catch (e) { toast(e.message); }
renderAll();
if (!isSecureContext) log("Contexto no seguro: Web Crypto y los sensores requieren HTTPS o localhost (usa el QR de vinculación)", "bad");
log(`Fingerprint del servidor: ${st.server ? st.server.fingerprint.slice(0, 16) : "?"}…`);
