// GPS via the Geolocation API. Works only with the page visible and in a secure context (HTTPS or localhost).
let watchId = null, last = null, error = null;

export function start() {
  if (!("geolocation" in navigator)) { error = "Geolocalización no disponible"; return; }
  watchId = navigator.geolocation.watchPosition(
    (p) => { last = p; error = null; },
    (e) => { error = e.message || "Permiso de ubicación denegado"; },
    { enableHighAccuracy: true, maximumAge: 2000, timeout: 20000 });
}

export function stop() { if (watchId != null) navigator.geolocation.clearWatch(watchId); watchId = null; }

export function status() { return error ?? (last ? `±${Math.round(last.coords.accuracy)} m` : "esperando señal"); }

/** Returns the gps object of the payload (only fields that exist), or undefined. */
export function read() {
  if (!last || Date.now() - last.timestamp > 30000) return undefined;
  const c = last.coords;
  const g = { lat: round(c.latitude, 6), lon: round(c.longitude, 6), acc: round(c.accuracy, 1) };
  if (c.altitude != null) g.alt = round(c.altitude, 1);
  if (c.speed != null && !Number.isNaN(c.speed)) g.spd = round(c.speed, 2);
  if (c.heading != null && !Number.isNaN(c.heading)) g.hdg = round(c.heading, 0);
  return g;
}

export const round = (v, d) => Math.round(v * 10 ** d) / 10 ** d;
