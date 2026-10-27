// Yael Crypto March1 (invented educational cipher, PLAN §6.5). Same algorithm as reference/ycm1.js,
// plus an optional trace callback (stage name, bytes) used by the admin console and the lab.
// It is affine over Z_256: ALWAYS wrap it with an AEAD (AES-GCM / ChaCha20-Poly1305).
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

export const pad = (d) => { const n = 2 - (d.length % 2); const o = new Uint8Array(d.length + n); o.set(d); o.fill(n, d.length); return o; };
export const unpad = (p) => {
  const n = p[p.length - 1];
  if (!p.length || (n !== 1 && n !== 2) || p.length < n) throw new Error("bad padding");
  for (let i = p.length - n; i < p.length; i++) if (p[i] !== n) throw new Error("bad padding");
  return p.slice(0, p.length - n);
};
const rotl = (p, r) => { r %= p.length; return r ? Uint8Array.from([...p.slice(r), ...p.slice(0, r)]) : p.slice(); };
const rotr = (p, r) => { r %= p.length; return r ? Uint8Array.from([...p.slice(p.length - r), ...p.slice(0, p.length - r)]) : p.slice(); };

/** Encrypt with explicit parameters { seed, prm: [{k, m, rot}] }; usePad=false requires even length. */
export function encryptWithParams(plain, { seed, prm }, trace = null, usePad = true) {
  let p = usePad ? pad(plain) : Uint8Array.from(plain);
  if (p.length % 2) throw new Error("odd length");
  const n = p.length; let s = seed;
  trace?.("padded", p.slice());
  prm.forEach(({ k, m, rot }, r) => {
    for (let i = 0; i < n; i++) { s = (5 * s + 3) & 255; p[i] = (p[i] + s) & 255; }
    trace?.(`r${r + 1}.stream`, p.slice());
    for (let i = 0; i < n; i += 2) {
      const a = p[i], b = p[i + 1];
      p[i] = (a + k * b) & 255; p[i + 1] = (m * a + (1 + k * m) * b) & 255;
    }
    trace?.(`r${r + 1}.matrix`, p.slice());
    if (n) p = rotl(p, rot);
    trace?.(`r${r + 1}.rotate`, p.slice());
  });
  return p;
}

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

export async function encrypt(plain, kSig, seq, rounds = 3, trace = null) {
  return encryptWithParams(plain, await deriveParams(kSig, seq, rounds), trace);
}

export async function decrypt(ct, kSig, seq, rounds = 3) {
  return decryptWithParams(ct, await deriveParams(kSig, seq, rounds));
}
