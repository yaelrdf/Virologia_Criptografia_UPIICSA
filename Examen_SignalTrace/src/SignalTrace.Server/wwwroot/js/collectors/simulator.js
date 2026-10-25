// Synthetic sources so the system can be demonstrated on a desktop or without sensors:
// GPS random walk around UPIICSA, 5 Hz vibration (sine + noise) at 32 Hz, draining battery and a synthetic RSSI.
import { round } from "./geo.js";
import * as motion from "./motion.js";

const state = { lat: 19.3955, lon: -99.0915, hdg: 45, bat: 0.93, rssi: -58, t: 0 };
let timer = null;
const gauss = () => Math.sqrt(-2 * Math.log(Math.random() || 1e-9)) * Math.cos(2 * Math.PI * Math.random());

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

export function step(dtSec) {
  state.hdg = (state.hdg + 20 * gauss() + 360) % 360;
  const spd = Math.max(0, 1.3 + 0.3 * gauss());           // walking, m/s
  const d = spd * dtSec;
  state.lat += (d * Math.cos(state.hdg * Math.PI / 180)) / 111_320;
  state.lon += (d * Math.sin(state.hdg * Math.PI / 180)) / (111_320 * Math.cos(state.lat * Math.PI / 180));
  state.bat = Math.max(0.05, state.bat - 0.0004 * dtSec);
  state.rssi = Math.max(-95, Math.min(-35, state.rssi + 1.5 * gauss()));
  return {
    gps: { lat: round(state.lat, 6), lon: round(state.lon, 6), acc: round(5 + 3 * Math.random(), 1), spd: round(spd, 2), hdg: Math.round(state.hdg) },
    bat: { lvl: round(state.bat, 3), chg: false },
    wifi: { rssi: Math.round(state.rssi), freq: 5180, link: 433 },
  };
}
