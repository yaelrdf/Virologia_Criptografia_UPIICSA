// SignalTrace v1 client protocol over Web Crypto (PLAN §6.3–6.4). Mirrors SignalTrace.Core in C#.
import * as sigmix from "./sigmix.js";
import { concat, fromB64, toB64, toHex, u64be, utf8, randomBytes } from "./util.js";

const subtle = crypto.subtle;
export const SUITE_AES = "aes-256-gcm";
export const SUITE_CHACHA = "chacha20-poly1305";
const EC = { name: "ECDH", namedCurve: "P-256" };

let chachaImpl = null;
/** ChaCha20-Poly1305 is not in Web Crypto: it is loaded on demand from the local copy of @noble/ciphers. */
async function chacha() {
  if (!chachaImpl) {
    const mod = await import(new URL("../../lib/noble-ciphers/chacha.js", import.meta.url).href);
    chachaImpl = mod.chacha20poly1305;
  }
  return chachaImpl;
}
export function setChaChaImplementation(fn) { chachaImpl = fn; }

export async function fingerprint(publicKey) {
  return toHex(new Uint8Array(await subtle.digest("SHA-256", publicKey)));
}

export function transcript(pubC, pubS, nonceC, nonceS, deviceId, sessionId, suite = SUITE_AES) {
  const t = concat(utf8("ST1-HS"), pubC, pubS, nonceC, nonceS, utf8(deviceId), utf8(sessionId));
  return suite === SUITE_AES ? t : concat(t, utf8("|" + suite));
}

export async function hkdf(z, salt, info, len) {
  const ikm = await subtle.importKey("raw", z, "HKDF", false, ["deriveBits"]);
  return new Uint8Array(await subtle.deriveBits({ name: "HKDF", hash: "SHA-256", salt, info: utf8(info) }, ikm, len * 8));
}

export async function deriveKeys(z, nonceC, nonceS, suite = SUITE_AES) {
  const salt = concat(nonceC, nonceS);
  return {
    suite,
    kAead: await hkdf(z, salt, suite === SUITE_CHACHA ? "st1/c20-up" : "st1/aes-up", 32),
    kSig: await hkdf(z, salt, "st1/sigmix", 32),
    ivSalt: await hkdf(z, salt, "st1/iv-up", 4),
  };
}

/** Step 1: ephemeral ECDH P-256 key pair and a 16-byte nonce. Returns the request body and the private state. */
export async function beginHandshake(deviceId, suite = SUITE_AES) {
  const pair = await subtle.generateKey(EC, false, ["deriveBits"]);
  const pub = new Uint8Array(await subtle.exportKey("raw", pair.publicKey));
  const nonce = randomBytes(16);
  return {
    state: { pair, pub, nonce, deviceId, suite },
    request: { deviceId, clientPublicKey: toB64(pub), clientNonce: toB64(nonce), suite },
  };
}

/** Step 2: verify the server's ECDSA signature with the pinned key (abort on failure), then ECDH + HKDF. */
export async function completeHandshake(state, resp, pinnedServerKey) {
  const pubS = fromB64(resp.serverPublicKey);
  const nonceS = fromB64(resp.serverNonce);
  const suite = resp.suite || SUITE_AES;
  const t = transcript(state.pub, pubS, state.nonce, nonceS, state.deviceId, resp.sessionId, suite);
  const verifyKey = await subtle.importKey("raw", pinnedServerKey, { name: "ECDSA", namedCurve: "P-256" }, false, ["verify"]);
  const ok = await subtle.verify({ name: "ECDSA", hash: "SHA-256" }, verifyKey, fromB64(resp.signature), t);
  if (!ok) throw new Error("La firma del servidor no coincide con la llave fijada (posible MITM)");
  const peer = await subtle.importKey("raw", pubS, EC, false, []);
  const z = new Uint8Array(await subtle.deriveBits({ name: "ECDH", public: peer }, state.pair.privateKey, 256));
  return { sid: resp.sessionId, did: state.deviceId, expiresAt: resp.expiresAt, keys: await deriveKeys(z, state.nonce, nonceS, suite) };
}

export const nonceFor = (ivSalt, seq) => concat(ivSalt, u64be(seq));
export const aadFor = (deviceId, seq, ts) => concat(utf8("ST1"), utf8(deviceId), u64be(seq), u64be(ts));

async function aeadEncrypt(keys, nonce, data, aad) {
  if (keys.suite === SUITE_CHACHA) return (await chacha())(keys.kAead, nonce, aad).encrypt(data);
  const k = await subtle.importKey("raw", keys.kAead, "AES-GCM", false, ["encrypt"]);
  return new Uint8Array(await subtle.encrypt({ name: "AES-GCM", iv: nonce, additionalData: aad, tagLength: 128 }, k, data));
}

async function aeadDecrypt(keys, nonce, data, aad) {
  if (keys.suite === SUITE_CHACHA) return (await chacha())(keys.kAead, nonce, aad).decrypt(data);
  const k = await subtle.importKey("raw", keys.kAead, "AES-GCM", false, ["decrypt"]);
  return new Uint8Array(await subtle.decrypt({ name: "AES-GCM", iv: nonce, additionalData: aad, tagLength: 128 }, k, data));
}

/** sm = SigMix(pt, k_sig, seq); ct||tag = AEAD(k, iv_salt||u64be(seq), sm, "ST1"||did||seq||ts). */
export async function seal(session, seq, ts, plaintext, trace = null) {
  const sm = await sigmix.encrypt(plaintext, session.keys.kSig, seq, 3, trace);
  return aeadEncrypt(session.keys, nonceFor(session.keys.ivSalt, seq), sm, aadFor(session.did, seq, ts));
}

export async function open(session, seq, ts, ct) {
  const sm = await aeadDecrypt(session.keys, nonceFor(session.keys.ivSalt, seq), ct, aadFor(session.did, seq, ts));
  return sigmix.decrypt(sm, session.keys.kSig, seq);
}

/** Builds the wire envelope for POST /api/telemetry. */
export async function envelope(session, seq, ts, payloadObject) {
  const ct = await seal(session, seq, ts, utf8(JSON.stringify(payloadObject)));
  return { v: 1, sid: session.sid, did: session.did, seq, ts, ct: toB64(ct) };
}
