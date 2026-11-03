"""Prototipo de referencia de ABCO (antes PRISMA-L), en su versión ligera.

Sirve para validar las propiedades matemáticas del algoritmo, medir sus métricas y generar los
vectores de prueba con los que se verifica la implementación en C# (Prisma.Core). No es la
implementación del sistema. Los nombres "PRISMA-L" y "prisma" que aparecen en el código se
refieren a ABCO.

Componentes:
  - Derivación de claves: PBKDF2-HMAC-SHA256 de la frase con una sal aleatoria de 16 bytes.
  - Capa de cifrado ("César matricial"): César dinámico (LCG de 8 bits) + Hill 2x2 mod 256
    + encadenamiento XOR entre bloques de 2 bytes.
  - Capa esteganográfica: se reemplaza el bit menos significativo de cada canal de color,
    siguiendo un recorrido de canales barajado con Fisher-Yates (LCG de 64 bits sembrado con la
    clave). Un HMAC-SHA256 truncado a 8 bytes detecta la frase incorrecta y las alteraciones.
  - Métricas: entropía de Shannon, PSNR y la prueba χ² con la que se rompe el César clásico.

Advertencia: gen_vectors.py importa este módulo y los vectores de tests/Prisma.Core.Tests
dependen de que su código no cambie en nada.

Uso: python3 -I prisma_lite_proto.py   (ejecuta las comprobaciones [1] a [9])
"""
import hashlib, hmac, math, os, random, collections

# ---------- derivación de claves ----------
def derivar(frase: str, sal: bytes):
    """Deriva de la frase y la sal todos los parámetros secretos de ABCO.

    K = PBKDF2-HMAC-SHA256(frase, sal, 100 000 iteraciones, 32 bytes). Las iteraciones encarecen
    probar frases por fuerza bruta y la sal hace que la misma frase dé claves distintas en cada imagen.
    De K se toman bytes y resúmenes SHA-256 con etiquetas distintas ("hill", "mac") para que cada
    parámetro sea independiente de los demás.

    Args:
        frase: frase secreta compartida (se codifica en UTF-8).
        sal: 16 bytes aleatorios; viajan sin cifrar en la propia imagen.

    Returns:
        Tupla (A, s0, la, lc, c0, semilla, k_mac):
          A: matriz de Hill 2x2 invertible mod 256, como (a, b, c, d) = [[a, b], [c, d]].
          s0: desplazamiento César inicial (0..255).
          la, lc: multiplicador e incremento del LCG de 8 bits del César dinámico.
          c0: bloque inicial (2 bytes) del encadenamiento XOR, como un vector de inicialización.
          semilla: entero de 64 bits que siembra el recorrido de canales.
          k_mac: clave de 32 bytes del HMAC que autentica el cifrado.
    """
    K = hashlib.pbkdf2_hmac("sha256", frase.encode(), sal, 100_000, 32)
    # Matriz de Hill: es invertible mod 256 si y solo si su determinante es una unidad de Z_256, es decir,
    # si gcd(det, 256) = 1, lo que equivale a que det sea impar. Se generan candidatas con
    # SHA-256(K || "hill" || ctr) y se rechazan las de determinante par (muestreo con rechazo); como
    # 6 de las 16 matrices mod 2 son invertibles, se acepta una candidata con probabilidad 3/8.
    ctr = 0
    while True:                                   # matriz 2x2 con det impar (muestreo con rechazo)
        h = hashlib.sha256(K + b"hill" + bytes([ctr])).digest()
        a, b, c, d = h[0], h[1], h[2], h[3]
        if (a * d - b * c) % 2 == 1: break
        ctr += 1
    A = (a, b, c, d)
    # César dinámico: s_{i+1} = (la·s_i + lc) mod 256. Por el teorema de Hull–Dobell, un LCG
    # mod m = 2^8 tiene periodo completo m si y solo si (1) lc es coprimo con m, es decir, impar;
    # (2) la − 1 es divisible por todos los primos de m (el 2), y (3) la − 1 es divisible por 4, porque
    # 4 divide a m. Con la = 4·(1..63) + 1 ∈ {5, 9, …, 253} y lc impar se cumplen las tres, y los 256
    # desplazamientos aparecen antes de repetirse. Se excluye la = 1, que haría del César un simple contador.
    s0 = K[4]                                      # desplazamiento César inicial
    la = 4 * (K[5] % 63 + 1) + 1                   # a ≡ 1 (mod 4), a ≠ 1 -> Hull-Dobell
    lc = 2 * (K[6] % 128) + 1                      # c impar        -> Hull-Dobell
    c0 = (K[7], K[8])                              # bloque inicial del encadenamiento
    semilla = int.from_bytes(K[16:24], "little")   # semilla del recorrido de píxeles
    # Clave del HMAC separada del resto por la etiqueta "mac" (separación de dominios).
    k_mac = hashlib.sha256(K + b"mac").digest()
    return A, s0, la, lc, c0, semilla, k_mac

def inv_mod(x, m=256):                             # Euclides extendido
    """Inverso multiplicativo de x módulo m con el algoritmo de Euclides extendido.

    Raises:
        AssertionError: si gcd(x, m) != 1 (x no es invertible; mod 256, si x es par).
    """
    # Invariante: r0 ≡ s0·x y r1 ≡ s1·x (mod m). Cada paso hace la división r0 = q·r1 + resto y aplica
    # la misma combinación a los coeficientes s. Al terminar (r1 = 0), r0 = gcd(x, m); si vale 1,
    # entonces s0·x ≡ 1 (mod m) y s0 es el inverso. No hace falta llevar el coeficiente de m.
    r0, r1, s0, s1 = x % m, m, 1, 0
    while r1:
        q = r0 // r1; r0, r1 = r1, r0 - q * r1; s0, s1 = s1, s0 - q * s1
    assert r0 == 1, "no invertible"
    return s0 % m

def inv_matriz(A):                                 # A^-1 = det^-1 · adj(A)  (mod 256)
    """Inversa de la matriz de Hill mod 256: A^-1 = det^-1 · adj(A), con adj([[a, b], [c, d]]) = [[d, −b], [−c, a]].

    El determinante es impar por construcción (ver derivar), así que siempre tiene inverso mod 256.
    """
    a, b, c, d = A
    di = inv_mod((a * d - b * c) % 256)
    return ((d * di) % 256, (-b * di) % 256, (-c * di) % 256, (a * di) % 256)

def mul(A, x, y):
    """Producto de la matriz A = (a, b, c, d) por el vector columna (x, y), mod 256."""
    a, b, c, d = A
    return ((a * x + b * y) % 256, (c * x + d * y) % 256)

# ---------- PRISMA-L (ABCO): capa de cifrado ----------
def cifrar(P: bytes, A, s0, la, lc, c0):
    """Cifra P con el "César matricial" de ABCO, en bloques de 2 bytes.

    Para cada bloque P_i, con C_0 = c0:
        s_i = (la·s_{i−1} + lc) mod 256              (César dinámico, LCG de 8 bits)
        C_i = A·(P_i ⊕ C_{i−1}) + (s_i, s_i)  mod 256 (encadenamiento XOR, Hill y desplazamiento)
    El encadenamiento hace que bloques iguales den cifrados distintos, y el desplazamiento variable
    rompe la linealidad pura de Hill, que se ataca con pocos pares de texto claro y cifrado.

    Si P tiene longitud impar se rellena con un 0x00; la longitud real viaja en la trama (ver ocultar).
    """
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
    """Operación inversa de cifrar: P_i = A^-1·(C_i − (s_i, s_i)) ⊕ C_{i−1}  (mod 256).

    Devuelve el texto con el posible byte de relleno; quien llama lo recorta a la longitud real.
    """
    Ai, out, prev, s = inv_matriz(A), bytearray(), c0, s0
    for i in range(0, len(C), 2):
        s = (la * s + lc) % 256
        x, y = (C[i] - s) % 256, (C[i + 1] - s) % 256
        x, y = mul(Ai, x, y)
        out += bytes((x ^ prev[0], y ^ prev[1])); prev = (C[i], C[i + 1])
    return bytes(out)

# ---------- PRISMA-L (ABCO): capa esteganográfica ----------
class LCG64:
    """Generador congruencial lineal de 64 bits que baraja el recorrido de canales.

    x_{n+1} = (6364136223846793005·x_n + 1442695040888963407) mod 2^64, con las constantes de
    Knuth (MMIX). El multiplicador es ≡ 1 (mod 4) y el incremento es impar, así que por Hull–Dobell
    el periodo es completo (2^64). No es un generador criptográfico: su papel es que el orden de los
    bits dependa de la semilla derivada de la frase.
    """
    def __init__(self, s): self.x = s & (2**64 - 1)
    # Devuelve los 32 bits altos del estado: en un LCG módulo una potencia de 2 los bits bajos tienen
    # periodos cortos (el bit k repite cada 2^(k+1) pasos), mientras que los altos se comportan mejor.
    def next32(self):
        self.x = (self.x * 6364136223846793005 + 1442695040888963407) % 2**64
        return self.x >> 32
    def uniforme(self, n):                         # muestreo con rechazo: sin sesgo de módulo
        """Entero uniforme en [0, n) sin sesgo de módulo."""
        # Muestreo con rechazo: lim es el mayor múltiplo de n que no pasa de 2^32. Se descartan los v ≥ lim,
        # así cada residuo v mod n tiene exactamente lim/n preimágenes y todos salen con igual probabilidad.
        # Con v % n directo, los primeros (2^32 mod n) residuos saldrían un poco más seguido.
        lim = (2**32 // n) * n
        while True:
            v = self.next32()
            if v < lim: return v % n

def recorrido(n_total, semilla, inicio=128):
    """Orden pseudoaleatorio en que se escriben los bits de la trama.

    Devuelve una permutación de los índices de canal [inicio, n_total) determinada por la semilla.
    Los primeros 128 canales se reservan para la sal (16 bytes × 8 bits), que se escribe en orden
    porque hace falta leerla antes de poder derivar la semilla.
    """
    idx = list(range(inicio, n_total)); g = LCG64(semilla)
    # Fisher–Yates (variante de Durstenfeld): de atrás hacia adelante, idx[i] se intercambia con una
    # posición j uniforme en [0, i]. Con j uniforme, ninguna permutación se ve favorecida por el
    # algoritmo; por eso j se toma con uniforme() y no con un módulo sesgado.
    for i in range(len(idx) - 1, 0, -1):           # Fisher-Yates
        j = g.uniforme(i + 1); idx[i], idx[j] = idx[j], idx[i]
    return idx

# Conversión entre bytes y listas de bits, con el bit más significativo de cada byte primero.
bits = lambda bs: [(b >> (7 - k)) & 1 for b in bs for k in range(8)]
def a_bytes(bl): return bytes(sum(bl[i + k] << (7 - k) for k in range(8)) for i in range(0, len(bl), 8))

def ocultar(canales: list, mensaje: str, frase: str):
    """Oculta un mensaje con ABCO en una lista de canales de color.

    Args:
        canales: valores 0..255 de los canales de la imagen (R, G, B de cada píxel, en fila).
        mensaje: texto a ocultar (se codifica en UTF-8).
        frase: frase secreta.

    Returns:
        Una copia de los canales con el bit menos significativo modificado:
          - canales[0:128]: los 128 bits de la sal, en orden;
          - a lo largo del recorrido barajado: la trama
            longitud(P) (4 bytes, big-endian) || C || HMAC-SHA256(k_mac, C)[:8].

    Raises:
        ValueError: si la trama no cabe en los canales disponibles.
    """
    sal = os.urandom(16)
    A, s0, la, lc, c0, sem, kmac = derivar(frase, sal)
    P = mensaje.encode(); C = cifrar(P, A, s0, la, lc, c0)
    trama = len(P).to_bytes(4, "big") + C + hmac.new(kmac, C, hashlib.sha256).digest()[:8]
    tb = bits(trama)
    if len(tb) > len(canales) - 128: raise ValueError("no cabe")
    out = canales[:]
    # (x & 0xFE) | b borra el bit menos significativo del canal y escribe en su lugar el bit b.
    for i, b in enumerate(bits(sal)): out[i] = (out[i] & 0xFE) | b
    R = recorrido(len(out), sem)
    for j, b in enumerate(tb): out[R[j]] = (out[R[j]] & 0xFE) | b
    return out

def revelar(canales: list, frase: str):
    """Recupera el mensaje oculto con ocultar().

    Lee la sal de los primeros 128 canales, deriva las claves, recorre los canales en el mismo orden,
    lee la longitud, el cifrado y la etiqueta, y verifica el HMAC antes de descifrar.

    Raises:
        ValueError: "error genérico" si la frase es incorrecta, la imagen fue alterada o la longitud
            leída es imposible. Es el mismo mensaje en todos los casos para no dar pistas a un atacante.
    """
    sal = a_bytes([c & 1 for c in canales[:128]])
    A, s0, la, lc, c0, sem, kmac = derivar(frase, sal)
    R = recorrido(len(canales), sem)
    leer = lambda a, n: a_bytes([canales[R[k]] & 1 for k in range(a * 8, (a + n) * 8)])
    # Lc es la longitud del cifrado: L redondeada al par siguiente por el relleno de cifrar().
    L = int.from_bytes(leer(0, 4), "big"); Lc = L + (L % 2)
    # Con una frase incorrecta, L sale al azar; se valida antes de leer fuera de la imagen.
    if (4 + Lc + 8) * 8 > len(canales) - 128: raise ValueError("error genérico")
    C, T = leer(4, Lc), leer(4 + Lc, 8)
    # compare_digest compara en tiempo constante, para no filtrar cuántos bytes de la etiqueta coinciden.
    if not hmac.compare_digest(T, hmac.new(kmac, C, hashlib.sha256).digest()[:8]): raise ValueError("error genérico")
    return descifrar(C, A, s0, la, lc, c0)[:L].decode()

# ---------- métricas ----------
def entropia(bs):
    """Entropía de Shannon en bits por byte: H = −Σ p_i·log2(p_i), con p_i la frecuencia de cada valor.

    Va de 0 (un solo valor repetido) a 8 (los 256 valores igual de frecuentes). Un texto en español
    ronda 4 bits por byte; un buen cifrado se acerca a 8.
    """
    n = len(bs); f = collections.Counter(bs)
    return -sum(v / n * math.log2(v / n) for v in f.values())

def psnr(a, b):
    """Relación señal a ruido pico entre dos listas de canales, en dB.

    MSE = media de (a_i − b_i)^2 y PSNR = 10·log10(255^2 / MSE). Si las listas son idénticas (MSE = 0)
    devuelve infinito. Cambiar solo bits menos significativos da diferencias de a lo más 1 por canal,
    de ahí valores altos; por encima de unos 40 dB el cambio es imperceptible.
    """
    mse = sum((x - y) ** 2 for x, y in zip(a, b)) / len(a)
    return float("inf") if mse == 0 else 10 * math.log10(255 ** 2 / mse)

# César sobre bytes (mod 256), sin clave dinámica: es la referencia con la que se compara ABCO.
def cesar(bs, k): return bytes((b + k) % 256 for b in bs)

if __name__ == "__main__":
    # Comprobaciones del prototipo. random usa una semilla fija; la sal y los mensajes de la prueba [4]
    # salen de os.urandom, así que algunos valores impresos cambian entre corridas, pero todas las
    # comprobaciones deben salir bien siempre.
    random.seed(1)
    # 1. |GL2(Z_16)| por fuerza bruta vs fórmula 2^(4(k-1))·6  -> extrapola a Z_256
    # Una matriz es invertible mod 2^k si y solo si lo es mod 2 (det impar), y cada una de las 6 matrices
    # invertibles mod 2 se levanta de 2^(4(k−1)) maneras: |GL2(Z_2^k)| = 6·2^(4(k−1)).
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
    # χ² = Σ (O − E)^2 / E, con O las apariciones de cada letra y E = n·p/100 las esperadas en español.
    # La clave correcta es la que deja el texto con el χ² más bajo.
    def chi2(t):
        n = sum(t.count(ch) for ch in esp) or 1
        return sum((t.count(ch) - n * p / 100) ** 2 / (n * p / 100) for ch, p in esp.items())
    abc = "abcdefghijklmnopqrstuvwxyz"
    ces26 = lambda t, k: "".join(abc[(abc.index(ch) + k) % 26] if ch in abc else ch for ch in t)
    claro = "la criptografia estudia como proteger la informacion de los mensajes que se envian entre dos personas"
    cif = ces26(claro, 11)
    k = min(range(26), key=lambda k: chi2(ces26(cif, -k)))
    print(f"[9] César (A-Z) k=11 roto con χ² probando las 26 claves: k encontrada = {k} -> {ces26(cif, -k)[:27]!r}")
