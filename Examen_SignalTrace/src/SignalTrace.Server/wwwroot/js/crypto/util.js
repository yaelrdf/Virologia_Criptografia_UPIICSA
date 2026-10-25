// Byte helpers shared by the web client (browser and Node >= 20).
export const enc = new TextEncoder();
export const dec = new TextDecoder();
export const utf8 = (s) => enc.encode(s);

export const toHex = (u) => Array.from(u, (b) => b.toString(16).padStart(2, "0")).join("");
export const fromHex = (h) => Uint8Array.from(h.match(/../g) ?? [], (b) => parseInt(b, 16));

export function toB64(u) {
  let s = "";
  for (let i = 0; i < u.length; i += 0x8000) s += String.fromCharCode(...u.subarray(i, i + 0x8000));
  return btoa(s);
}
export const fromB64 = (b) => Uint8Array.from(atob(b), (c) => c.charCodeAt(0));

export function concat(...parts) {
  const out = new Uint8Array(parts.reduce((n, p) => n + p.length, 0));
  let o = 0;
  for (const p of parts) { out.set(p, o); o += p.length; }
  return out;
}

export function u64be(n) {
  const b = new Uint8Array(8);
  new DataView(b.buffer).setBigUint64(0, BigInt(n), false);
  return b;
}

export const randomBytes = (n) => crypto.getRandomValues(new Uint8Array(n));

/** Shannon entropy in bits per byte. */
export function entropy(u) {
  if (!u.length) return 0;
  const c = new Array(256).fill(0);
  for (const b of u) c[b]++;
  let h = 0;
  for (const x of c) if (x) { const p = x / u.length; h -= p * Math.log2(p); }
  return h;
}
