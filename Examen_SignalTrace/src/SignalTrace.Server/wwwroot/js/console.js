// Admin console (PLAN §11.2): every packet stage by stage, SigMix round by round, entropies and a step-by-step animation.
import { api, getAuth, esc, fmtTime } from "./api.js";
import { requireAuth, renderShell } from "./ui/shell.js";
import { fromHex } from "./crypto/util.js";
import * as C from "./ui/charts.js";

requireAuth({ admin: true });
renderShell("console.html");
const $ = (id) => document.getElementById(id);

const st = { traces: [], selected: null, paused: false, stageIdx: 0, counts: {} };
const CODE_LABEL = { ok: "aceptado", replay: "replay (409)", auth_failed: "etiqueta inválida (422)", invalid_payload: "contenido inválido (422)",
  unknown_session: "sesión desconocida (404)", unauthorized: "no autorizado (401)", malformed: "mal formado (400)" };
const STAGE_HELP = {
  padded: "Relleno estilo PKCS#7 con bloque de 2: se agregan n bytes de valor n (1 si la longitud es impar, 2 si es par).",
  stream: "Suma de flujo: s ← (5·s + 3) mod 256 (LCG de periodo completo por Hull–Dobell); p[i] ← p[i] + s.",
  matrix: "Mezcla lineal por pares con K = [[1, k], [m, 1+k·m]] (det K = 1, invertible mod 256).",
  rotate: "Permutación: rotación circular a la izquierda de rot mod n posiciones.",
};

// ---------- helpers
const short = (s, n = 8) => (s ? s.slice(0, n) + "…" : "—");
const entPct = (e) => `${Math.max(0, Math.min(100, (e / 8) * 100))}%`;
function hexView(hex, prevHex, max = 320) {
  const b = hex.match(/../g) ?? [];
  const p = prevHex?.match(/../g) ?? [];
  const out = b.slice(0, max).map((x, i) => `<span class="${p.length && p[i] !== x ? "chg" : ""}">${x}</span>`).join("");
  return out + (b.length > max ? ` <span class="muted">… (+${b.length - max} bytes)</span>` : "");
}

/** Splits the AAD: "ST1" || utf8(deviceId) || u64be(seq) || u64be(ts). */
function aadParts(hex) {
  const b = fromHex(hex);
  const did = new TextDecoder().decode(b.slice(3, b.length - 16));
  const dv = new DataView(b.buffer);
  return `"ST1" ‖ "${did}" ‖ seq=${dv.getBigUint64(b.length - 16)} ‖ ts=${dv.getBigUint64(b.length - 8)}`;
}

// ---------- packet list
function renderCodes() {
  $("codes").innerHTML = Object.entries(st.counts).map(([c, n]) =>
    `<span class="badge ${c === "ok" ? "ok" : c === "unknown_session" ? "warn" : "bad"}">${esc(CODE_LABEL[c] ?? c)}: ${n}</span>`).join("");
}
function renderList() {
  const f = $("f-status").value;
  const list = st.traces.filter((t) => !f || t.status === f).slice(0, 150);
  $("pcount").textContent = `${list.length}`;
  $("pkts").innerHTML = list.map((t) => `<li><button data-id="${t.id}" aria-pressed="${t.id === st.selected?.id}">
      <span class="mono">#${t.seq ?? "—"}</span><span>${esc(short(t.did, 8))}</span>
      <span class="badge ${t.status === "ok" ? "ok" : "bad"}">${esc(t.code)}</span>
      <span class="sub">${fmtTime(t.tsServer)} · ${t.wireBytes} B · ${esc(t.transport)}${t.suite ? " · " + esc(t.suite) : ""}</span></button></li>`).join("")
    || `<li class="muted small">Sin paquetes.</li>`;
}
$("pkts").addEventListener("click", (e) => { const b = e.target.closest("button[data-id]"); if (b) show(st.traces.find((t) => String(t.id) === b.dataset.id)); });
$("f-status").onchange = renderList;

function add(t) {
  st.counts[t.code] = (st.counts[t.code] ?? 0) + 1;
  st.traces.unshift(t);
  if (st.traces.length > 300) st.traces.pop();
  renderCodes();
  if (st.paused) return;
  renderList();
  if (!st.selected || !animating) show(t);
}

// ---------- detail
const entChart = C.xyChart($("c-ent"), "bits/byte", C.SERIES.blue, { yLabel: "bits/byte" });
entChart.options.scales.y.max = 8;

function show(t) {
  if (!t) return;
  st.selected = t;
  $("empty").hidden = true;
  $("view").hidden = false;
  renderList();
  // 1. wire
  $("w-meta").textContent = `${t.wireBytes} B · ${t.transport}`;
  try { $("w-body").textContent = JSON.stringify(JSON.parse(t.wire), null, 2); } catch { $("w-body").textContent = t.wire; }
  // 2. verification
  $("v-kv").innerHTML = [
    ["Resultado", `<span class="badge ${t.status === "ok" ? "ok" : "bad"}">${esc(CODE_LABEL[t.code] ?? t.code)}</span>`],
    t.message && ["Motivo", esc(t.message)], t.warning && ["Aviso", esc(t.warning)],
    ["Dispositivo", `<span class="mono">${esc(t.did ?? "—")}</span>`], ["Sesión", `<span class="mono">${esc(t.sid ?? "—")}</span>`],
    ["seq", `${t.seq}${t.lastSeq != null ? ` (último aceptado: ${t.lastSeq})` : ""}`], ["ts", `${t.ts} (${fmtTime(t.ts)})`], ["Suite", esc(t.suite ?? "—")],
    ["Orden", "token ↔ did → sesión → seq fresco → etiqueta AEAD → SigMix⁻¹ → validación → se consume seq"],
  ].filter(Boolean).map(([k, v]) => `<dt>${k}</dt><dd>${v}</dd>`).join("");
  $("v-times").textContent = Object.entries(t.micros ?? {}).map(([k, v]) => `${k}: ${v} µs`).join(" · ");
  // 3. AEAD
  $("a-title").textContent = t.suite === "chacha20-poly1305" ? "ChaCha20-Poly1305" : "AES-256-GCM";
  $("a-kv").innerHTML = [
    t.nonce && ["Nonce (12 B)", `<span class="mono">${t.nonce}</span><br><span class="small muted">iv_salt (4 B, HKDF) ‖ u64be(seq) — nunca se repite con la misma llave</span>`],
    t.aad && ["AAD", `<span class="mono">${esc(aadParts(t.aad))}</span><br><span class="small muted">autenticado pero no cifrado: si cambia did, seq o ts falla la etiqueta</span>`],
    t.tag && ["Etiqueta (16 B)", `<span class="mono">${t.tag}</span>`],
  ].filter(Boolean).map(([k, v]) => `<dt>${k}</dt><dd>${v}</dd>`).join("") || `<dt>—</dt><dd class="muted">No llegó a esta etapa</dd>`;
  $("a-ct").innerHTML = t.ciphertext ? hexView(t.ciphertext) : `<span class="muted">—</span>`;
  $("a-len").textContent = t.ciphertext ? `${t.ciphertext.length / 2} B` : "";
  $("a-ent").textContent = t.ciphertextEntropy != null ? `entropía ${t.ciphertextEntropy.toFixed(2)} bits/byte` : "";
  $("a-bar").style.width = t.ciphertextEntropy != null ? entPct(t.ciphertextEntropy) : "0";
  // 4. SigMix
  renderSigMix(t);
  // 5. plaintext
  $("p-json").textContent = t.plaintextJson ? JSON.stringify(JSON.parse(t.plaintextJson), null, 2) : "— (no expuesto o no descifrado)";
  $("p-ent").textContent = t.plaintextEntropy != null ? `entropía ${t.plaintextEntropy.toFixed(2)} bits/byte` : "";
  // Caesar contrast
  $("c-hex").innerHTML = t.caesarHex ? hexView(t.caesarHex, null, 160) : "—";
  $("c-ent-caesar").textContent = t.caesarEntropy != null ? `${t.caesarEntropy.toFixed(2)} bits/byte` : "";
  // entropy chart
  const rows = [["texto claro", t.plaintextEntropy], ...(t.sigMixStages ?? []).filter((s) => /padded|rotate/.test(s.name)).map((s) => [s.name, s.entropy]),
    ["ct (AEAD)", t.ciphertextEntropy], ["César", t.caesarEntropy]].filter((r) => r[1] != null);
  entChart.data.labels = rows.map((r) => r[0]);
  entChart.data.datasets[0].data = rows.map((r) => r[1]);
  entChart.data.datasets[0].backgroundColor = rows.map((r) => (r[0] === "César" ? C.SERIES.red : r[0].startsWith("ct") ? C.SERIES.green : C.SERIES.blue));
  entChart.update("none");
  // keys
  $("keys-card").hidden = !t.keys;
  if (t.keys) $("keys").innerHTML = Object.entries(t.keys).map(([k, v]) => `<dt>${k}</dt><dd class="mono">${v}</dd>`).join("");
}

function renderSigMix(t) {
  const stages = t.sigMixStages ?? [];
  const p = t.sigMixParams;
  $("s-params").textContent = p ? `seed = ${p.seed} · 3 rondas · parámetros = SHA-256(k_sig ‖ seq ‖ "SigMix")` : "";
  $("s-rounds-tbl").innerHTML = p ? `<table><thead><tr><th>Ronda</th><th>k</th><th>m</th><th>rot</th><th>K (mod 256)</th><th>det</th></tr></thead><tbody>${
    p.rounds.map((r, i) => `<tr><td>${i + 1}</td><td>${r.k}</td><td>${r.m}</td><td>${r.rot}</td>
      <td><span class="matrix"><span>1</span><span>${r.k}</span><span>${r.m}</span><span>${(1 + r.k * r.m) & 255}</span></span></td><td>1</td></tr>`).join("")}</tbody></table>` : "";
  $("s-rounds").innerHTML = stages.map((s, i) => `<button role="tab" data-i="${i}" aria-pressed="${i === st.stageIdx}">${s.name}</button>`).join("")
    || `<span class="muted small">Sin etapas (paquete rechazado antes de descifrar o texto claro no expuesto).</span>`;
  selectStage(Math.min(st.stageIdx, Math.max(0, stages.length - 1)));
}
function selectStage(i) {
  const stages = st.selected?.sigMixStages ?? [];
  st.stageIdx = i;
  document.querySelectorAll("#s-rounds button").forEach((b) => b.setAttribute("aria-pressed", String(Number(b.dataset.i) === i)));
  const s = stages[i];
  if (!s) { $("s-hex").innerHTML = ""; $("s-explain").textContent = ""; $("s-bar").style.width = "0"; return; }
  const kind = s.name.includes(".") ? s.name.split(".")[1] : s.name;
  $("s-explain").textContent = `${s.name}: ${STAGE_HELP[kind] ?? ""} Entropía ${s.entropy.toFixed(2)} bits/byte. Los bytes resaltados cambiaron respecto a la etapa anterior.`;
  $("s-hex").innerHTML = hexView(s.hex, stages[i - 1]?.hex);
  $("s-bar").style.width = entPct(s.entropy);
}
$("s-rounds").addEventListener("click", (e) => { const b = e.target.closest("button[data-i]"); if (b) selectStage(Number(b.dataset.i)); });

// ---------- step-by-step animation (device side order: plaintext → SigMix rounds → AEAD → wire)
let animating = false;
async function play() {
  const t = st.selected;
  if (!t || animating) return;
  animating = true;
  $("play").textContent = "⏸ Animando…";
  const cards = [...document.querySelectorAll(".stage")];
  const focus = async (name, ms = 1100) => {
    cards.forEach((c) => c.classList.toggle("active", c.dataset.stage === name));
    document.querySelector(`.stage[data-stage="${name}"]`).scrollIntoView({ behavior: "smooth", block: "center" });
    await new Promise((r) => setTimeout(r, ms));
  };
  await focus("plain");
  for (let i = 0; i < (t.sigMixStages?.length ?? 0); i++) { selectStage(i); await focus("sigmix", 900); }
  await focus("aead");
  await focus("wire");
  await focus("verify", 1500);
  cards.forEach((c) => c.classList.remove("active"));
  animating = false;
  $("play").textContent = "▶ Paso a paso";
}
$("play").onclick = play;
$("pause").onclick = () => { st.paused = !st.paused; $("pause").textContent = st.paused ? "Reanudar" : "Pausar"; if (!st.paused) renderList(); };

// ---------- live
const hub = new signalR.HubConnectionBuilder().withUrl("/hubs/live", { accessTokenFactory: () => getAuth()?.token ?? "" })
  .withAutomaticReconnect().configureLogging(signalR.LogLevel.Warning).build();
const setLive = (k, s) => { $("live").className = "badge " + k; $("live").textContent = s; };
hub.on("PacketTrace", add);
hub.onreconnecting(() => setLive("warn", "Reconectando…"));
hub.onreconnected(() => setLive("ok", "En vivo"));
hub.onclose(() => setLive("bad", "Sin conexión"));

const recent = await api("/api/console/traces?limit=60").catch(() => []);
recent.reverse().forEach((t) => { st.counts[t.code] = (st.counts[t.code] ?? 0) + 1; st.traces.unshift(t); });
renderCodes();
renderList();
if (st.traces[0]) show(st.traces[0]);
try { await hub.start(); setLive("ok", "En vivo"); } catch { setLive("bad", "Sin conexión"); }
