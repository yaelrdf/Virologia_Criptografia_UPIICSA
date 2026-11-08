// Sensores de movimiento, orientación y entorno del navegador del teléfono, además del acelerómetro (motion.js):
// aceleración lineal, gravedad y giroscopio desde DeviceMotionEvent, orientación desde DeviceOrientationEvent,
// magnetómetro y luz ambiental desde la Generic Sensor API (Chrome; en algunas versiones detrás de una opción).
// Cada intervalo produce la media de las muestras recibidas desde la lectura anterior.
import { round } from "./util.js";

const acc = {};             // bloque → { x, y, z, n } (sumas para promediar)
let orientation = null;     // último { az, pi, ro }
let lux = null;             // última iluminancia
const generic = [];         // objetos Generic Sensor en marcha
const errors = {};          // bloque → mensaje de error
let want = {};              // bloques elegidos por el usuario
let started = false;

/** Qué bloques soporta el navegador, antes de pedir ningún permiso. */
export function support() {
  const motion = typeof DeviceMotionEvent !== "undefined";
  return {
    lin: motion || "LinearAccelerationSensor" in window,
    grv: "GravitySensor" in window || motion,
    gyr: "Gyroscope" in window || motion,
    mfl: "Magnetometer" in window,
    ori: typeof DeviceOrientationEvent !== "undefined",
    lux: "AmbientLightSensor" in window,
  };
}

/** Acumula una muestra de un bloque vectorial; ignora valores no finitos. */
function add(block, x, y, z) {
  if (![x, y, z].every(Number.isFinite)) return;
  const a = (acc[block] ??= { x: 0, y: 0, z: 0, n: 0 });
  a.x += x; a.y += y; a.z += z; a.n++;
}

/** Manejador de devicemotion: aceleración lineal, gravedad (con gravedad − sin gravedad) y giroscopio. */
function onMotion(e) {
  const g = e.accelerationIncludingGravity, a = e.acceleration, r = e.rotationRate;
  if (want.lin && a?.x != null) add("lin", a.x, a.y, a.z);
  if (want.grv && g?.x != null && a?.x != null) add("grv", g.x - a.x, g.y - a.y, g.z - a.z);
  // rotationRate viene en °/s alrededor de z (alpha), x (beta) e y (gamma); la carga usa rad/s en x, y, z.
  if (want.gyr && r?.alpha != null) add("gyr", r.beta * Math.PI / 180, r.gamma * Math.PI / 180, r.alpha * Math.PI / 180);
}

/** Manejador de orientación: guarda azimut, cabeceo y alabeo en grados. */
function onOrientation(e) {
  if (e.alpha == null && e.beta == null) return;
  orientation = { az: round(((e.alpha ?? 0) + 360) % 360, 1), pi: round(e.beta ?? 0, 1), ro: round(e.gamma ?? 0, 1) };
}

/**
 * Arranca un sensor de la Generic Sensor API a 10 Hz si el navegador lo tiene.
 * @param {string} block Bloque al que corresponde (para los errores).
 * @param {string} Ctor Nombre del constructor, por ejemplo "Magnetometer".
 * @param {(s: object) => void} onReading Qué hacer con cada lectura.
 */
function startGeneric(block, Ctor, onReading) {
  if (!(Ctor in window)) return;
  try {
    const s = new window[Ctor]({ frequency: 10 });
    s.addEventListener("reading", () => onReading(s));
    s.addEventListener("error", (e) => { errors[block] = e.error?.name === "NotAllowedError" ? "permiso denegado" : (e.error?.message ?? "error"); });
    s.start();
    generic.push(s);
  } catch (e) { errors[block] = e.message; }
}

/**
 * Arranca los bloques elegidos. Debe ejecutarse dentro del clic que inicia el agente (permisos de iOS).
 * @param {{lin?: boolean, grv?: boolean, gyr?: boolean, mfl?: boolean, ori?: boolean, lux?: boolean}} selected
 */
export async function start(selected) {
  stop();
  want = selected;
  if (selected.ori && typeof DeviceOrientationEvent?.requestPermission === "function") {
    try { if ((await DeviceOrientationEvent.requestPermission()) !== "granted") errors.ori = "permiso denegado"; }
    catch { errors.ori = "permiso denegado"; }
  }
  if (selected.lin || selected.grv || selected.gyr) addEventListener("devicemotion", onMotion);
  // La orientación absoluta (referida al norte) se prefiere cuando existe.
  if (selected.ori) addEventListener("ondeviceorientationabsolute" in window ? "deviceorientationabsolute" : "deviceorientation", onOrientation);
  if (selected.mfl) startGeneric("mfl", "Magnetometer", (s) => add("mfl", s.x, s.y, s.z));
  if (selected.lux) startGeneric("lux", "AmbientLightSensor", (s) => { lux = s.illuminance; });
  started = true;
}

/** Detiene todos los sensores y borra los datos acumulados. */
export function stop() {
  removeEventListener("devicemotion", onMotion);
  removeEventListener("deviceorientationabsolute", onOrientation);
  removeEventListener("deviceorientation", onOrientation);
  for (const s of generic.splice(0)) try { s.stop(); } catch { /* ya estaba detenido */ }
  for (const k of Object.keys(acc)) delete acc[k];
  orientation = null; lux = null; started = false;
}

/** Estado legible de un bloque para la página del agente. */
export function status(block) {
  if (errors[block]) return errors[block];
  if (!started) return "apagado";
  const has = block === "ori" ? orientation : block === "lux" ? lux != null : acc[block]?.n;
  return has ? "recibiendo" : "sin datos (¿escritorio?)";
}

/** Bloques de la carga con datos desde la llamada anterior: { lin, grv, gyr, mfl, ori, env }. Vacía los acumulados. */
export function read() {
  const out = {};
  for (const [block, a] of Object.entries(acc)) {
    if (!a.n) continue;
    out[block] = { x: round(a.x / a.n, 3), y: round(a.y / a.n, 3), z: round(a.z / a.n, 3) };
    delete acc[block];
  }
  if (orientation) out.ori = orientation;
  if (lux != null) out.env = { lux: round(lux, 1) };
  return out;
}
