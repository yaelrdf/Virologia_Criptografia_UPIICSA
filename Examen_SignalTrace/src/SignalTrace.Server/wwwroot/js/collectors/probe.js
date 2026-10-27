// Link quality measured by the app itself against our server (works the same on web and native):
// 10 small POST /api/ping per round → mean RTT, jitter (mean |Δrtt|), loss; every few rounds a 64 KB download → kbps.
import { round } from "./util.js";

let last = null, timer = null, rounds = 0, running = false;

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

async function throughput() {
  const t0 = performance.now();
  try {
    const r = await fetch("/api/ping/download?kb=64", { cache: "no-store" });
    const n = (await r.arrayBuffer()).byteLength;
    return (n * 8) / ((performance.now() - t0) / 1000) / 1000;
  } catch { return null; }
}

export async function measure(count = 10) {
  const rtts = [];
  let lost = 0;
  for (let i = 0; i < count; i++) {
    const v = await once();
    if (v == null) lost++; else rtts.push(v);
  }
  const res = { loss: round(lost / count, 2) };
  if (rtts.length) {
    res.rtt = round(rtts.reduce((a, b) => a + b, 0) / rtts.length, 1);
    let j = 0;
    for (let i = 1; i < rtts.length; i++) j += Math.abs(rtts[i] - rtts[i - 1]);
    res.jit = round(rtts.length > 1 ? j / (rtts.length - 1) : 0, 1);
  }
  if (rounds++ % 6 === 0) { const k = await throughput(); if (k) res.kbps = Math.round(k); }
  return res;
}

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
export function stop() { running = false; clearTimeout(timer); }
export function status() { return last ? `RTT ${last.rtt ?? "—"} ms · jitter ${last.jit ?? "—"} · pérdida ${Math.round(last.loss * 100)} %` : "midiendo…"; }
/** Last probe result (without the internal timestamp) if it is recent. */
export function read() {
  if (!last || Date.now() - last.at > 60000) return undefined;
  const { at, ...p } = last;
  return p;
}
