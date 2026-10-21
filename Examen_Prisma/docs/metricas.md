# Métricas medidas de PRISMA-L
Generado por `tools/Prisma.Metrics` el 2026-10-21 16:47. Máquina: 2 núcleos, .NET 10.0.13.

## 1. Entropía de Shannon (bits por byte, máximo 8)
Texto en español (sin tildes) de 2350 bytes.

| Algoritmo | Entropía | Observación |
|---|---|---|
| Texto claro | 4.097 | Redundancia del idioma |
| César (k = 3) | 4.097 | Solo renombra símbolos: el histograma se conserva |
| LSB clásico | 4.097 | No cifra: los bits del mensaje van tal cual |
| PRISMA-L | 7.929 | Matriz + César dinámico + encadenamiento |
| AES-256-GCM | 7.918 | Referencia comercial |

## 2. Difusión (avalancha, Monte Carlo con 2 000 ensayos)
Se cambia 1 bit al azar del texto claro de 64 bytes y se mide la fracción de bits que cambian en la salida. Un cifrador ideal da ≈ 50 %.

| Algoritmo | Bits de salida que cambian |
|---|---|
| César | 0.33 % |
| PRISMA-L (mensaje completo) | 13.10 % |
| PRISMA-L (solo del bloque alterado en adelante) | 25.88 % |
| AES-256-GCM (modo contador, misma clave y nonce) | 0.20 % |

Lectura: el encadenamiento propaga el cambio **hacia adelante**, no hacia atrás; por eso el promedio sobre el mensaje completo es menor. AES-GCM es un modo de flujo: un bit del claro cambia un bit del cifrado, y su protección contra alteraciones viene de la etiqueta GCM, no de la avalancha.

**Sensibilidad a la clave:** cambiar una letra de la frase ("frase-secreta" → "frase-secretb") cambia el **50.31 %** de los bits del cifrado (40 ensayos).

## 3. Ataque de texto claro conocido (álgebra lineal)
Matriz secreta real: A = [[207, 16], [245, 191]].

| Cifrador atacado | Matriz recuperada con 2 bloques conocidos | Bloques nuevos predichos |
|---|---|---|
| Hill puro 2×2 (C = A·P) | [[207, 16], [245, 191]] | 1000 de 1000 |
| PRISMA-L | [[190, 68], [116, 109]] | 0 de 1000 |

## 4. Esteganografía: distorsión y detectabilidad
Portadora: `montanas.png`, 640×480, capacidad PRISMA-L 115,172 B, LSB clásico 115,196 B.

| Mensaje | Modo | PSNR (dB) | P(mensaje) χ² al 10 % inicial | P(mensaje) χ² en toda la imagen |
|---|---|---|---|---|
| 32 B | PRISMA-L | 84.01 | 0.0 % | 0.0 % |
| 32 B | LSB clásico | 86.13 | 0.0 % | 0.0 % |
| 1,024 B | PRISMA-L | 71.46 | 0.0 % | 0.0 % |
| 1,024 B | LSB clásico | 71.59 | 0.0 % | 0.0 % |
| 10,240 B | PRISMA-L | 61.63 | 0.0 % | 0.0 % |
| 10,240 B | LSB clásico | 61.64 | 0.0 % | 0.0 % |
| 57,000 B | PRISMA-L | 54.19 | 0.0 % | 0.0 % |
| 57,000 B | LSB clásico | 54.20 | 98.6 % | 0.0 % |
| 114,000 B | PRISMA-L | 51.18 | 96.8 % | 100.0 % |
| 114,000 B | LSB clásico | 51.18 | 77.7 % | 100.0 % |
| (sin mensaje) | — | ∞ | 0.0 % | 0.0 % |

Lectura: el LSB clásico escribe al inicio de la imagen, así que el ataque χ² sobre el 10 % inicial lo detecta en cuanto el mensaje llena esa zona. PRISMA-L reparte los bits por toda la imagen: con mensajes pequeños, ninguna región queda saturada. Con ocupación alta, cualquier LSB por reemplazo se vuelve detectable.

## 4b. Integridad y robustez (escenarios E3 y E6)
Mensaje de 31 B oculto con PRISMA-L en `montanas.png`.

| Caso | Resultado |
|---|---|
| Imagen intacta, frase correcta | se revela: «Hola Bob, nos vemos a las 18:00» |
| Imagen intacta, frase incorrecta | error genérico (rechazado) |
| 1 bit alterado **dentro** de la trama (posición 200 del recorrido) | error genérico (rechazado) |
| 1 bit alterado en un canal que la trama **no usa** | se revela: «Hola Bob, nos vemos a las 18:00» |
| 1 bit alterado en la sal (canales 0..127) | error genérico (rechazado) |
| Recomprimida como JPEG calidad 90 ("enviar como foto") | error genérico (rechazado); cambiaron 460,764 de 921,600 LSB (50.0 %) |
| Recomprimida como JPEG calidad 100 | error genérico (rechazado) |

## 5. Tiempos (promedio de 5 ejecuciones)
| Operación | ms |
|---|---|
| Derivar claves (PBKDF2-SHA256, 100,000 iteraciones) | 246.0 |
| Cifrar 1 MB con PRISMA-L (sin derivación) | 21.4 |
| Recorrido Fisher–Yates de 921,600 canales | 128.7 |
| Ocultar 64 B en 640×480 (total) | 299.3 |
| Revelar 64 B (total) | 332.1 |
| Romper César con χ² (26 claves, 200 caracteres) | 4.30 |

## 6. Espacio de claves
| Algoritmo | Claves posibles |
|---|---|
| César (A–Z) | 26 (25 útiles) |
| Matriz de Hill 2×2 invertible en ℤ₂₅₆ | \|GL₂(ℤ₂₅₆)\| = 2²⁸·6 = 1,610,612,736 |
| Parámetros del César dinámico (s₀, a, c) | 256 · 63 · 128 = 2,064,384 |
| Semilla del recorrido | 2⁶⁴ |
| Material de clave de PRISMA-L | Derivado de la frase con PBKDF2 (32 B): la seguridad práctica depende de la frase |
