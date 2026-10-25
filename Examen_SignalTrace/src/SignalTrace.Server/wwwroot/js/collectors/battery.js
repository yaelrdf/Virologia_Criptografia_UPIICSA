// Battery Status API (Chrome/Android only; absent in Safari and Firefox → the field is omitted).
let battery = null;
export async function start() { try { battery = navigator.getBattery ? await navigator.getBattery() : null; } catch { battery = null; } }
export function status() { return battery ? `${Math.round(battery.level * 100)} %${battery.charging ? " (cargando)" : ""}` : "no disponible"; }
export function read() { return battery ? { lvl: Math.round(battery.level * 100) / 100, chg: battery.charging } : undefined; }
