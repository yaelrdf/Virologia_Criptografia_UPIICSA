// node --test tests/web : the browser crypto modules against reference/test-vectors.json.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync, existsSync } from "node:fs";
import * as sigmix from "../../src/SignalTrace.Server/wwwroot/js/crypto/sigmix.js";
import * as S from "../../src/SignalTrace.Server/wwwroot/js/crypto/session.js";
import { fromHex, toHex, toB64, utf8, randomBytes, entropy } from "../../src/SignalTrace.Server/wwwroot/js/crypto/util.js";

const V = JSON.parse(readFileSync(new URL("../../reference/test-vectors.json", import.meta.url)));
const hs = V.handshake, pk = V.packet;
const out = (l) => hs.hkdf.outputs[l];

test("SigMix worked example (1 round, no rotation, no padding)", () => {
  const ex = V.sigmix_round_example;
  const stages = {};
  const c = sigmix.encryptWithParams([72, 105], { seed: ex.seed, prm: [{ k: ex.k, m: ex.m, rot: 0 }] }, (n, s) => (stages[n] = [...s]), false);
  assert.deepEqual(stages["r1.stream"], ex.after_stream);
  assert.deepEqual([...c], ex.cipher);
  assert.deepEqual([...sigmix.decryptWithParams(c, { seed: ex.seed, prm: [{ k: ex.k, m: ex.m, rot: 0 }] }, false)], ex.plain);
});

test("SigMix packet params, every stage and output", async () => {
  const kSig = fromHex(out("st1/sigmix(32)"));
  const { seed, prm } = await sigmix.deriveParams(kSig, pk.seq);
  assert.deepEqual({ seed, rounds: prm }, pk.sigmix_params);
  const stages = {};
  const sm = await sigmix.encrypt(fromHex(pk.plaintext_hex), kSig, pk.seq, 3, (n, s) => (stages[n] = toHex(s)));
  assert.deepEqual(stages, pk.sigmix_stages);
  assert.equal(toHex(sm), pk.sigmix_output);
  assert.equal(toHex(await sigmix.decrypt(sm, kSig, pk.seq)), pk.plaintext_hex);
});

test("SigMix round trip for lengths 0..199 and bad padding rejected", async () => {
  for (let len = 0; len < 200; len++) {
    const d = randomBytes(len), k = randomBytes(32);
    assert.deepEqual(await sigmix.decrypt(await sigmix.encrypt(d, k, len * 7919), k, len * 7919), d);
  }
  const prm = await sigmix.deriveParams(randomBytes(32), 1);
  assert.throws(() => sigmix.decryptWithParams(sigmix.encryptWithParams([10, 3], prm, null, false), prm));
});

test("HKDF outputs and transcript", async () => {
  const keys = await S.deriveKeys(fromHex(hs.shared_secret_x), fromHex(hs.client_nonce), fromHex(hs.server_nonce));
  assert.equal(toHex(keys.kAead), out("st1/aes-up(32)"));
  assert.equal(toHex(keys.kSig), out("st1/sigmix(32)"));
  assert.equal(toHex(keys.ivSalt), out("st1/iv-up(4)"));
  const t = S.transcript(fromHex(hs.client_ephemeral_public), fromHex(hs.server_ephemeral_public), fromHex(hs.client_nonce), fromHex(hs.server_nonce), hs.device_id, hs.session_id);
  assert.equal(toHex(t), hs.transcript);
});

test("completeHandshake verifies the vector signature and rejects another key", async () => {
  // Rebuild the client state from the vector's fixed ephemeral key.
  const p = fromHex(hs.client_ephemeral_public);
  const b64u = (u) => Buffer.from(u).toString("base64url");
  const privateKey = await crypto.subtle.importKey("jwk", { kty: "EC", crv: "P-256", d: b64u(fromHex(hs.client_ephemeral_private)), x: b64u(p.slice(1, 33)), y: b64u(p.slice(33)) }, { name: "ECDH", namedCurve: "P-256" }, false, ["deriveBits"]);
  const state = { pair: { privateKey }, pub: p, nonce: fromHex(hs.client_nonce), deviceId: hs.device_id, suite: S.SUITE_AES };
  const resp = { sessionId: hs.session_id, serverPublicKey: toB64(fromHex(hs.server_ephemeral_public)), serverNonce: toB64(fromHex(hs.server_nonce)), signature: toB64(fromHex(hs.server_signature_p1363_VERIFY_ONLY)), suite: S.SUITE_AES };
  const session = await S.completeHandshake(state, resp, fromHex(hs.server_longterm_public));
  assert.equal(toHex(session.keys.kAead), out("st1/aes-up(32)"));

  const other = await crypto.subtle.generateKey({ name: "ECDSA", namedCurve: "P-256" }, true, ["sign"]);
  const otherPub = new Uint8Array(await crypto.subtle.exportKey("raw", other.publicKey));
  await assert.rejects(S.completeHandshake(state, resp, otherPub), /MITM/);
});

test("packet seal matches the AES-GCM vector; bit flip fails", async () => {
  const keys = await S.deriveKeys(fromHex(hs.shared_secret_x), fromHex(hs.client_nonce), fromHex(hs.server_nonce));
  const session = { sid: hs.session_id, did: hs.device_id, keys };
  const ct = await S.seal(session, pk.seq, pk.ts_ms, utf8(pk.plaintext_utf8));
  assert.equal(toHex(ct), pk.aes_gcm.ciphertext_plus_tag);
  assert.equal(toB64(ct), pk.aes_gcm.ciphertext_plus_tag_b64);
  assert.equal(new TextDecoder().decode(await S.open(session, pk.seq, pk.ts_ms, ct)), pk.plaintext_utf8);
  const bad = ct.slice(); bad[5] ^= 0x01;
  await assert.rejects(S.open(session, pk.seq, pk.ts_ms, bad));
  await assert.rejects(S.open(session, pk.seq + 1, pk.ts_ms, ct));
});

test("fingerprint and entropy", async () => {
  assert.equal((await S.fingerprint(fromHex(hs.server_longterm_public))).length, 64);
  assert.equal(entropy(new Uint8Array(64)), 0);
  assert.ok(Math.abs(entropy(Uint8Array.from({ length: 256 }, (_, i) => i)) - 8) < 1e-9);
});

const chachaPath = new URL("../../src/SignalTrace.Server/wwwroot/lib/noble-ciphers/chacha.js", import.meta.url);
test("ChaCha20-Poly1305 suite round trip", { skip: !existsSync(chachaPath) && "noble-ciphers not copied yet" }, async () => {
  const keys = await S.deriveKeys(randomBytes(32), randomBytes(16), randomBytes(16), S.SUITE_CHACHA);
  const session = { sid: "00", did: hs.device_id, keys };
  const ct = await S.seal(session, 9, 1000, utf8("{\"t\":1000,\"src\":\"web\"}"));
  assert.equal(new TextDecoder().decode(await S.open(session, 9, 1000, ct)), "{\"t\":1000,\"src\":\"web\"}");
  const bad = ct.slice(); bad[0] ^= 1;
  await assert.rejects(S.open(session, 9, 1000, bad));
});

test("noble ChaCha20-Poly1305 equals OpenSSL (same backend as .NET)", { skip: !existsSync(chachaPath) && "noble-ciphers not copied yet" }, async () => {
  const { createCipheriv } = await import("node:crypto");
  const { chacha20poly1305 } = await import(chachaPath.href);
  const key = randomBytes(32), nonce = randomBytes(12), aad = utf8("ST1aad"), pt = utf8("{\"t\":1,\"src\":\"web\"}");
  const c = createCipheriv("chacha20-poly1305", key, nonce, { authTagLength: 16 });
  c.setAAD(aad, { plaintextLength: pt.length });
  const ossl = Buffer.concat([c.update(pt), c.final(), c.getAuthTag()]);
  assert.equal(toHex(chacha20poly1305(key, nonce, aad).encrypt(pt)), ossl.toString("hex"));
});
