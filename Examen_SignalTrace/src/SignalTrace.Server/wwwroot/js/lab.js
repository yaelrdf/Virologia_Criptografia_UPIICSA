// Scenario lab (PLAN §11.3 and §17): each scenario runs in the browser with a real lab device, real handshake and
// real HTTP requests, and compares what happened with what the protocol promises.
import { api, store, esc, toast } from "./api.js";
import { requireAuth, renderShell } from "./ui/shell.js";
import * as S from "./crypto/session.js";
import { fromB64, toB64, utf8, entropy } from "./crypto/util.js";
import { encryptWithParams } from "./crypto/sigmix.js";

const auth = requireAuth({ admin: true });
renderShell("lab.html");
const $ = (id) => document.getElementById(id);
const LAB_KEY = "st.lab.device." + auth.username;

let server = null, device = null, session = null;

// ---------- lab device and helpers
async function ensureDevice() {
  server ??= await api("/api/server-key");
  device ??= store.read(LAB_KEY);
  if (!device) {
    const r = await api("/api/devices", { method: "POST", body: { name: "Laboratorio (navegador)", kind: "web" } });
    device = { deviceId: r.deviceId, deviceToken: r.deviceToken };
    store.write(LAB_KEY, device);
  }
  return device;
}

async function handshake({ mitm = false, suite = S.SUITE_AES } = {}) {
  const d = await ensureDevice();
  const { state, request } = await S.beginHandshake(d.deviceId, suite);
  const resp = await api("/api/handshake" + (mitm ? "?mitm=true" : ""), { method: "POST", body: request, token: d.deviceToken });
  const s = await S.completeHandshake(state, resp, fromB64(server.publicKey));
  session = { ...s, seq: 0 };
  return { resp, session };
}

const payload = (extra = {}) => ({ t: Date.now(), src: "web", gps: { lat: 19.3955, lon: -99.0915, acc: 8 }, bat: { lvl: 0.71, chg: false }, ...extra });

async function post(env, token = device.deviceToken) {
  const res = await api("/api/telemetry", { method: "POST", body: typeof env === "string" ? env : JSON.stringify(env), token, raw: true });
  const body = await res.json().catch(() => ({}));
  return { status: res.status, code: res.status === 202 ? "ok" : body.code, message: body.message };
}

async function seal(p, seq = ++session.seq) {
  return S.envelope(session, seq, p.t, p);
}

// ---------- scenarios: { id, title, what, expect, run(log) → actual }
const SCENARIOS = [
  { id: "normal", title: "Normal", what: "Handshake ECDH firmado, luego una lectura SigMix → AES-GCM.", expect: "202",
    async run(log) {
      const { resp } = await handshake();
      log(`Handshake: sesión ${resp.sessionId}, firma ECDSA verificada con la huella fijada ${server.fingerprint.slice(0, 16)}…`);
      const r = await post(await seal(payload()));
      log(`POST /api/telemetry → ${r.status} ${r.code}`);
      return String(r.status);
    } },
  { id: "eve", title: "Eve pasiva", what: "Eve captura el tráfico. Comparamos lo que ve con el texto claro.", expect: "ct ≈ 8 bits/byte",
    async run(log) {
      if (!session) await handshake();
      const p = payload({ net: { type: "wifi", probe: { rtt: 32.5, jit: 2.1, loss: 0 } } });
      const plain = utf8(JSON.stringify(p));
      const env = await seal(p);
      const ct = fromB64(env.ct);
      log(`Texto claro (${plain.length} B, entropía ${entropy(plain).toFixed(2)} bits/byte):`, JSON.stringify(p));
      log(`En el cable (${ct.length} B = texto + relleno + etiqueta de 16 B, entropía ${entropy(ct).toFixed(2)} bits/byte):`, env.ct);
      log("Eve ve did, seq y ts (metadatos autenticados en el AAD) pero no las coordenadas, la batería ni la red.");
      const r = await post(env);
      log(`El servidor lo acepta: ${r.status}`);
      // Entropy over a longer capture so the estimate is meaningful.
      const caps = [];
      for (let i = 0; i < 8; i++) { const e = await seal(payload()); caps.push(...fromB64(e.ct)); await post(e); }
      const h = entropy(Uint8Array.from(caps));
      log(`Entropía de 8 capturas concatenadas (${caps.length} B): ${h.toFixed(2)} bits/byte (máximo teórico 8; el límite para ${caps.length} B es ≈ ${Math.log2(Math.min(256, caps.length)).toFixed(2)})`);
      return h > 7 ? "ct ≈ 8 bits/byte" : `ct ${h.toFixed(2)} bits/byte`;
    } },
  { id: "bitflip", title: "Mallory modifica (bit flip)", what: "Mallory invierte un bit del texto cifrado y reenvía.", expect: "422 auth_failed",
    async run(log) {
      if (!session) await handshake();
      const env = await seal(payload());
      const ct = fromB64(env.ct);
      ct[7] ^= 0x01;
      const r = await post({ ...env, ct: toB64(ct) });
      log(`Bit 0 del byte 7 invertido → ${r.status} ${r.code}: ${r.message}`);
      const ok = await post(env);
      log(`El original con el mismo seq sigue siendo aceptado (${ok.status}): un paquete alterado no consume números de secuencia.`);
      return `${r.status} ${r.code}`;
    } },
  { id: "replay", title: "Mallory reenvía (replay)", what: "Mallory captura un paquete válido y lo vuelve a enviar.", expect: "409 replay",
    async run(log) {
      if (!session) await handshake();
      const env = await seal(payload());
      const a = await post(env);
      log(`Envío original → ${a.status}`);
      const b = await post(env);
      log(`Reenvío idéntico → ${b.status} ${b.code}: ${b.message}`);
      return `${b.status} ${b.code}`;
    } },
  { id: "aad", title: "Mallory cambia ts o seq del sobre", what: "Se altera ts en el sobre sin tocar ct (ts va en el AAD).", expect: "422 auth_failed",
    async run(log) {
      if (!session) await handshake();
      const env = await seal(payload());
      const r = await post({ ...env, ts: env.ts - 3_600_000 });
      log(`ts retrasado 1 h → ${r.status} ${r.code}. El AAD = "ST1" ‖ did ‖ seq ‖ ts no coincide y la etiqueta GCM falla.`);
      return `${r.status} ${r.code}`;
    } },
  { id: "mitm", title: "Mallory MITM en el handshake", what: "El servidor (con Lab:EnableMitmDemo) firma con una llave que no es la fijada.", expect: "firma rechazada",
    async run(log) {
      try {
        await handshake({ mitm: true });
        log("¡El cliente aceptó la llave! (no debería pasar)");
        return "aceptada";
      } catch (e) {
        log(`El cliente verificó la firma ECDSA del transcript con la llave fijada y abortó: ${e.message}`);
        session = null;
        return "firma rechazada";
      }
    } },
  { id: "lost", title: "Sesión perdida (reinicio del servidor)", what: "Se borran las sesiones en memoria del servidor.", expect: "404 → 202",
    async run(log) {
      if (!session) await handshake();
      const cleared = await api("/api/lab/reset-sessions", { method: "POST" });
      log(`Sesiones borradas en el servidor: ${cleared.cleared}`);
      const a = await post(await seal(payload()));
      log(`Envío con la sesión vieja → ${a.status} ${a.code}`);
      await handshake();
      const b = await post(await seal(payload()));
      log(`Nuevo handshake (llaves nuevas) y reenvío → ${b.status}`);
      return `${a.status} → ${b.status}`;
    } },
  { id: "invalid", title: "Contenido inválido", what: "Paquete auténtico pero con latitud 200° (fuera de rango).", expect: "422 invalid_payload",
    async run(log) {
      if (!session) await handshake();
      const r = await post(await seal(payload({ gps: { lat: 200, lon: 0 } })));
      log(`→ ${r.status} ${r.code}: ${r.message}. La etiqueta era válida; la validación ocurre después de descifrar.`);
      return `${r.status} ${r.code}`;
    } },
  { id: "token", title: "Token de otro dispositivo / sin token", what: "Se envía un sobre con un deviceId que no corresponde al token, y otro sin token.", expect: "401 unauthorized",
    async run(log) {
      if (!session) await handshake();
      const env = await seal(payload());
      const r = await post({ ...env, did: "00000000-0000-0000-0000-000000000000" });
      log(`did ajeno → ${r.status} ${r.code}`);
      const res = await fetch("/api/telemetry", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(env) });
      log(`Sin token → ${res.status}`);
      return `${r.status} ${r.code}`;
    } },
  { id: "chacha", title: "Suite ChaCha20-Poly1305", what: "Handshake negociando ChaCha20-Poly1305 (la suite queda firmada en el transcript).", expect: "202",
    async run(log) {
      const { resp } = await handshake({ suite: S.SUITE_CHACHA });
      log(`Suite acordada: ${resp.suite}`);
      const r = await post(await seal(payload()));
      log(`→ ${r.status}`);
      await handshake();
      return String(r.status);
    } },
  { id: "sigmix", title: "Ataque a SigMix sin AES (texto claro conocido)", what: "SigMix de 1 ronda con parámetros reutilizados, sin capa AEAD.", expect: "texto recuperado",
    async run(log) {
      const r = await api("/api/lab/sigmix-attack", { method: "POST", body: {} });
      log(`Parámetros secretos: seed=${r.secret.seed}, k=${r.secret.k}, m=${r.secret.m}, rot=${r.secret.rot}`);
      r.log.forEach((l) => log(l));
      log(`Parámetros hallados: seed=${r.found?.seed}, k=${r.found?.k}, m=${r.found?.m}, rot≡${r.found?.rot} · ${r.candidates} candidatos en ${r.elapsedMs} ms`);
      log("Segundo mensaje descifrado sin conocer la llave:", r.recoveredPlaintext);
      log(r.note);
      return r.recoveredPlaintext ? "texto recuperado" : "falló";
    } },
  { id: "caesar", title: "Contraste: César", what: "Cifrado clásico byte + 3 mod 256, roto por fuerza bruta.", expect: "roto en ≤ 256 intentos",
    async run(log) {
      const p = utf8(JSON.stringify(payload()));
      const c = p.map((b) => (b + 3) & 255);
      log(`Entropía: texto ${entropy(p).toFixed(2)} vs César ${entropy(c).toFixed(2)} bits/byte (idéntica: solo se renombran los símbolos).`);
      for (let k = 0; k < 256; k++) {
        const d = c.map((b) => (b - k + 256) & 255);
        if (d[0] === 0x7b && d[1] === 0x22) { log(`Desplazamiento encontrado: ${k} (el JSON empieza con {").`, new TextDecoder().decode(d)); return "roto en ≤ 256 intentos"; }
      }
      return "no roto";
    } },
  { id: "sigmixdemo", title: "SigMix: ejemplo de mano", what: "\"Hi\" = (72, 105), seed 7, k 3, m 5, 1 ronda sin rotación.", expect: "(236, 198)",
    async run(log) {
      const stages = [];
      const c = encryptWithParams([72, 105], { seed: 7, prm: [{ k: 3, m: 5, rot: 0 }] }, (n, s) => stages.push(`${n}: (${[...s].join(", ")})`), false);
      stages.forEach((s) => log(s));
      log("Flujo: s₁ = 5·7+3 = 38, s₂ = 5·38+3 = 193 → (72+38, 105+193) mod 256 = (110, 42); K = [[1,3],[5,16]] → (110+3·42, 5·110+16·42) mod 256 = (236, 198).");
      return `(${[...c].join(", ")})`;
    } },
];

// ---------- UI
function card(s) {
  return `<section class="card" id="sc-${s.id}">
    <h3>${esc(s.title)} <span class="badge off" id="st-${s.id}">pendiente</span></h3>
    <p class="small muted">${esc(s.what)} <b>Esperado:</b> ${esc(s.expect)}</p>
    <button data-run="${s.id}">Ejecutar</button>
    <div class="out" id="out-${s.id}"></div></section>`;
}
$("scenarios").innerHTML = SCENARIOS.map(card).join("");

async function run(s) {
  const out = $("out-" + s.id);
  out.innerHTML = "";
  const badge = $("st-" + s.id);
  badge.className = "badge info"; badge.textContent = "ejecutando";
  const log = (msg, data) => {
    out.insertAdjacentHTML("beforeend", `<div class="small" style="margin-top:6px">${esc(msg)}</div>${data != null ? `<pre class="mono hex" style="margin-top:4px;max-height:140px">${esc(data)}</pre>` : ""}`);
  };
  let actual;
  try { actual = await s.run(log); } catch (e) { actual = "error: " + e.message; log(e.message); }
  const pass = actual === s.expect;
  badge.className = "badge " + (pass ? "ok" : "bad");
  badge.textContent = pass ? "como se esperaba" : "distinto";
  return { s, actual, pass };
}

document.addEventListener("click", (e) => {
  const id = e.target.dataset?.run;
  if (id) run(SCENARIOS.find((s) => s.id === id));
});

$("run-all").onclick = async () => {
  $("run-all").disabled = true;
  const rows = [];
  for (const s of SCENARIOS) rows.push(await run(s));
  $("summary").innerHTML = `<table><thead><tr><th>Escenario</th><th>Esperado</th><th>Obtenido</th><th></th></tr></thead><tbody>${
    rows.map((r) => `<tr><td>${esc(r.s.title)}</td><td>${esc(r.s.expect)}</td><td>${esc(r.actual)}</td><td>${r.pass ? "✅" : "❌"}</td></tr>`).join("")}</tbody></table>
    <p class="small muted" style="margin-top:8px">${rows.filter((r) => r.pass).length}/${rows.length} escenarios con el resultado esperado.</p>`;
  $("run-all").disabled = false;
};

try {
  const cfg = await api("/api/lab/config");
  $("cfg").innerHTML = Object.entries(cfg).map(([k, v]) => `<dt>${k}</dt><dd>${esc(Array.isArray(v) ? v.join(", ") : String(v))}</dd>`).join("");
  await ensureDevice();
  $("dev").textContent = device.deviceId;
} catch (e) { toast(e.message); }
