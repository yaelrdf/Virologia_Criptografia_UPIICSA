"""Regenerates test-vectors.json from fixed inputs (private keys, nonces, ids, plaintext).

Every value is recomputed with Python `cryptography` (OpenSSL) and ycm1.py, so the C#, JavaScript and Kotlin
implementations are checked against an independent source. verify.mjs re-checks the result with Web Crypto.
    python3 reference/gen_vectors.py [out.json]
"""
import base64, hashlib, json, struct, sys
from pathlib import Path
from cryptography.hazmat.primitives import hashes
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.hazmat.primitives.asymmetric.utils import decode_dss_signature
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from cryptography.hazmat.primitives.kdf.hkdf import HKDF
from cryptography.hazmat.primitives.serialization import Encoding, PublicFormat
import ycm1

CLIENT_NONCE = bytes(range(0x00, 0x10))
SERVER_NONCE = bytes(range(0x10, 0x20))
DEVICE_ID = "6f1c2a3e-8b4d-4c5e-9a7f-0123456789ab"
SESSION_ID = "a1b2c3d4e5f60718"
SEQ, TS = 1, 1760000000000
PLAINTEXT = ('{"t":1760000000000,"src":"web","mot":{"ax":0.12,"ay":-0.03,"az":9.79,"rms":0.04,"peak":0.11,"fs":32},'
             '"ori":{"az":182.5,"pi":-3.1,"ro":1.2},"env":{"lux":320},"bat":{"lvl":0.87,"chg":false}}')


def key(byte: int):
    return ec.derive_private_key(int.from_bytes(bytes([byte]) * 32, "big"), ec.SECP256R1())


def pub(k) -> bytes:
    return k.public_key().public_bytes(Encoding.X962, PublicFormat.UncompressedPoint)


def hkdf(ikm: bytes, salt: bytes, info: str, n: int) -> bytes:
    return HKDF(hashes.SHA256(), n, salt, info.encode()).derive(ikm)


def main(out: Path):
    c_eph, s_eph, s_lt = key(0x11), key(0x22), key(0x33)
    z = c_eph.exchange(ec.ECDH(), s_eph.public_key())
    assert z == s_eph.exchange(ec.ECDH(), c_eph.public_key())
    salt = CLIENT_NONCE + SERVER_NONCE
    k_aes, k_sig, iv = hkdf(z, salt, "st1/aes-up", 32), hkdf(z, salt, "st1/ycm1", 32), hkdf(z, salt, "st1/iv-up", 4)
    transcript = (b"ST1-HS" + pub(c_eph) + pub(s_eph) + CLIENT_NONCE + SERVER_NONCE
                  + DEVICE_ID.encode() + SESSION_ID.encode())
    r, s = decode_dss_signature(s_lt.sign(transcript, ec.ECDSA(hashes.SHA256())))
    sig = r.to_bytes(32, "big") + s.to_bytes(32, "big")

    plain = PLAINTEXT.encode()
    trace = []
    sm = ycm1.encrypt(plain, k_sig, SEQ, trace=trace)
    assert ycm1.decrypt(sm, k_sig, SEQ) == plain
    seed, prm = ycm1.derive_params(k_sig, SEQ)
    nonce = iv + struct.pack(">Q", SEQ)
    aad = b"ST1" + DEVICE_ID.encode() + struct.pack(">Q", SEQ) + struct.pack(">Q", TS)
    ct = AESGCM(k_aes).encrypt(nonce, sm, aad)

    v = {
        "notes": "All byte strings hex unless suffixed _b64. Big-endian integers. EC = NIST P-256. Public keys = X9.62 "
                 "uncompressed (65 bytes). Signatures = IEEE P1363 r||s (64 bytes). Cipher: Yael Crypto March1 (YCM1).",
        "ycm1_round_example": {"seed": 7, "k": 3, "m": 5, "plain": [72, 105], "after_stream": [110, 42],
                               "cipher": [236, 198], "note": "1 round, no rotation, no padding; K=[[1,3],[5,16]], det=1"},
        "handshake": {
            "client_ephemeral_private": "11" * 32, "server_ephemeral_private": "22" * 32, "server_longterm_private": "33" * 32,
            "client_ephemeral_public": pub(c_eph).hex(), "server_ephemeral_public": pub(s_eph).hex(),
            "server_longterm_public": pub(s_lt).hex(),
            "client_nonce": CLIENT_NONCE.hex(), "server_nonce": SERVER_NONCE.hex(),
            "device_id": DEVICE_ID, "session_id": SESSION_ID, "shared_secret_x": z.hex(),
            "hkdf": {"hash": "SHA-256", "salt": "client_nonce||server_nonce", "ikm": "shared_secret_x",
                     "outputs": {"st1/aes-up(32)": k_aes.hex(), "st1/ycm1(32)": k_sig.hex(), "st1/iv-up(4)": iv.hex()}},
            "transcript_formula": "'ST1-HS' || client_eph_pub || server_eph_pub || client_nonce || server_nonce || "
                                  "utf8(device_id) || utf8(session_id)",
            "transcript": transcript.hex(),
            "server_signature_p1363_VERIFY_ONLY": sig.hex(),
            "signature_note": "ECDSA is randomized: do not compare signatures, only verify this one with server_longterm_public.",
        },
        "packet": {
            "seq": SEQ, "ts_ms": TS, "plaintext_utf8": PLAINTEXT, "plaintext_hex": plain.hex(),
            "ycm1_params": {"seed": seed, "rounds": [{"k": k, "m": m, "rot": rot} for k, m, rot in prm]},
            "ycm1_stages": {name: state.hex() for name, state in trace},
            "ycm1_output": sm.hex(),
            "aes_gcm": {"key": k_aes.hex(), "nonce(12)=iv_salt||u64be(seq)": nonce.hex(),
                        "aad = 'ST1'||utf8(device_id)||u64be(seq)||u64be(ts)": aad.hex(),
                        "ciphertext_plus_tag": ct.hex(), "ciphertext_plus_tag_b64": base64.b64encode(ct).decode(), "tag_len": 16},
        },
        "pbkdf2": {"password": "correct horse battery staple", "salt": b"defghijklmnopqrs".hex(), "iterations": 600000,
                   "hash": "SHA-256", "dklen": 32,
                   "derived": hashlib.pbkdf2_hmac("sha256", b"correct horse battery staple", b"defghijklmnopqrs", 600000, 32).hex()},
    }
    out.write_text(json.dumps(v, indent=2) + "\n")
    print(f"wrote {out}")


if __name__ == "__main__":
    main(Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).with_name("test-vectors.json"))
