// YCM1 reference (ES module). Educational cipher: ALWAYS wrap with AES-GCM.
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
const pad = (d) => { const n = 2 - (d.length % 2); const o = new Uint8Array(d.length + n); o.set(d); o.fill(n, d.length); return o; };
const unpad = (p) => { const n = p[p.length - 1]; if (n !== 1 && n !== 2) throw new Error("bad padding");
  for (let i = p.length - n; i < p.length; i++) if (p[i] !== n) throw new Error("bad padding"); return p.slice(0, p.length - n); };
const rotl = (p, r) => { r %= p.length; return Uint8Array.from([...p.slice(r), ...p.slice(0, r)]); };
const rotr = (p, r) => { r %= p.length; return r ? Uint8Array.from([...p.slice(p.length - r), ...p.slice(0, p.length - r)]) : p.slice(); };
export async function encrypt(plain, kSig, seq, rounds = 3) {
  const { seed, prm } = await deriveParams(kSig, seq, rounds);
  let p = pad(plain); const n = p.length; let s = seed;
  for (let r = 0; r < rounds; r++) {
    const { k, m, rot } = prm[r];
    for (let i = 0; i < n; i++) { s = (5 * s + 3) & 255; p[i] = (p[i] + s) & 255; }
    for (let i = 0; i < n; i += 2) { const a = p[i], b = p[i + 1];
      p[i] = (a + k * b) & 255; p[i + 1] = (m * a + (1 + k * m) * b) & 255; }
    p = rotl(p, rot);
  }
  return p;
}
export async function decrypt(ct, kSig, seq, rounds = 3) {
  const { seed, prm } = await deriveParams(kSig, seq, rounds);
  const n = ct.length; let s = seed; const stream = [];
  for (let r = 0; r < rounds; r++) { const row = new Uint8Array(n); for (let i = 0; i < n; i++) { s = (5 * s + 3) & 255; row[i] = s; } stream.push(row); }
  let p = ct.slice();
  for (let r = rounds - 1; r >= 0; r--) {
    const { k, m, rot } = prm[r]; p = rotr(p, rot);
    for (let i = 0; i < n; i += 2) { const c0 = p[i], c1 = p[i + 1];
      p[i] = ((1 + k * m) * c0 - k * c1) & 255; p[i + 1] = (-m * c0 + c1) & 255; }
    for (let i = 0; i < n; i++) p[i] = (p[i] - stream[r][i]) & 255;
  }
  return unpad(p);
}
