// Utilidades de bytes que comparte el cliente web (en el navegador y en Node ≥ 20 para las pruebas).
/** Codificador UTF-8. */
export const enc = new TextEncoder();
/** Decodificador UTF-8. */
export const dec = new TextDecoder();
/** Texto → bytes UTF-8. */
export const utf8 = (s) => enc.encode(s);

/** Bytes → hexadecimal en minúsculas. */
export const toHex = (u) => Array.from(u, (b) => b.toString(16).padStart(2, "0")).join("");
/** Hexadecimal → bytes (toma los caracteres de dos en dos). */
export const fromHex = (h) => Uint8Array.from(h.match(/../g) ?? [], (b) => parseInt(b, 16));

/**
 * Bytes → Base64 estándar. Convierte en bloques de 32 KB porque pasar millones de argumentos a
 * String.fromCharCode desborda la pila.
 * @param {Uint8Array} u
 * @returns {string}
 */
export function toB64(u) {
  let s = "";
  for (let i = 0; i < u.length; i += 0x8000) s += String.fromCharCode(...u.subarray(i, i + 0x8000));
  return btoa(s);
}
/** Base64 → bytes. */
export const fromB64 = (b) => Uint8Array.from(atob(b), (c) => c.charCodeAt(0));

/** Une varios Uint8Array en uno nuevo, en el orden recibido. */
export function concat(...parts) {
  const out = new Uint8Array(parts.reduce((n, p) => n + p.length, 0));
  let o = 0;
  for (const p of parts) { out.set(p, o); o += p.length; }
  return out;
}

/**
 * Entero sin signo de 64 bits en 8 bytes big-endian (secuencia y marca de tiempo del nonce y el AAD).
 * Usa BigInt porque los números de JavaScript solo son exactos hasta 2^53.
 * @param {number|bigint} n
 */
export function u64be(n) {
  const b = new Uint8Array(8);
  new DataView(b.buffer).setBigUint64(0, BigInt(n), false);
  return b;
}

/** n bytes aleatorios criptográficamente seguros. */
export const randomBytes = (n) => crypto.getRandomValues(new Uint8Array(n));

/** Entropía de Shannon en bits por byte (0 a 8), igual que el servidor. */
export function entropy(u) {
  if (!u.length) return 0;
  const c = new Array(256).fill(0);
  for (const b of u) c[b]++;
  let h = 0;
  for (const x of c) if (x) { const p = x / u.length; h -= p * Math.log2(p); }
  return h;
}
