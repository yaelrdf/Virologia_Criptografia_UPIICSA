"""Genera vectores de prueba deterministas para Prisma.Core.Tests a partir del prototipo.
Uso: python3 -I tools/prototipo/gen_vectors.py > tests/Prisma.Core.Tests/vectors.json
"""
import json, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from prisma_lite_proto import derivar, cifrar, inv_matriz, recorrido, LCG64

casos = []
for frase, sal_hex, msg in [
    ("frase-secreta", "000102030405060708090a0b0c0d0e0f", "Hola, PRISMA-L"),
    ("contraseña Ñandú 🦄", "ffeeddccbbaa99887766554433221100", "Reunión a las 18:00"),
    ("x", "a5" * 16, "A"),
]:
    sal = bytes.fromhex(sal_hex)
    A, s0, la, lc, c0, sem, kmac = derivar(frase, sal)
    g = LCG64(sem)
    casos.append({
        "frase": frase, "sal": sal_hex, "mensaje": msg,
        "A": list(A), "Ainv": list(inv_matriz(A)), "s0": s0, "a": la, "c": lc, "c0": list(c0),
        "semilla": str(sem), "kmac": kmac.hex(),
        "cifrado": cifrar(msg.encode(), A, s0, la, lc, c0).hex(),
        "lcg32": [g.next32() for _ in range(5)],
        "recorrido1000": recorrido(1000, sem)[:40],
    })
json.dump({"casos": casos}, sys.stdout, ensure_ascii=False, indent=1)
