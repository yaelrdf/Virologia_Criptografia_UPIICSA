// Acceleration from DeviceMotionEvent, resampled to ~32 Hz. Each interval yields mean axes, RMS and peak of the
// dynamic part of |a|, and every N packets a window of 64 samples of |a| for the server-side DFT.
import { round } from "./util.js";

export const FS = 32;
const WINDOW = 64;
let samples = [];          // |a| at FS Hz (ring of WINDOW)
let interval = [];         // samples of the current interval: [ax, ay, az, |a|]
let lastT = 0, listening = false, error = null;

function onMotion(e) {
  const a = e.accelerationIncludingGravity;
  if (!a || a.x == null) return;
  const t = performance.now();
  if (t - lastT < 1000 / FS - 2) return;
  lastT = t;
  const m = Math.hypot(a.x, a.y, a.z);
  push(a.x, a.y, a.z, m);
}

export function push(ax, ay, az, m) {
  interval.push([ax, ay, az, m]);
  samples.push(m);
  if (samples.length > WINDOW) samples.shift();
}

/** iOS needs an explicit permission requested from a user gesture. */
export async function start() {
  if (typeof DeviceMotionEvent === "undefined") { error = "Sensor de movimiento no disponible"; return; }
  if (typeof DeviceMotionEvent.requestPermission === "function") {
    try { if ((await DeviceMotionEvent.requestPermission()) !== "granted") { error = "Permiso de movimiento denegado"; return; } }
    catch { error = "Permiso de movimiento denegado"; return; }
  }
  addEventListener("devicemotion", onMotion);
  listening = true;
}

export function stop() { removeEventListener("devicemotion", onMotion); listening = false; }
export function status() { return error ?? (listening ? (samples.length ? `${samples.length} muestras` : "sin datos (¿escritorio?)") : "apagado"); }

/** Aggregates the samples of the last interval. includeWindow attaches mot.mag (64 samples). */
export function read(includeWindow) {
  if (!interval.length) return undefined;
  const n = interval.length;
  const mean = (i) => interval.reduce((s, v) => s + v[i], 0) / n;
  const mMean = mean(3);
  const dyn = interval.map((v) => v[3] - mMean);
  const rms = Math.sqrt(dyn.reduce((s, d) => s + d * d, 0) / n);
  const peak = Math.max(...dyn.map(Math.abs));
  const mot = { ax: round(mean(0), 3), ay: round(mean(1), 3), az: round(mean(2), 3), rms: round(rms, 4), peak: round(peak, 4), fs: FS };
  if (includeWindow && samples.length === WINDOW) mot.mag = samples.map((v) => round(v, 3));
  interval = [];
  return mot;
}
