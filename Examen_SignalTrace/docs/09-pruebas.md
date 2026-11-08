# Pruebas

La interoperabilidad se apoya en los **vectores de prueba** (`reference/test-vectors.json`). Los genera `reference/gen_vectors.py` con Python y `cryptography`/OpenSSL a partir de entradas fijas (llaves privadas, nonces, identificadores y un texto claro con sensores), y `verify.mjs` los verifica de forma independiente con Web Crypto. Las tres implementaciones del protocolo (C#, JavaScript y Kotlin) deben reproducirlos **byte a byte**.

Al renombrar el algoritmo a Yael Crypto March1 cambiaron sus etiquetas (`st1/ycm1` y `"YCM1"`), así que los vectores se regeneraron y las cuatro verificaciones se volvieron a pasar. Todas las pruebas de esta versión se ejecutaron el 27 de octubre de 2026 en la máquina de desarrollo.

## Resumen

| Suite | Herramienta | Pruebas | Resultado |
|---|---|---|---|
| Generación y autoverificación de los vectores | `python3 reference/gen_vectors.py` y `reference/ycm1.py` | ejemplo de mano, periodo del LCG, ida y vuelta 0–199 B | ✅ |
| Verificación independiente de los vectores | `node reference/verify.mjs` (Web Crypto) | 13 comprobaciones | ✅ todas |
| Core C#: vectores, propiedades y criptoanálisis | xUnit | 19 | ✅ 19/19 |
| Servidor: integración HTTP y SignalR, numéricos, datos, usuarios | xUnit + WebApplicationFactory | 31 | ✅ 31/31 |
| Criptografía del navegador | `node --test` | 9 | ✅ 9/9 |
| App Android: vectores en Kotlin | JUnit (JVM) | 9 | ✅ 9/9 |
| App Android: flujo completo contra el servidor real, incluido HTTPS con certificado fijado | JUnit (JVM) + servidor en ejecución | 1 (≈ 18 comprobaciones) | ✅ |
| Escenarios de ataque en vivo | Laboratorio («Ejecutar todos») | 12 | ✅ 12/12 |
| Carga: simulador, 10 dispositivos a 1 Hz por HTTP | `SignalTrace.Simulator` | 201 paquetes | ✅ 200 aceptados, 0 rechazados (1 en vuelo al terminar) |
| Carga: MQTT con ChaCha20, 2 dispositivos | `SignalTrace.Simulator --transport mqtt` | 40 paquetes | ✅ 40/40 |
| Resistencia: 6 dispositivos, 10 min, anomalías y huecos | `SignalTrace.Simulator` | 3046 paquetes | ✅ 3046/3046, latencia media de 42.7 ms |
| Segunda iteración: 6 dispositivos, 15 min, banco completo de sensores, con un reinicio deliberado del servidor y dos reinicios de sesiones del laboratorio | `SignalTrace.Simulator --anomalies --gaps` | 4591 paquetes | ✅ 4491 aceptados. Ningún rechazo criptográfico ni de validación. 24 `unknown_session` (reinicios) resueltos con un nuevo handshake automático. 76 fallos de red durante los ~40 s que el servidor estuvo apagado, reintentados con *backoff*. Latencia media de 110 ms, con las capturas y las pruebas corriendo a la vez en 2 núcleos |

## Vectores de interoperabilidad

| Comprobación | C# | JS | Kotlin |
|---|---|---|---|
| ECDH: coordenada X del secreto compartido (desde ambos lados) | ✅ | ✅ | ✅ |
| HKDF: `st1/aes-up`, `st1/ycm1`, `st1/iv-up` | ✅ | ✅ | ✅ |
| Transcript del handshake y verificación de la firma ECDSA (y rechazo si se altera) | ✅ | ✅ | ✅ |
| Yael Crypto March1: ejemplo de mano "Hi" → (236, 198) | ✅ | ✅ | ✅ |
| Yael Crypto March1: parámetros, las 10 etapas intermedias y la salida del paquete | ✅ | ✅ | ✅ |
| Nonce, AAD y `ct‖tag` de AES-256-GCM idénticos al vector | ✅ | ✅ | ✅ |
| PBKDF2 con 600 000 iteraciones | ✅ | (Web Crypto en verify.mjs) | — |

## Propiedades

- `Descifrar(Cifrar(x)) = x` para longitudes de 0 a 199 bytes con datos y llaves aleatorios (C#, JS y Kotlin).
- Invertir cualquier bit del texto cifrado, o cambiar `seq` o `ts`, hace fallar la etiqueta (AES-GCM y ChaCha20).
- Los parámetros de Yael Crypto March1 son deterministas para un mismo `(k_sig, seq)` y distintos para otro `seq`.
- El LCG recorre los 256 estados (Hull–Dobell) y det K = 1 para los 65 536 pares (k, m).
- Un relleno inválido se rechaza.
- El ataque de texto claro conocido rompe Yael Crypto March1 de 1 ronda en 5 casos aleatorios.
- `ResolverLineal` y el inverso modular dan resultados correctos (5⁻¹ ≡ 205 mod 256; 2x ≡ 3 no tiene solución).

## Integración del servidor (HTTP real en memoria)

- **Autenticación y roles:**
  - login correcto e incorrecto;
  - sin token (401);
  - roles: viewer → 403, token de dispositivo → 403.
- **Telemetría:**
  - flujo completo con la lectura visible y su carga de sensores (`p`);
  - texto cifrado alterado (422) **sin consumir el `seq`**;
  - replay (409);
  - sesión desconocida (404);
  - token de otro dispositivo (401);
  - contenido inválido (422) sin consumir el `seq`;
  - `t ≠ ts` (422);
  - JSON mal formado (400);
  - llave pública de longitud inválida (400);
  - suite ChaCha20;
  - ventana anti-replay de 64 bits;
  - cifrado en reposo.
- **Handshake y laboratorio:**
  - MITM (el cliente rechaza la firma);
  - ataque a Yael Crypto March1;
  - reinicio de sesiones.
- **Vinculación:** el QR es una URL `…/agent.html#enroll=…&fp=…&tls=…` y el código es de un solo uso.
- **SignalR:** autenticado; recibe `Reading` y `PacketTrace` con las 10 etapas y rechaza conexiones anónimas.
- **Datos:**
  - malla interpolada;
  - historial con z-scores móviles (la anomalía se conserva aunque se muestree);
  - **banco de sensores**: columnas de giroscopio, luz y presión, malla de luz, humedad fuera de rango → 422;
  - exportación CSV sin columnas de ubicación.
- **Usuarios:**
  - cambio de rol (el viewer no puede cambiarlo; el rol nuevo vale en el siguiente login);
  - el último administrador no se puede degradar (`last_admin`);
  - restablecer la contraseña (la anterior deja de servir y la nueva funciona);
  - cambiar la contraseña propia (exige la actual y una longitud mínima).

## Métodos numéricos

| Prueba | Valor esperado | Obtenido |
|---|---|---|
| DFT de un seno de 5 Hz, f_s = 32 Hz, N = 64 | pico en k = 10 con magnitud 0.5 | ✅ |
| FFT radix-2 frente a DFT directa | iguales (error < 10⁻⁹) | ✅ |
| Interpolación de y = 2t + 1 con hueco | error 0, 2 puntos marcados | ✅ |
| Hueco de 10 intervalos | queda vacío | ✅ |
| Regresión sobre una recta exacta | a = 80, b = −0.5, r² = 1 | ✅ |
| SMA, EMA y z-score | [1, 1.5, 2, 3, 4]; e₂ = 2.25; z = 3 | ✅ |

## Pruebas manuales en el navegador

- **Agente web:** registro, fijado de la huella, handshake y envío con AES-GCM y ChaCha20. También:
  - **vinculación con el QR sin sesión:** el agente abre con `#enroll=…`, comprueba la huella, la fija, canjea el código y transmite;
  - **tabla de sensores:** en modo simulador todos están disponibles; con sensores reales en el escritorio, los que solo existen en Android aparecen deshabilitados, y la selección se bloquea al transmitir;
  - reinicio de sesiones → 404 → nuevo handshake;
  - servidor detenido unos 10 s → la cola guarda 15 lecturas, sobrevive a una recarga y se envía en orden sin rechazos;
  - vista de teléfono (375 px) sin desplazamiento horizontal.
- **Dashboard:**
  - 6 dispositivos en vivo;
  - panel de valores en tiempo real con los seis grupos (movimiento, orientación, entorno, batería, señales y analítica), incluida la altitud barométrica (779 hPa ≈ 2 160 m);
  - historial de 15 min a 24 h con gráficas de giroscopio, orientación, luz y presión, y marcas de |z| > 3;
  - espectro, malla interpolada, QR y exportación CSV;
  - temas claro y oscuro, escritorio y teléfono, sin errores en la consola.
- **Interfaz en el teléfono (375 px):**
  - menú hamburguesa que se abre y se cierra (con Escape, al elegir un enlace o al pulsar fuera);
  - menú de usuario desplegable hacia abajo;
  - gráficas dentro del ancho de la pantalla;
  - sin desplazamiento horizontal en el dashboard, el agente, la consola, el laboratorio y usuarios.
- **Usuarios:** cambio de rol, restablecimiento con el diálogo de la contraseña de un solo uso y cambio de la contraseña propia desde el menú de usuario.
- **Errores encontrados y corregidos durante estas pruebas:**
  - el limitador de tasa se aplicaba por IP en lugar de por dispositivo;
  - el *backoff* del agente crecía con cada muestra en lugar de con cada reintento;
  - las pausas inflaban el intervalo nominal usado por la malla;
  - el ataque a Yael Crypto March1 solo recuperaba `rot mod n`;
  - (segunda iteración) un estilo `display: grid` anulaba el atributo `hidden` del panel del dispositivo;
  - (segunda iteración) la línea del azimut daba un salto vertical al pasar de 359° a 0°; ahora se corta;
  - (segunda iteración) la tabla de sensores decía «solo en la app Android» para el magnetómetro y la luz, que sí existen en algunos navegadores.
  - (ajustes finales) el laboratorio reutilizaba un dispositivo de laboratorio que se había revocado desde el dashboard; ahora comprueba que siga existiendo y, si no, registra uno nuevo.

![Consola: paquete rechazado por etiqueta inválida (bit flip del laboratorio)](capturas/web_consola_rechazo.png)
