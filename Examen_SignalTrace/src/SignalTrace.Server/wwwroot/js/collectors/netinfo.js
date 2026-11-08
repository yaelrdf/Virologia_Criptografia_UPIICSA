// Network Information API (Chrome/Android). El navegador redondea downlink y rtt (para evitar la huella digital);
// NO existe una API web para el RSSI del Wi-Fi ni para la señal celular. La calidad real del enlace la mide probe.js.
/** Texto de estado: tipo de conexión y tipo efectivo, por ejemplo "wifi / 4g". */
export function status() {
  const c = navigator.connection;
  return c ? `${c.type ?? "?"} / ${c.effectiveType ?? "?"}` : "no disponible";
}
/**
 * Bloque net de la carga con los campos que el navegador ofrezca, más el resultado del sondeo si lo hay.
 * @param {object} [probe] Resultado de probe.read().
 * @returns {object|undefined} El bloque, o undefined si no hay ningún dato.
 */
export function read(probe) {
  const c = navigator.connection;
  const net = {};
  if (c?.type) net.type = c.type;
  if (c?.effectiveType) net.eff = c.effectiveType;
  if (typeof c?.rtt === "number") net.rtt = c.rtt;
  if (typeof c?.downlink === "number") net.down = c.downlink;
  if (probe) net.probe = probe;
  return Object.keys(net).length ? net : undefined;
}
