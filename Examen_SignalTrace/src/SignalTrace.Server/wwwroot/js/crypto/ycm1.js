// Yael Crypto March1 (cifrado didáctico inventado, PLAN §6.5). Es el mismo algoritmo que reference/ycm1.js,
// más una función opcional de traza (nombre de la etapa, bytes) que usan la consola del administrador y el laboratorio.
// Es afín sobre Z_256: SIEMPRE debe ir envuelto en un AEAD (AES-GCM o ChaCha20-Poly1305).

/**
 * Deriva los parámetros de un paquete: h = SHA-256(k_sig ‖ u64be(seq) ‖ "YCM1"); seed = h[0];
 * ronda i: k = h[1+3i], m = h[2+3i], rot = h[3+3i].
 * @param {Uint8Array} kSig Llave YCM1 de la sesión (32 bytes).
 * @param {number} seq Número de secuencia.
 * @param {number} [rounds] Número de rondas.
 * @returns {Promise<{seed: number, prm: {k: number, m: number, rot: number}[]}>}
 */
export async function deriveParams(kSig, seq, rounds = 3) {
  const buf = new Uint8Array(kSig.length + 8 + 4);
  buf.set(kSig, 0);
  new DataView(buf.buffer).setBigUint64(kSig.length, BigInt(seq), false);
  buf.set(new TextEncoder().encode("YCM1"), kSig.length + 8);
  const h = new Uint8Array(await crypto.subtle.digest("SHA-256", buf));
  const prm = [];
  for (let i = 0; i < rounds; i++) prm.push({ k: h[1 + 3 * i], m: h[2 + 3 * i], rot: h[3 + 3 * i] });
  return { seed: h[0], prm };
}

/** Relleno estilo PKCS#7 con bloque de 2: agrega n bytes de valor n (n = 1 o 2). Siempre agrega relleno. */
export const pad = (d) => { const n = 2 - (d.length % 2); const o = new Uint8Array(d.length + n); o.set(d); o.fill(n, d.length); return o; };
/** Quita y valida el relleno; lanza un error si no es válido. */
export const unpad = (p) => {
  const n = p[p.length - 1];
  if (!p.length || (n !== 1 && n !== 2) || p.length < n) throw new Error("bad padding");
  for (let i = p.length - n; i < p.length; i++) if (p[i] !== n) throw new Error("bad padding");
  return p.slice(0, p.length - n);
};
/** Rotación circular a la izquierda (siempre devuelve una copia). */
const rotl = (p, r) => { r %= p.length; return r ? Uint8Array.from([...p.slice(r), ...p.slice(0, r)]) : p.slice(); };
/** Rotación circular a la derecha, inversa de rotl. */
const rotr = (p, r) => { r %= p.length; return r ? Uint8Array.from([...p.slice(p.length - r), ...p.slice(0, p.length - r)]) : p.slice(); };

/**
 * Cifra con parámetros explícitos { seed, prm: [{k, m, rot}] }. Cada ronda: suma del flujo LCG s ← (5s+3) mod 256,
 * mezcla de cada par con la matriz [[1, k], [m, 1+k·m]] y rotación a la izquierda.
 * @param {Uint8Array} plain Texto claro.
 * @param {{seed: number, prm: object[]}} params Parámetros.
 * @param {((name: string, bytes: Uint8Array) => void)|null} [trace] Recibe una copia del estado tras cada etapa.
 * @param {boolean} [usePad] Si es false, no rellena y la entrada debe tener longitud par.
 * @returns {Uint8Array} Texto cifrado.
 */
export function encryptWithParams(plain, { seed, prm }, trace = null, usePad = true) {
  let p = usePad ? pad(plain) : Uint8Array.from(plain);
  if (p.length % 2) throw new Error("odd length");
  const n = p.length; let s = seed;
  trace?.("padded", p.slice());
  prm.forEach(({ k, m, rot }, r) => {
    // 1) Flujo de llave: el estado s continúa entre rondas.
    for (let i = 0; i < n; i++) { s = (5 * s + 3) & 255; p[i] = (p[i] + s) & 255; }
    trace?.(`r${r + 1}.stream`, p.slice());
    // 2) Matriz de determinante 1 sobre cada par de bytes.
    for (let i = 0; i < n; i += 2) {
      const a = p[i], b = p[i + 1];
      p[i] = (a + k * b) & 255; p[i + 1] = (m * a + (1 + k * m) * b) & 255;
    }
    trace?.(`r${r + 1}.matrix`, p.slice());
    // 3) Rotación circular.
    if (n) p = rotl(p, rot);
    trace?.(`r${r + 1}.rotate`, p.slice());
  });
  return p;
}

/**
 * Descifra con parámetros explícitos: regenera primero todo el flujo de llave y después deshace las rondas de la
 * última a la primera (rotación a la derecha, matriz inversa [[1+k·m, −k], [−m, 1]] y resta del flujo).
 * @param {Uint8Array} ct Texto cifrado (longitud par).
 * @param {{seed: number, prm: object[]}} params Los mismos parámetros del cifrado.
 * @param {boolean} [usePad] Si es true, quita y valida el relleno.
 * @returns {Uint8Array} Texto claro.
 */
export function decryptWithParams(ct, { seed, prm }, usePad = true) {
  const n = ct.length;
  if (n % 2 || (usePad && !n)) throw new Error("bad length");
  let s = seed; const stream = [];
  for (let r = 0; r < prm.length; r++) {
    const row = new Uint8Array(n);
    for (let i = 0; i < n; i++) { s = (5 * s + 3) & 255; row[i] = s; }
    stream.push(row);
  }
  let p = Uint8Array.from(ct);
  for (let r = prm.length - 1; r >= 0; r--) {
    const { k, m, rot } = prm[r];
    p = rotr(p, rot);
    for (let i = 0; i < n; i += 2) {
      const c0 = p[i], c1 = p[i + 1];
      p[i] = ((1 + k * m) * c0 - k * c1) & 255; p[i + 1] = (-m * c0 + c1) & 255;
    }
    for (let i = 0; i < n; i++) p[i] = (p[i] - stream[r][i]) & 255;
  }
  return usePad ? unpad(p) : p;
}

/** Cifra con los parámetros derivados de la llave y la secuencia. */
export async function encrypt(plain, kSig, seq, rounds = 3, trace = null) {
  return encryptWithParams(plain, await deriveParams(kSig, seq, rounds), trace);
}

/** Descifra con los parámetros derivados de la llave y la secuencia. */
export async function decrypt(ct, kSig, seq, rounds = 3) {
  return decryptWithParams(ct, await deriveParams(kSig, seq, rounds));
}
