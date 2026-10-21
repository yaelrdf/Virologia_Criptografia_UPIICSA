"""Prototipo de validación de PRISMA-L (versión ligera). Solo para medir y generar vectores; no es la implementación.
Componentes: César dinámico (LCG) + Hill 2x2 mod 256 + encadenamiento XOR + LSB con recorrido Fisher-Yates.
Uso: python3 -I prisma_lite_proto.py
"""
import hashlib, hmac, math, os, random, collections

# ---------- derivación de claves ----------
def derivar(frase: str, sal: bytes):
    K = hashlib.pbkdf2_hmac("sha256", frase.encode(), sal, 100_000, 32)
    ctr = 0
    while True:                                   # matriz 2x2 con det impar (muestreo con rechazo)
        h = hashlib.sha256(K + b"hill" + bytes([ctr])).digest()
        a, b, c, d = h[0], h[1], h[2], h[3]
        if (a * d - b * c) % 2 == 1: break
        ctr += 1
    A = (a, b, c, d)
    s0 = K[4]                                      # desplazamiento César inicial
    la = 4 * (K[5] % 63 + 1) + 1                   # a ≡ 1 (mod 4), a ≠ 1 -> Hull-Dobell
    lc = 2 * (K[6] % 128) + 1                      # c impar        -> Hull-Dobell
    c0 = (K[7], K[8])                              # bloque inicial del encadenamiento
    semilla = int.from_bytes(K[16:24], "little")   # semilla del recorrido de píxeles
    k_mac = hashlib.sha256(K + b"mac").digest()
    return A, s0, la, lc, c0, semilla, k_mac

def inv_mod(x, m=256):                             # Euclides extendido
    r0, r1, s0, s1 = x % m, m, 1, 0
    while r1:
        q = r0 // r1; r0, r1 = r1, r0 - q * r1; s0, s1 = s1, s0 - q * s1
    assert r0 == 1, "no invertible"
    return s0 % m

def inv_matriz(A):                                 # A^-1 = det^-1 · adj(A)  (mod 256)
    a, b, c, d = A
    di = inv_mod((a * d - b * c) % 256)
    return ((d * di) % 256, (-b * di) % 256, (-c * di) % 256, (a * di) % 256)

def mul(A, x, y):
    a, b, c, d = A
    return ((a * x + b * y) % 256, (c * x + d * y) % 256)

# ---------- PRISMA-L: capa de cifrado ----------
def cifrar(P: bytes, A, s0, la, lc, c0):
    if len(P) % 2: P += b"\x00"                    # relleno; la longitud real va en la trama
    out, prev, s = bytearray(), c0, s0
    for i in range(0, len(P), 2):
        s = (la * s + lc) % 256                    # César dinámico
        x, y = P[i] ^ prev[0], P[i + 1] ^ prev[1]  # encadenamiento
        x, y = mul(A, x, y)                        # Hill
        cb = ((x + s) % 256, (y + s) % 256)        # desplazamiento César
        out += bytes(cb); prev = cb
    return bytes(out)

def descifrar(C: bytes, A, s0, la, lc, c0):
    Ai, out, prev, s = inv_matriz(A), bytearray(), c0, s0
    for i in range(0, len(C), 2):
        s = (la * s + lc) % 256
        x, y = (C[i] - s) % 256, (C[i + 1] - s) % 256
        x, y = mul(Ai, x, y)
        out += bytes((x ^ prev[0], y ^ prev[1])); prev = (C[i], C[i + 1])
    return bytes(out)

# ---------- PRISMA-L: capa estego ----------
class LCG64:
    def __init__(self, s): self.x = s & (2**64 - 1)
    def next32(self):
        self.x = (self.x * 6364136223846793005 + 1442695040888963407) % 2**64
        return self.x >> 32
    def uniforme(self, n):                         # muestreo con rechazo: sin sesgo de módulo
        lim = (2**32 // n) * n
        while True:
            v = self.next32()
            if v < lim: return v % n

def recorrido(n_total, semilla, inicio=128):
    idx = list(range(inicio, n_total)); g = LCG64(semilla)
    for i in range(len(idx) - 1, 0, -1):           # Fisher-Yates
        j = g.uniforme(i + 1); idx[i], idx[j] = idx[j], idx[i]
    return idx

bits = lambda bs: [(b >> (7 - k)) & 1 for b in bs for k in range(8)]
def a_bytes(bl): return bytes(sum(bl[i + k] << (7 - k) for k in range(8)) for i in range(0, len(bl), 8))

def ocultar(canales: list, mensaje: str, frase: str):
    sal = os.urandom(16)
    A, s0, la, lc, c0, sem, kmac = derivar(frase, sal)
    P = mensaje.encode(); C = cifrar(P, A, s0, la, lc, c0)
    trama = len(P).to_bytes(4, "big") + C + hmac.new(kmac, C, hashlib.sha256).digest()[:8]
    tb = bits(trama)
    if len(tb) > len(canales) - 128: raise ValueError("no cabe")
    out = canales[:]
    for i, b in enumerate(bits(sal)): out[i] = (out[i] & 0xFE) | b
    R = recorrido(len(out), sem)
    for j, b in enumerate(tb): out[R[j]] = (out[R[j]] & 0xFE) | b
    return out

def revelar(canales: list, frase: str):
    sal = a_bytes([c & 1 for c in canales[:128]])
    A, s0, la, lc, c0, sem, kmac = derivar(frase, sal)
    R = recorrido(len(canales), sem)
    leer = lambda a, n: a_bytes([canales[R[k]] & 1 for k in range(a * 8, (a + n) * 8)])
    L = int.from_bytes(leer(0, 4), "big"); Lc = L + (L % 2)
    if (4 + Lc + 8) * 8 > len(canales) - 128: raise ValueError("error genérico")
    C, T = leer(4, Lc), leer(4 + Lc, 8)
    if not hmac.compare_digest(T, hmac.new(kmac, C, hashlib.sha256).digest()[:8]): raise ValueError("error genérico")
    return descifrar(C, A, s0, la, lc, c0)[:L].decode()

# ---------- métricas ----------
def entropia(bs):
    n = len(bs); f = collections.Counter(bs)
    return -sum(v / n * math.log2(v / n) for v in f.values())

def psnr(a, b):
    mse = sum((x - y) ** 2 for x, y in zip(a, b)) / len(a)
    return float("inf") if mse == 0 else 10 * math.log10(255 ** 2 / mse)

def cesar(bs, k): return bytes((b + k) % 256 for b in bs)

if __name__ == "__main__":
    random.seed(1)
    # 1. |GL2(Z_16)| por fuerza bruta vs fórmula 2^(4(k-1))·6  -> extrapola a Z_256
    cnt = sum(1 for a in range(16) for b in range(16) for c in range(16) for d in range(16) if (a * d - b * c) % 2)
    print(f"[1] |GL2(Z16)| bruto = {cnt}, fórmula = {2**12 * 6}  ->  |GL2(Z256)| = 2^28·6 = {2**28 * 6:,}")
    # 2. A·A^-1 = I
    sal = os.urandom(16); A, s0, la, lc, c0, sem, _ = derivar("clave demo", sal); Ai = inv_matriz(A)
    I = (mul(A, *mul(Ai, 1, 0)), mul(A, *mul(Ai, 0, 1)))
    print(f"[2] A={A}, det={(A[0]*A[3]-A[1]*A[2])%256}, A^-1={Ai}, A·A^-1·e1,e2 = {I}")
    # 3. LCG periodo completo (Hull-Dobell)
    s, vistos = s0, set()
    for _ in range(256): s = (la * s + lc) % 256; vistos.add(s)
    print(f"[3] LCG a={la}, c={lc}: {len(vistos)} desplazamientos distintos de 256")
    # 4. ida y vuelta del cifrador
    ok = all(descifrar(cifrar(m := os.urandom(2 * random.randint(1, 50)), A, s0, la, lc, c0), A, s0, la, lc, c0) == m for _ in range(500))
    print(f"[4] cifrar/descifrar 500 mensajes aleatorios: {'OK' if ok else 'FALLA'}")
    # 5. entropía: César conserva el histograma, PRISMA-L lo aplana
    texto = ("el veloz murcielago hindu comia feliz cardillo y kiwi " * 40).encode()
    print(f"[5] entropía bits/byte  claro={entropia(texto):.2f}  César={entropia(cesar(texto, 3)):.2f}  "
          f"PRISMA-L={entropia(cifrar(texto, A, s0, la, lc, c0)):.2f}  (máx 8)")
    # 6. bloques iguales -> cifrados distintos (encadenamiento + César dinámico)
    C = cifrar(b"AAAAAAAAAAAAAAAA", A, s0, la, lc, c0)
    print(f"[6] 'AAAA…' -> {C.hex(' ', 2)}  (bloques distintos: {len({C[i:i+2] for i in range(0, 16, 2)})}/8)")
    # 7. recorrido es permutación
    R = recorrido(10_000, sem); print(f"[7] recorrido de 9 872 canales sin repetidos: {len(set(R)) == len(R) == 9872}")
    # 8. estego ida y vuelta + PSNR + frase incorrecta
    portadora = [random.randint(0, 255) for _ in range(256 * 256 * 3)]
    msg = "Reunión a las 18:00 en la biblioteca. ¡No faltes! 🦄"
    est = ocultar(portadora, msg, "frase-secreta")
    print(f"[8] revelado: {revelar(est, 'frase-secreta')!r}  PSNR={psnr(portadora, est):.1f} dB")
    try: revelar(est, "otra-frase"); print("    frase incorrecta: ¡NO detectada!")
    except ValueError as e: print(f"    frase incorrecta -> {e}")
    est2 = est[:]; R2 = recorrido(len(est2), derivar("frase-secreta", a_bytes([c & 1 for c in est2[:128]]))[5])
    est2[R2[100]] ^= 1
    try: revelar(est2, "frase-secreta"); print("    alteración: ¡NO detectada!")
    except ValueError as e: print(f"    1 bit alterado en el recorrido -> {e}")
    # 9. César roto por fuerza bruta + χ² (frecuencias del español, letras a-z)
    esp = dict(zip("abcdefghijklmnopqrstuvwxyz", [12.5,1.4,4.7,5.9,13.7,.7,1.0,.7,6.3,.4,.01,5.0,3.2,6.7,8.7,2.5,.9,6.9,8.0,4.6,3.9,.9,.02,.2,.9,.5]))
    def chi2(t):
        n = sum(t.count(ch) for ch in esp) or 1
        return sum((t.count(ch) - n * p / 100) ** 2 / (n * p / 100) for ch, p in esp.items())
    abc = "abcdefghijklmnopqrstuvwxyz"
    ces26 = lambda t, k: "".join(abc[(abc.index(ch) + k) % 26] if ch in abc else ch for ch in t)
    claro = "la criptografia estudia como proteger la informacion de los mensajes que se envian entre dos personas"
    cif = ces26(claro, 11)
    k = min(range(26), key=lambda k: chi2(ces26(cif, -k)))
    print(f"[9] César (A-Z) k=11 roto con χ² probando las 26 claves: k encontrada = {k} -> {ces26(cif, -k)[:27]!r}")
