"""SigMix reference implementation (educational cipher, NOT secure on its own)."""
import hashlib, struct

def derive_params(k_sig: bytes, seq: int, rounds: int = 3):
    h = hashlib.sha256(k_sig + struct.pack(">Q", seq) + b"SigMix").digest()
    seed = h[0]
    ks = [(h[1 + 3 * i], h[2 + 3 * i], h[3 + 3 * i]) for i in range(rounds)]  # (k, m, rot)
    return seed, ks

def pad(data: bytes) -> bytes:
    n = 2 - (len(data) % 2)           # 1 if odd length, 2 if even
    return data + bytes([n]) * n

def unpad(p: bytes) -> bytes:
    n = p[-1]
    if n not in (1, 2) or len(p) < n or any(b != n for b in p[-n:]):
        raise ValueError("bad padding")
    return p[:-n]

def rotl(p, r):
    r %= len(p); return p[r:] + p[:r]

def rotr(p, r):
    r %= len(p); return p[-r:] + p[:-r] if r else p

def encrypt(plain: bytes, k_sig: bytes, seq: int, rounds: int = 3, trace=None) -> bytes:
    seed, prm = derive_params(k_sig, seq, rounds)
    p = list(pad(plain)); n = len(p); s = seed
    if trace is not None: trace.append(("padded", bytes(p)))
    for r in range(rounds):
        k, m, rot = prm[r]
        for i in range(n):                       # 1. stream addition (LCG)
            s = (5 * s + 3) & 255
            p[i] = (p[i] + s) & 255
        if trace is not None: trace.append((f"r{r+1}.stream", bytes(p)))
        for i in range(0, n, 2):                 # 2. 2x2 matrix mix, det = 1
            a, b = p[i], p[i + 1]
            p[i] = (a + k * b) & 255
            p[i + 1] = (m * a + (1 + k * m) * b) & 255
        if trace is not None: trace.append((f"r{r+1}.matrix", bytes(p)))
        p = rotl(p, rot)                         # 3. rotation (permutation)
        if trace is not None: trace.append((f"r{r+1}.rotate", bytes(p)))
    return bytes(p)

def decrypt(ct: bytes, k_sig: bytes, seq: int, rounds: int = 3) -> bytes:
    seed, prm = derive_params(k_sig, seq, rounds)
    n = len(ct); s = seed; stream = []
    for r in range(rounds):                      # regenerate keystream, in order
        row = []
        for i in range(n):
            s = (5 * s + 3) & 255; row.append(s)
        stream.append(row)
    p = list(ct)
    for r in reversed(range(rounds)):
        k, m, rot = prm[r]
        p = rotr(p, rot)
        for i in range(0, n, 2):
            c0, c1 = p[i], p[i + 1]
            p[i] = ((1 + k * m) * c0 - k * c1) & 255
            p[i + 1] = (-m * c0 + c1) & 255
        for i in range(n):
            p[i] = (p[i] - stream[r][i]) & 255
    return unpad(bytes(p))

if __name__ == "__main__":
    # Round-level check from the worked example (seed=7,k=3,m=5, 1 round, no rotation, no padding)
    s = 7; a, b = 72, 105
    s = (5*s+3)&255; a = (a+s)&255
    s = (5*s+3)&255; b = (b+s)&255
    c0 = (a + 3*b)&255; c1 = (5*a + 16*b)&255
    print("worked example:", (a, b), "->", (c0, c1))
    # LCG full period check (Hull-Dobell)
    seen=set(); x=0
    for _ in range(256): x=(5*x+3)&255; seen.add(x)
    print("LCG period covers", len(seen), "values")
    # roundtrip fuzz
    import os
    for L in range(0, 200):
        d = os.urandom(L); key = os.urandom(32); sq = L * 7919
        assert decrypt(encrypt(d, key, sq), key, sq) == d
    print("roundtrip OK for lengths 0..199")
