// Independent check of reference/test-vectors.json with Web Crypto (Node >= 20).
// Usage: node reference/verify.mjs
import { readFileSync } from "node:fs";
import { webcrypto as wc } from "node:crypto";
import * as sigmix from "./sigmix.js";

const subtle = wc.subtle;
const V = JSON.parse(readFileSync(new URL("./test-vectors.json", import.meta.url)));
const hex = (h) => Uint8Array.from(h.match(/../g) ?? [], (b) => parseInt(b, 16));
const toHex = (u) => Buffer.from(u).toString("hex");
const b64u = (u) => Buffer.from(u).toString("base64url");
const cat = (...a) => { const o = new Uint8Array(a.reduce((n, x) => n + x.length, 0)); let i = 0; for (const x of a) { o.set(x, i); i += x.length; } return o; };
const utf8 = (s) => new TextEncoder().encode(s);
const u64 = (n) => { const b = new Uint8Array(8); new DataView(b.buffer).setBigUint64(0, BigInt(n), false); return b; };

let failures = 0;
function check(name, got, want) {
  const ok = got === want;
  if (!ok) failures++;
  console.log(`${ok ? "OK  " : "FAIL"} ${name}${ok ? "" : `\n     got  ${got}\n     want ${want}`}`);
}

function jwk(priv, pub, alg) {
  const p = hex(pub);
  return { kty: "EC", crv: "P-256", d: b64u(hex(priv)), x: b64u(p.slice(1, 33)), y: b64u(p.slice(33)), ext: true, ...(alg ? { alg } : {}) };
}

const hs = V.handshake;
// ECDH: client private x server public must equal server private x client public.
const cPriv = await subtle.importKey("jwk", jwk(hs.client_ephemeral_private, hs.client_ephemeral_public), { name: "ECDH", namedCurve: "P-256" }, false, ["deriveBits"]);
const sPub = await subtle.importKey("raw", hex(hs.server_ephemeral_public), { name: "ECDH", namedCurve: "P-256" }, false, []);
const z = new Uint8Array(await subtle.deriveBits({ name: "ECDH", public: sPub }, cPriv, 256));
check("ECDH shared_secret_x", toHex(z), hs.shared_secret_x);

const salt = cat(hex(hs.client_nonce), hex(hs.server_nonce));
const ikm = await subtle.importKey("raw", z, "HKDF", false, ["deriveBits"]);
for (const [label, want] of Object.entries(hs.hkdf.outputs)) {
  const [, info, len] = label.match(/^(.*)\((\d+)\)$/);
  const out = new Uint8Array(await subtle.deriveBits({ name: "HKDF", hash: "SHA-256", salt, info: utf8(info) }, ikm, Number(len) * 8));
  check(`HKDF ${label}`, toHex(out), want);
}

const transcript = cat(utf8("ST1-HS"), hex(hs.client_ephemeral_public), hex(hs.server_ephemeral_public), hex(hs.client_nonce), hex(hs.server_nonce), utf8(hs.device_id), utf8(hs.session_id));
check("transcript", toHex(transcript), hs.transcript);
const ltPub = await subtle.importKey("raw", hex(hs.server_longterm_public), { name: "ECDSA", namedCurve: "P-256" }, false, ["verify"]);
const sigOk = await subtle.verify({ name: "ECDSA", hash: "SHA-256" }, ltPub, hex(hs.server_signature_p1363_VERIFY_ONLY), transcript);
check("ECDSA signature verifies", String(sigOk), "true");

const pk = V.packet;
const kSig = hex(hs.hkdf.outputs["st1/sigmix(32)"]);
const { seed, prm } = await sigmix.deriveParams(kSig, pk.seq);
check("SigMix params", JSON.stringify({ seed, rounds: prm }), JSON.stringify(pk.sigmix_params));
const sm = await sigmix.encrypt(hex(pk.plaintext_hex), kSig, pk.seq);
check("SigMix output", toHex(sm), pk.sigmix_output);
check("SigMix decrypt", toHex(await sigmix.decrypt(sm, kSig, pk.seq)), pk.plaintext_hex);

const aesKey = await subtle.importKey("raw", hex(pk.aes_gcm.key), "AES-GCM", false, ["encrypt"]);
const nonce = cat(hex(hs.hkdf.outputs["st1/iv-up(4)"]), u64(pk.seq));
const aad = cat(utf8("ST1"), utf8(hs.device_id), u64(pk.seq), u64(pk.ts_ms));
check("AES-GCM nonce", toHex(nonce), pk.aes_gcm["nonce(12)=iv_salt||u64be(seq)"]);
check("AES-GCM aad", toHex(aad), pk.aes_gcm["aad = 'ST1'||utf8(device_id)||u64be(seq)||u64be(ts)"]);
const ct = new Uint8Array(await subtle.encrypt({ name: "AES-GCM", iv: nonce, additionalData: aad, tagLength: 128 }, aesKey, sm));
check("AES-GCM ciphertext||tag", toHex(ct), pk.aes_gcm.ciphertext_plus_tag);

const pb = V.pbkdf2;
const pw = await subtle.importKey("raw", utf8(pb.password), "PBKDF2", false, ["deriveBits"]);
const dk = new Uint8Array(await subtle.deriveBits({ name: "PBKDF2", hash: "SHA-256", salt: hex(pb.salt), iterations: pb.iterations }, pw, pb.dklen * 8));
check("PBKDF2", toHex(dk), pb.derived);

console.log(failures ? `\n${failures} check(s) failed` : "\nAll vectors verified");
process.exit(failures ? 1 : 0);
