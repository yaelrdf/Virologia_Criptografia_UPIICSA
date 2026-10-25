// Network Information API (Chrome/Android). downlink and rtt are capped by the browser (anti-fingerprinting);
// there is NO web API for Wi-Fi RSSI or cellular signal. Real link quality comes from probe.js.
export function status() {
  const c = navigator.connection;
  return c ? `${c.type ?? "?"} / ${c.effectiveType ?? "?"}` : "no disponible";
}
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
