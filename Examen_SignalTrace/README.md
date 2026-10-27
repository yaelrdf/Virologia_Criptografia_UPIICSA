# SignalTrace — rastreo de señales con telemetría cifrada

Proyecto del 2.º parcial de **Virología y Criptografía (6NV68, UPIICSA-IPN)**. Es un sistema cliente-servidor:

- Teléfonos y navegadores (web/PWA, app Android, simulador) envían la telemetría de su **banco de sensores**: movimiento, orientación, entorno (luz, presión, etc.), batería, red y señal. El usuario elige qué sensores expone. No se recolecta la ubicación.
- Cada paquete se cifra con un **algoritmo inventado, Yael Crypto March1 (YCM1)**, y después con **AES-256-GCM** (o ChaCha20-Poly1305).
- Las llaves de sesión se acuerdan con **ECDH P-256 + HKDF-SHA256**.
- El servidor firma el handshake con **ECDSA P-256**, y los clientes fijan la huella de esa llave.
- Las contraseñas se guardan con **PBKDF2-HMAC-SHA256** y las sesiones son JWT **HMAC-SHA256**.

## Estructura

```
reference/                 vectores de prueba (fuente de verdad), su generador (gen_vectors.py) e implementaciones de referencia de YCM1
src/SignalTrace.Core/      Yael Crypto March1, handshake, sobre AEAD, DTOs, criptoanálisis de Yael Crypto March1 (C#)
src/SignalTrace.Server/    ASP.NET Core: API, SignalR, SQLite, analítica, laboratorio, puente MQTT
  wwwroot/                 cliente web sin framework (login, agente, dashboard, consola, laboratorio, usuarios)
src/SignalTrace.Simulator/ N dispositivos virtuales por HTTP o MQTT
android/                   app Kotlin + Jetpack Compose
tests/                     xUnit (Core y Server) y node --test (crypto del navegador)
docs/                      documentación (fuente del Word), diagramas y capturas
```

## Requisitos

- **.NET SDK 10.0.402** o superior. La versión se fija en `global.json`.
- **Node.js ≥ 20**. Solo hace falta para las pruebas de JavaScript y para `reference/verify.mjs`.

## Ejecutar el servidor

```bash
dotnet run --project src/SignalTrace.Server --launch-profile http
```

- El servidor escucha en **http://localhost:5090** y, para los teléfonos, en **https://<IP-de-la-LAN>:5443**, con un certificado autofirmado que genera en el primer arranque.
- Usuario inicial: **`admin` / `SignalTrace2026!`**, una contraseña fija definida en `appsettings.json` (`Admin:Password`). Se cambia desde el menú de usuario → «Cambiar contraseña».
- También imprime la **huella SHA-256** de la llave del servidor. Los clientes la comparan antes de fijarla.
- Los datos se guardan en `src/SignalTrace.Server/data/`, que el repositorio ignora:
  - la base SQLite,
  - la clave JWT,
  - la llave ECDSA de largo plazo;
  - el certificado TLS de la red local (`lan-cert.pfx`).
- La documentación interactiva de la API está en `/scalar` y el OpenAPI en `/openapi/v1.json`.

### Opciones (`appsettings.json` o `--Seccion:Clave=valor`)

| Clave | Por defecto | Efecto |
|---|---|---|
| `Telemetry:ReplayWindow` | `false` | Usa una ventana anti-replay de 64 bits en lugar de la monotonía estricta |
| `Crypto:AllowChaCha` | `true` | Ofrece la suite ChaCha20-Poly1305 |
| `Storage:EncryptAtRest` | `false` | Cifra `Readings.Json` con AES-256-GCM |
| `Console:Enabled` / `Console:ExposePlaintext` | `true` | Habilita la consola de administrador y la muestra del texto claro |
| `Lab:Enabled` / `Lab:EnableMitmDemo` / `Lab:ExposeKeys` | `true` / `true` / `false` | Configuran el laboratorio de ataques |
| `Mqtt:Enabled` / `Mqtt:Port` | `false` (`true` en Development) / `1883` | Activa el broker MQTT embebido |
| `Https:Enabled` / `Https:Port` | `true` / `5443` | HTTPS con certificado autofirmado para los teléfonos de la red local |
| `Https:AdvertiseUrl` | vacío | URL HTTPS que va en el QR si no se puede detectar (p. ej. en un contenedor) |
| `Server:PublicUrl` | vacío | URL pública (túnel o proxy con certificado real) que va en el QR en lugar de la de la LAN |
| `Admin:Password` | `SignalTrace2026!` | Contraseña del admin inicial |

## Agente web en un teléfono

Web Crypto y los sensores solo funcionan en un **contexto seguro**, es decir, HTTPS o `localhost`. El servidor ya sirve HTTPS en el puerto 5443.

**Lo más rápido: el QR.**

1. En el dashboard pulsa **Vincular teléfono (QR)**.
2. Escanéalo con la cámara del teléfono. Es una URL normal: `https://<IP>:5443/agent.html#enroll=CÓDIGO&fp=HUELLA&tls=CERT`.
3. Acepta una vez el aviso del certificado autofirmado.
4. El agente comprueba la huella de la llave del servidor contra la del QR, la fija y vincula el teléfono con el código de un solo uso, sin iniciar sesión.
5. Elige en la tabla **Sensores a exponer** qué sensores enviar y pulsa **Iniciar**.

La app Android escanea el mismo QR y además fija el certificado TLS.

**Otras formas:**

- Iniciar sesión en el agente, registrar el navegador y fijar la huella a mano.
- Con cable USB: `adb reverse tcp:5090 tcp:5090` y `http://localhost:5090/agent.html`.

El interruptor **Modo simulador** genera datos sintéticos de todo el banco de sensores para hacer la demostración desde una computadora de escritorio.

Con la pantalla bloqueada o la pestaña oculta, el navegador detiene los sensores y la telemetría queda **en pausa**. Es una limitación de la web, no un error. Para telemetría continua está la app Android.

## Simulador

```bash
dotnet run --project src/SignalTrace.Simulator -- --user admin --password 'SignalTrace2026!' --fingerprint <huella> --devices 10 --rate 1 --anomalies
```

Agrega `--gaps` para imitar pantallas bloqueadas. Hace pausas de 4 s, que el servidor interpola, y de 12 s, que quedan como hueco. Así el dashboard muestra la malla interpolada.

Para enviar por el broker MQTT con ChaCha20, agrega:

```
--transport mqtt --suite chacha20-poly1305
```

Notas:

- Los tokens de los dispositivos registrados se guardan en `sim-devices.json`, que el repositorio ignora.
- Si la huella del servidor no coincide con `--fingerprint`, el simulador se detiene sin enviar nada.

## Dashboard (`app.html`)

- **Lista de dispositivos:** se actualiza en vivo por SignalR. Al seleccionar uno:
- **Valores en tiempo real:** cada sensor que expone el dispositivo, agrupado y actualizado con cada paquete:
  - movimiento, orientación y entorno (con la altitud barométrica estimada);
  - batería y red;
  - analítica: frecuencia dominante, tendencias, EMA y z-scores.
- **Historial** de 15 min, 1 h, 6 h o 24 h. Gráficas:
  - RSSI con EMA, RTT y pérdida, movimiento con SMA y EMA;
  - giroscopio, orientación, luz, presión y batería con la proyección de la regresión;
  - espectro DFT/FFT.
  - Una ✕ roja marca las muestras con |z| > 3.
- **Malla temporal regular:** los puntos **interpolados** aparecen en naranja y los huecos largos como cortes.
- **Exportación** a CSV.
- **QR de vinculación:** una URL que sirve para la cámara del teléfono (agente web) y para la app Android.

## Consola, laboratorio y usuarios (solo admin)

- **`console.html`:** cada paquete aceptado o rechazado llega por SignalR. Se muestra por etapas:
  - bytes en el cable;
  - verificación;
  - nonce, AAD y etiqueta del AEAD;
  - Yael Crypto March1 ronda por ronda, con k, m, rot, la matriz K y los bytes que cambian;
  - texto claro.

  Incluye la entropía por etapa y una animación «Paso a paso».
- **`lab.html`:** «Ejecutar todos» corre 12 escenarios con un dispositivo de laboratorio real y los compara con el resultado esperado:
  - normal;
  - Eve;
  - bit flip;
  - replay;
  - AAD alterado;
  - MITM;
  - sesión perdida;
  - contenido inválido;
  - token ajeno;
  - ChaCha20;
  - ataque a Yael Crypto March1;
  - ejemplo de mano.
- **`admin.html`:**
  - alta y baja de usuarios;
  - **cambio de rol** y **restablecimiento de contraseña** (aleatoria, se muestra una vez);
  - la identidad del servidor y el historial de paquetes por estado.

Cualquier usuario cambia su propia contraseña desde el menú de usuario.

## Pruebas

```bash
dotnet test SignalTrace.slnx
```

```bash
node --test 'tests/web/*.test.mjs'
```

```bash
node reference/verify.mjs
```

```bash
python3 reference/gen_vectors.py   # regenera test-vectors.json (Python cryptography / OpenSSL)
```

## Límites honestos

- Yael Crypto March1 es un cifrador **académico**: es afín sobre Z₂₅₆, así que un ataque de texto claro conocido lo rompe. Por eso siempre va dentro de AES-GCM. El laboratorio demuestra el ataque.
- La primera conexión es de confianza en el primer uso (TOFU). Por eso la huella se compara fuera de banda, o llega dentro del QR que muestra el dashboard autenticado.
- Desde el navegador no existe API para el RSSI de Wi-Fi, la señal celular ni los sensores de entorno (presión, temperatura, humedad, proximidad, pasos). Solo la app Android puede medirlos.
- El certificado HTTPS de la red local es autofirmado: la app lo fija por su huella y el navegador pide aceptarlo una vez.
