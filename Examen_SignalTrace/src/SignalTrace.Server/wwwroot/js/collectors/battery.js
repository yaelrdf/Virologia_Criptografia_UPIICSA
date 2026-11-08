// Battery Status API (solo Chrome/Android; no existe en Safari ni Firefox → el campo se omite).
/** Objeto BatteryManager del navegador, o null si no está disponible. */
let battery = null;
/** Obtiene el objeto de batería; si la API no existe o falla, queda en null. */
export async function start() { try { battery = navigator.getBattery ? await navigator.getBattery() : null; } catch { battery = null; } }
/** Texto de estado para la página del agente, por ejemplo "87 % (cargando)". */
export function status() { return battery ? `${Math.round(battery.level * 100)} %${battery.charging ? " (cargando)" : ""}` : "no disponible"; }
/** Bloque bat de la carga: nivel de 0 a 1 con dos decimales y si está cargando; undefined si no hay API. */
export function read() { return battery ? { lvl: Math.round(battery.level * 100) / 100, chg: battery.charging } : undefined; }
