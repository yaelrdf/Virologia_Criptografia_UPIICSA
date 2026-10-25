# Métricas medidas de ABCO
Generado por `tools/Prisma.Metrics` el 2026-10-24 23:55. Máquina: 2 núcleos, .NET 10.0.13.

## 1. Entropía de Shannon (bits por byte, máximo 8)
Texto en español (sin tildes) de 2350 bytes.

| Algoritmo | Entropía | Observación |
|---|---|---|
| Texto claro | 4.097 | Redundancia del idioma |
| César (k = 3) | 4.097 | Solo renombra símbolos: el histograma se conserva |
| Esteganografía clásica | 4.097 | No cifra: los bits del mensaje van tal cual |
| ABCO | 7.929 | Matriz + César dinámico + encadenamiento |

## 2. Difusión (avalancha, Monte Carlo con 2 000 ensayos)
Se cambia 1 bit al azar del texto claro de 64 bytes y se mide la fracción de bits que cambian en la salida. Un cifrador ideal da ≈ 50 %.

| Algoritmo | Bits de salida que cambian |
|---|---|
| César | 0.33 % |
| ABCO (mensaje completo) | 13.10 % |
| ABCO (solo del bloque alterado en adelante) | 26.48 % |

Lectura: el encadenamiento propaga el cambio **hacia adelante**, no hacia atrás; por eso el promedio sobre el mensaje completo es menor.

**Sensibilidad a la clave:** cambiar una letra de la frase ("frase-secreta" → "frase-secretb") cambia el **50.02 %** de los bits del cifrado (40 ensayos).

## 3. Ataque de texto claro conocido (álgebra lineal)
Matriz secreta real: A = [[207, 16], [245, 191]].

| Cifrador atacado | Matriz recuperada con 2 bloques conocidos | Bloques nuevos predichos |
|---|---|---|
| Hill puro 2×2 (C = A·P) | [[207, 16], [245, 191]] | 1000 de 1000 |
| ABCO | [[61, 149], [174, 121]] | 0 de 1000 |

## 4. Esteganografía: distorsión y detectabilidad
Portadora: `montanas.png`, 640×480, capacidad ABCO 115,172 B, Esteganografía clásica 115,196 B.

| Mensaje | Modo | PSNR (dB) | P(mensaje) χ² al 10 % inicial | P(mensaje) χ² en toda la imagen |
|---|---|---|---|---|
| 32 B | ABCO | 83.66 | 0.0 % | 0.0 % |
| 32 B | Esteganografía clásica | 86.57 | 0.0 % | 0.0 % |
| 1,024 B | ABCO | 71.57 | 0.0 % | 0.0 % |
| 1,024 B | Esteganografía clásica | 71.62 | 0.0 % | 0.0 % |
| 10,240 B | ABCO | 61.65 | 0.0 % | 0.0 % |
| 10,240 B | Esteganografía clásica | 61.65 | 0.0 % | 0.0 % |
| 57,000 B | ABCO | 54.19 | 0.0 % | 0.0 % |
| 57,000 B | Esteganografía clásica | 54.19 | 98.1 % | 0.0 % |
| 114,000 B | ABCO | 51.19 | 100.0 % | 100.0 % |
| 114,000 B | Esteganografía clásica | 51.18 | 99.8 % | 99.9 % |
| (sin mensaje) | — | ∞ | 0.0 % | 0.0 % |

Lectura: la esteganografía clásica escribe al inicio de la imagen, así que el ataque χ² sobre el 10 % inicial lo detecta en cuanto el mensaje llena esa zona. ABCO reparte los bits por toda la imagen: con mensajes pequeños, ninguna región queda saturada. Con ocupación alta, cualquier ocultamiento por reemplazo del bit menos significativo se vuelve detectable.

## 4b. Integridad y robustez (escenarios E3 y E6)
Mensaje de 31 B oculto con ABCO en `montanas.png`.

| Caso | Resultado |
|---|---|
| Imagen intacta, frase correcta | se revela: «Hola Bob, nos vemos a las 18:00» |
| Imagen intacta, frase incorrecta | error genérico (rechazado) |
| 1 bit alterado **dentro** de la trama (posición 200 del recorrido) | error genérico (rechazado) |
| 1 bit alterado en un canal que la trama **no usa** | se revela: «Hola Bob, nos vemos a las 18:00» |
| 1 bit alterado en la sal (canales 0..127) | error genérico (rechazado) |
| Recomprimida como JPEG calidad 90 ("enviar como foto") | error genérico (rechazado); cambiaron 460,743 de 921,600 bits menos significativos (50.0 %) |
| Recomprimida como JPEG calidad 100 | error genérico (rechazado) |

## 5. Tiempos (promedio de 5 ejecuciones)
| Operación | ms |
|---|---|
| Derivar claves (PBKDF2-SHA256, 100,000 iteraciones) | 104.3 |
| Cifrar 1 MB con ABCO (sin derivación) | 7.4 |
| Recorrido Fisher–Yates de 921,600 canales | 77.4 |
| Ocultar 64 B en 640×480 (total) | 153.7 |
| Revelar 64 B (total) | 199.6 |
| Romper César con χ² (26 claves, 200 caracteres) | 1.97 |

## 6. Espacio de claves
| Algoritmo | Claves posibles |
|---|---|
| César (A–Z) | 26 (25 útiles) |
| Matriz de Hill 2×2 invertible en ℤ₂₅₆ | \|GL₂(ℤ₂₅₆)\| = 2²⁸·6 = 1,610,612,736 |
| Parámetros del César dinámico (s₀, a, c) | 256 · 63 · 128 = 2,064,384 |
| Semilla del recorrido | 2⁶⁴ |
| Material de clave de ABCO | Derivado de la frase con PBKDF2 (32 B): la seguridad práctica depende de la frase |
