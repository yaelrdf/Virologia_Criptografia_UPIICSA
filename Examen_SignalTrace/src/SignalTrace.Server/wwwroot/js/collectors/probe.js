// Calidad del enlace medida por la propia aplicación contra nuestro servidor (funciona igual en web y en nativo):
// 10 POST /api/ping pequeños por ronda → RTT medio, jitter (media de |Δrtt|) y pérdida; cada pocas rondas una
// descarga de 64 KB → kbps.
import { round } from "./util.js";

let last = null, timer = null, rounds = 0, running = false;

/**
 * Un sondeo: envía 64 bytes aleatorios y mide cuánto tarda la respuesta.
 * @param {number} [timeoutMs] Tiempo máximo antes de contarlo como perdido.
 * @returns {Promise<number|null>} El RTT en ms, o null si se perdió.
 */
async function once(timeoutMs = 2000) {
  const ctl = new AbortController();
  const t = setTimeout(() => ctl.abort(), timeoutMs);
  const body = crypto.getRandomValues(new Uint8Array(64));
  const t0 = performance.now();
  try {
    const r = await fetch("/api/ping", { method: "POST", body, signal: ctl.signal, cache: "no-store" });
    await r.arrayBuffer();
    return r.ok ? performance.now() - t0 : null;
  } catch { return null; } finally { clearTimeout(t); }
}

/** Descarga 64 KB y calcula el rendimiento en kbit/s; null si falla. */
async function throughput() {
  const t0 = performance.now();
  try {
    const r = await fetch("/api/ping/download?kb=64", { cache: "no-store" });
    const n = (await r.arrayBuffer()).byteLength;
    return (n * 8) / ((performance.now() - t0) / 1000) / 1000;
  } catch { return null; }
}

/**
 * Una ronda completa de medición.
 * @param {number} [count] Sondeos por ronda.
 * @returns {Promise<{loss: number, rtt?: number, jit?: number, kbps?: number}>}
 */
export async function measure(count = 10) {
  const rtts = [];
  let lost = 0;
  // Los sondeos van uno tras otro para no competir entre sí por el enlace.
  for (let i = 0; i < count; i++) {
    const v = await once();
    if (v == null) lost++; else rtts.push(v);
  }
  const res = { loss: round(lost / count, 2) };
  if (rtts.length) {
    res.rtt = round(rtts.reduce((a, b) => a + b, 0) / rtts.length, 1);
    // Jitter: media de la diferencia absoluta entre RTT consecutivos.
    let j = 0;
    for (let i = 1; i < rtts.length; i++) j += Math.abs(rtts[i] - rtts[i - 1]);
    res.jit = round(rtts.length > 1 ? j / (rtts.length - 1) : 0, 1);
  }
  // El rendimiento se mide en una de cada 6 rondas porque la descarga consume datos.
  if (rounds++ % 6 === 0) { const k = await throughput(); if (k) res.kbps = Math.round(k); }
  return res;
}

/**
 * Mide de forma periódica mientras la página está visible.
 * @param {number} [everyMs] Pausa entre rondas.
 */
export function start(everyMs = 10000) {
  if (running) return;
  running = true;
  const loop = async () => {
    if (!running) return;
    if (document.visibilityState === "visible") last = { ...(await measure()), at: Date.now() };
    timer = setTimeout(loop, everyMs);
  };
  loop();
}
/** Detiene las mediciones. */
export function stop() { running = false; clearTimeout(timer); }
/** Texto de estado con el último resultado. */
export function status() { return last ? `RTT ${last.rtt ?? "—"} ms · jitter ${last.jit ?? "—"} · pérdida ${Math.round(last.loss * 100)} %` : "midiendo…"; }
/** Último resultado del sondeo (sin la marca de tiempo interna) si tiene menos de un minuto. */
export function read() {
  if (!last || Date.now() - last.at > 60000) return undefined;
  const { at, ...p } = last;
  return p;
}
