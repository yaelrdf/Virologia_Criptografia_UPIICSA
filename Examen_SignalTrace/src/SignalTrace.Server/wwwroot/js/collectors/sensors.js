// Motion, orientation and environment sensors of the phone browser, beyond the accelerometer (motion.js):
// linear acceleration, gravity and gyroscope from DeviceMotionEvent, orientation from DeviceOrientationEvent,
// magnetometer and ambient light from the Generic Sensor API (Chrome; behind a flag in some versions).
// Each interval yields the mean of the samples received since the previous read.
import { round } from "./util.js";

const acc = {};             // block → { x, y, z, n }
let orientation = null;     // last { az, pi, ro }
let lux = null;             // last illuminance
const generic = [];         // running Generic Sensor objects
const errors = {};
let want = {};              // blocks selected by the user
let started = false;

/** Browser support of each block, before asking for any permission. */
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

function add(block, x, y, z) {
  if (![x, y, z].every(Number.isFinite)) return;
  const a = (acc[block] ??= { x: 0, y: 0, z: 0, n: 0 });
  a.x += x; a.y += y; a.z += z; a.n++;
}

function onMotion(e) {
  const g = e.accelerationIncludingGravity, a = e.acceleration, r = e.rotationRate;
  if (want.lin && a?.x != null) add("lin", a.x, a.y, a.z);
  if (want.grv && g?.x != null && a?.x != null) add("grv", g.x - a.x, g.y - a.y, g.z - a.z);
  // rotationRate is in °/s around z (alpha), x (beta) and y (gamma); the payload uses rad/s on x, y, z.
  if (want.gyr && r?.alpha != null) add("gyr", r.beta * Math.PI / 180, r.gamma * Math.PI / 180, r.alpha * Math.PI / 180);
}

function onOrientation(e) {
  if (e.alpha == null && e.beta == null) return;
  orientation = { az: round(((e.alpha ?? 0) + 360) % 360, 1), pi: round(e.beta ?? 0, 1), ro: round(e.gamma ?? 0, 1) };
}

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

/** Starts the selected blocks. Must run inside the click that starts the agent (iOS permissions). */
export async function start(selected) {
  stop();
  want = selected;
  if (selected.ori && typeof DeviceOrientationEvent?.requestPermission === "function") {
    try { if ((await DeviceOrientationEvent.requestPermission()) !== "granted") errors.ori = "permiso denegado"; }
    catch { errors.ori = "permiso denegado"; }
  }
  if (selected.lin || selected.grv || selected.gyr) addEventListener("devicemotion", onMotion);
  if (selected.ori) addEventListener("ondeviceorientationabsolute" in window ? "deviceorientationabsolute" : "deviceorientation", onOrientation);
  if (selected.mfl) startGeneric("mfl", "Magnetometer", (s) => add("mfl", s.x, s.y, s.z));
  if (selected.lux) startGeneric("lux", "AmbientLightSensor", (s) => { lux = s.illuminance; });
  started = true;
}

export function stop() {
  removeEventListener("devicemotion", onMotion);
  removeEventListener("deviceorientationabsolute", onOrientation);
  removeEventListener("deviceorientation", onOrientation);
  for (const s of generic.splice(0)) try { s.stop(); } catch { /* already stopped */ }
  for (const k of Object.keys(acc)) delete acc[k];
  orientation = null; lux = null; started = false;
}

/** Human-readable state of one block for the agent page. */
export function status(block) {
  if (errors[block]) return errors[block];
  if (!started) return "apagado";
  const has = block === "ori" ? orientation : block === "lux" ? lux != null : acc[block]?.n;
  return has ? "recibiendo" : "sin datos (¿escritorio?)";
}

/** Payload blocks with data since the last call: { lin, grv, gyr, mfl, ori, env }. */
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
