// Protocolo de cliente SignalTrace v1 sobre Web Crypto (PLAN §6.3–6.4). Es el equivalente de SignalTrace.Core en C#:
// handshake ECDH P-256 con firma ECDSA del servidor, derivación HKDF-SHA256 y sellado de paquetes YCM1 + AEAD.
import * as ycm1 from "./ycm1.js";
import { concat, fromB64, toB64, toHex, u64be, utf8, randomBytes } from "./util.js";

const subtle = crypto.subtle;
/** Nombre de la suite AES-256-GCM. */
export const SUITE_AES = "aes-256-gcm";
/** Nombre de la suite ChaCha20-Poly1305. */
export const SUITE_CHACHA = "chacha20-poly1305";
/** Algoritmo de las llaves efímeras. */
const EC = { name: "ECDH", namedCurve: "P-256" };

/** Implementación de ChaCha20-Poly1305 cargada (o inyectada en las pruebas). */
let chachaImpl = null;
/** ChaCha20-Poly1305 no está en Web Crypto: se carga cuando se necesita desde la copia local de @noble/ciphers. */
async function chacha() {
  if (!chachaImpl) {
    const mod = await import(new URL("../../lib/noble-ciphers/chacha.js", import.meta.url).href);
    chachaImpl = mod.chacha20poly1305;
  }
  return chachaImpl;
}
/** Permite inyectar otra implementación de ChaCha20-Poly1305 (las pruebas en Node lo usan). */
export function setChaChaImplementation(fn) { chachaImpl = fn; }

/** Huella de una llave pública: hex(SHA-256(punto de 65 bytes)). */
export async function fingerprint(publicKey) {
  return toHex(new Uint8Array(await subtle.digest("SHA-256", publicKey)));
}

/**
 * Transcript que firma el servidor: "ST1-HS" ‖ llave del cliente ‖ llave del servidor ‖ nonceC ‖ nonceS ‖ dispositivo ‖ sesión.
 * Una suite distinta de la predeterminada se agrega al final para que la firma también la proteja.
 */
export function transcript(pubC, pubS, nonceC, nonceS, deviceId, sessionId, suite = SUITE_AES) {
  const t = concat(utf8("ST1-HS"), pubC, pubS, nonceC, nonceS, utf8(deviceId), utf8(sessionId));
  return suite === SUITE_AES ? t : concat(t, utf8("|" + suite));
}

/**
 * HKDF-SHA256 con Web Crypto.
 * @param {Uint8Array} z Material de entrada (secreto compartido).
 * @param {Uint8Array} salt Sal.
 * @param {string} info Etiqueta de contexto.
 * @param {number} len Bytes de salida.
 */
export async function hkdf(z, salt, info, len) {
  const ikm = await subtle.importKey("raw", z, "HKDF", false, ["deriveBits"]);
  return new Uint8Array(await subtle.deriveBits({ name: "HKDF", hash: "SHA-256", salt, info: utf8(info) }, ikm, len * 8));
}

/** Deriva las llaves de sesión (AEAD, YCM1 y sal del nonce) con sal = nonceC ‖ nonceS, igual que el servidor. */
export async function deriveKeys(z, nonceC, nonceS, suite = SUITE_AES) {
  const salt = concat(nonceC, nonceS);
  return {
    suite,
    kAead: await hkdf(z, salt, suite === SUITE_CHACHA ? "st1/c20-up" : "st1/aes-up", 32),
    kSig: await hkdf(z, salt, "st1/ycm1", 32),
    ivSalt: await hkdf(z, salt, "st1/iv-up", 4),
  };
}

/**
 * Paso 1: par de llaves efímeras ECDH P-256 (la privada no se puede exportar) y un nonce de 16 bytes.
 * @returns {Promise<{state: object, request: object}>} El estado privado y el cuerpo de POST /api/handshake.
 */
export async function beginHandshake(deviceId, suite = SUITE_AES) {
  const pair = await subtle.generateKey(EC, false, ["deriveBits"]);
  const pub = new Uint8Array(await subtle.exportKey("raw", pair.publicKey));
  const nonce = randomBytes(16);
  return {
    state: { pair, pub, nonce, deviceId, suite },
    request: { deviceId, clientPublicKey: toB64(pub), clientNonce: toB64(nonce), suite },
  };
}

/**
 * Paso 2: verifica la firma ECDSA del servidor con la llave fijada (y aborta si falla); después ECDH + HKDF.
 * @param {object} state Estado devuelto por beginHandshake.
 * @param {object} resp Respuesta del servidor.
 * @param {Uint8Array} pinnedServerKey Llave pública de largo plazo fijada.
 * @returns {Promise<{sid: string, did: string, expiresAt: number, keys: object}>} La sesión lista para sellar paquetes.
 * @throws {Error} Si la firma no coincide (posible intermediario).
 */
export async function completeHandshake(state, resp, pinnedServerKey) {
  const pubS = fromB64(resp.serverPublicKey);
  const nonceS = fromB64(resp.serverNonce);
  const suite = resp.suite || SUITE_AES;
  const t = transcript(state.pub, pubS, state.nonce, nonceS, state.deviceId, resp.sessionId, suite);
  const verifyKey = await subtle.importKey("raw", pinnedServerKey, { name: "ECDSA", namedCurve: "P-256" }, false, ["verify"]);
  const ok = await subtle.verify({ name: "ECDSA", hash: "SHA-256" }, verifyKey, fromB64(resp.signature), t);
  if (!ok) throw new Error("La firma del servidor no coincide con la llave fijada (posible MITM)");
  // deriveBits con 256 bits devuelve la coordenada X del punto compartido, como el servidor.
  const peer = await subtle.importKey("raw", pubS, EC, false, []);
  const z = new Uint8Array(await subtle.deriveBits({ name: "ECDH", public: peer }, state.pair.privateKey, 256));
  return { sid: resp.sessionId, did: state.deviceId, expiresAt: resp.expiresAt, keys: await deriveKeys(z, state.nonce, nonceS, suite) };
}

/** Nonce AEAD de 12 bytes: sal de la sesión ‖ u64be(seq). */
export const nonceFor = (ivSalt, seq) => concat(ivSalt, u64be(seq));
/** Datos asociados: "ST1" ‖ dispositivo ‖ u64be(seq) ‖ u64be(ts). */
export const aadFor = (deviceId, seq, ts) => concat(utf8("ST1"), utf8(deviceId), u64be(seq), u64be(ts));

/** Cifra con la suite de la sesión; devuelve texto cifrado ‖ etiqueta de 16 bytes. */
async function aeadEncrypt(keys, nonce, data, aad) {
  if (keys.suite === SUITE_CHACHA) return (await chacha())(keys.kAead, nonce, aad).encrypt(data);
  const k = await subtle.importKey("raw", keys.kAead, "AES-GCM", false, ["encrypt"]);
  return new Uint8Array(await subtle.encrypt({ name: "AES-GCM", iv: nonce, additionalData: aad, tagLength: 128 }, k, data));
}

/** Verifica la etiqueta y descifra con la suite de la sesión; lanza un error si la etiqueta no coincide. */
async function aeadDecrypt(keys, nonce, data, aad) {
  if (keys.suite === SUITE_CHACHA) return (await chacha())(keys.kAead, nonce, aad).decrypt(data);
  const k = await subtle.importKey("raw", keys.kAead, "AES-GCM", false, ["decrypt"]);
  return new Uint8Array(await subtle.decrypt({ name: "AES-GCM", iv: nonce, additionalData: aad, tagLength: 128 }, k, data));
}

/**
 * Sella un paquete: sm = Yael Crypto March1(pt, k_sig, seq); ct ‖ tag = AEAD(k, iv_salt ‖ u64be(seq), sm, "ST1" ‖ did ‖ seq ‖ ts).
 * @param {object} session Sesión de completeHandshake.
 * @param {number} seq Número de secuencia.
 * @param {number} ts Marca de tiempo en ms.
 * @param {Uint8Array} plaintext JSON en UTF-8.
 * @param {Function|null} [trace] Recibe las etapas de YCM1.
 */
export async function seal(session, seq, ts, plaintext, trace = null) {
  const sm = await ycm1.encrypt(plaintext, session.keys.kSig, seq, 3, trace);
  return aeadEncrypt(session.keys, nonceFor(session.keys.ivSalt, seq), sm, aadFor(session.did, seq, ts));
}

/** Abre un paquete sellado: AEAD primero y después la inversa de YCM1. */
export async function open(session, seq, ts, ct) {
  const sm = await aeadDecrypt(session.keys, nonceFor(session.keys.ivSalt, seq), ct, aadFor(session.did, seq, ts));
  return ycm1.decrypt(sm, session.keys.kSig, seq);
}

/** Arma el sobre que se transmite a POST /api/telemetry: { v, sid, did, seq, ts, ct }. */
export async function envelope(session, seq, ts, payloadObject) {
  const ct = await seal(session, seq, ts, utf8(JSON.stringify(payloadObject)));
  return { v: 1, sid: session.sid, did: session.did, seq, ts, ct: toB64(ct) };
}
