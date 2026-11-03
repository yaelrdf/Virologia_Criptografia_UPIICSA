// Aceleración de DeviceMotionEvent, remuestreada a ~32 Hz. Cada intervalo produce los ejes medios, el RMS y el pico
// de la parte dinámica de |a| y, cada N paquetes, una ventana de 64 muestras de |a| para la DFT del servidor.
import { round } from "./util.js";

/** Frecuencia de muestreo objetivo en Hz. */
export const FS = 32;
/** Tamaño de la ventana para el espectro (potencia de dos, así el servidor usa FFT). */
const WINDOW = 64;
let samples = [];          // |a| a FS Hz (anillo de WINDOW muestras)
let interval = [];         // muestras del intervalo actual: [ax, ay, az, |a|]
let lastT = 0, listening = false, error = null;

/** Manejador del evento devicemotion: descarta eventos demasiado seguidos para quedarse cerca de FS. */
function onMotion(e) {
  const a = e.accelerationIncludingGravity;
  if (!a || a.x == null) return;
  const t = performance.now();
  if (t - lastT < 1000 / FS - 2) return;
  lastT = t;
  const m = Math.hypot(a.x, a.y, a.z);
  push(a.x, a.y, a.z, m);
}

/**
 * Agrega una muestra al intervalo y a la ventana. El simulador también la usa para inyectar datos sintéticos.
 * @param {number} ax Eje X (m/s²).
 * @param {number} ay Eje Y (m/s²).
 * @param {number} az Eje Z (m/s²).
 * @param {number} m Magnitud |a|.
 */
export function push(ax, ay, az, m) {
  interval.push([ax, ay, az, m]);
  samples.push(m);
  if (samples.length > WINDOW) samples.shift();
}

/** Empieza a escuchar el acelerómetro. iOS exige pedir permiso de forma explícita desde un gesto del usuario. */
export async function start() {
  if (typeof DeviceMotionEvent === "undefined") { error = "Sensor de movimiento no disponible"; return; }
  if (typeof DeviceMotionEvent.requestPermission === "function") {
    try { if ((await DeviceMotionEvent.requestPermission()) !== "granted") { error = "Permiso de movimiento denegado"; return; } }
    catch { error = "Permiso de movimiento denegado"; return; }
  }
  addEventListener("devicemotion", onMotion);
  listening = true;
}

/** Deja de escuchar el acelerómetro. */
export function stop() { removeEventListener("devicemotion", onMotion); listening = false; }
/** Texto de estado para la página del agente. */
export function status() { return error ?? (listening ? (samples.length ? `${samples.length} muestras` : "sin datos (¿escritorio?)") : "apagado"); }

/**
 * Resume las muestras del último intervalo y lo vacía. Con includeWindow adjunta mot.mag (64 muestras).
 * @param {boolean} includeWindow Si se incluye la ventana para el espectro.
 * @returns {object|undefined} Bloque mot de la carga, o undefined si no hubo muestras.
 */
export function read(includeWindow) {
  if (!interval.length) return undefined;
  const n = interval.length;
  const mean = (i) => interval.reduce((s, v) => s + v[i], 0) / n;
  // Parte dinámica: magnitud menos su media (quita la gravedad).
  const mMean = mean(3);
  const dyn = interval.map((v) => v[3] - mMean);
  const rms = Math.sqrt(dyn.reduce((s, d) => s + d * d, 0) / n);
  const peak = Math.max(...dyn.map(Math.abs));
  const mot = { ax: round(mean(0), 3), ay: round(mean(1), 3), az: round(mean(2), 3), rms: round(rms, 4), peak: round(peak, 4), fs: FS };
  if (includeWindow && samples.length === WINDOW) mot.mag = samples.map((v) => round(v, 3));
  interval = [];
  return mot;
}
