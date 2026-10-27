// Synthetic sources so the system can be demonstrated on a desktop or without sensors: the whole sensor bank.
// 5 Hz vibration (sine + noise) at 32 Hz, a slowly turning orientation, light, pressure, temperature, humidity,
// steps, draining battery and synthetic Wi-Fi / cellular signal.
import { round } from "./util.js";
import * as motion from "./motion.js";

const state = { az: 45, lux: 320, hpa: 779.4, tmp: 23.5, hum: 44, steps: 0, bat: 0.93, rssi: -58, dbm: -92, t: 0 };
let timer = null;
const gauss = () => Math.sqrt(-2 * Math.log(Math.random() || 1e-9)) * Math.cos(2 * Math.PI * Math.random());
const vec = (x, y, z) => ({ x: round(x, 3), y: round(y, 3), z: round(z, 3) });

export function start() {
  stop();
  // Feed the motion collector at 32 Hz exactly like a real accelerometer.
  timer = setInterval(() => {
    state.t += 1 / motion.FS;
    const vib = 0.6 * Math.sin(2 * Math.PI * 5 * state.t) + 0.08 * gauss();
    motion.push(0.05 * gauss(), 0.05 * gauss(), 9.81 + vib, 9.81 + vib);
  }, 1000 / motion.FS);
}
export function stop() { clearInterval(timer); timer = null; }

/** One interval of every simulated block (the agent keeps only the sensors the user selected). */
export function step(dtSec) {
  state.az = (state.az + 6 * gauss() + 360) % 360;
  state.lux = Math.min(2000, Math.max(0, state.lux + 20 * gauss() + 0.05 * (320 - state.lux)));
  state.hpa += 0.01 * gauss() + 0.01 * (779.4 - state.hpa);
  state.tmp += 0.02 * gauss() + 0.01 * (23.5 - state.tmp);
  state.hum = Math.min(100, Math.max(0, state.hum + 0.2 * gauss()));
  state.steps += Math.max(0, Math.round(1.8 * dtSec + gauss()));
  state.bat = Math.max(0.05, state.bat - 0.0004 * dtSec);
  state.rssi = Math.max(-95, Math.min(-35, state.rssi + 1.5 * gauss()));
  state.dbm = Math.max(-120, Math.min(-60, state.dbm + 1.2 * gauss()));
  const rad = state.az * Math.PI / 180;
  return {
    lin: vec(0.2 * gauss(), 0.2 * gauss(), 0.3 * gauss()),
    grv: vec(0.05, -0.02, 9.80),
    gyr: vec(0.02 * gauss(), 0.02 * gauss(), 0.08 * gauss()),
    mfl: vec(22 * Math.cos(rad), -22 * Math.sin(rad), -38 + gauss()),
    ori: { az: round(state.az, 1), pi: round(-3 + gauss(), 1), ro: round(1 + gauss(), 1) },
    env: { lux: Math.round(state.lux), prx: 5, hpa: round(state.hpa, 2), tmp: round(state.tmp, 1), hum: round(state.hum, 1) },
    stp: { n: state.steps },
    bat: { lvl: round(state.bat, 3), chg: false, temp: round(30 + gauss() * 0.2, 1), volt: round(3.6 + 0.5 * state.bat, 3) },
    wifi: { rssi: Math.round(state.rssi), freq: 5180, link: 433, tx: 433, rx: 390 },
    cell: { type: "lte", dbm: Math.round(state.dbm), level: Math.max(0, Math.min(4, Math.round((state.dbm + 120) / 15))), rsrp: Math.round(state.dbm), rsrq: -10, snr: 12 },
  };
}
