# SignalTrace — rastreo de señales con telemetría cifrada

Proyecto del 2.º parcial de **Virología y Criptografía (6NV68, UPIICSA-IPN)**. Es un sistema cliente-servidor:

- Teléfonos y navegadores (web/PWA, app Android, simulador) envían telemetría: GPS, movimiento, batería, red y señal.
- Cada paquete se cifra con un **algoritmo inventado (SigMix)** y después con **AES-256-GCM** (o ChaCha20-Poly1305).
- Las llaves de sesión se acuerdan con **ECDH P-256 + HKDF-SHA256**.
- El servidor firma el handshake con **ECDSA P-256**, y los clientes fijan la huella de esa llave.
- Las contraseñas se guardan con **PBKDF2-HMAC-SHA256** y las sesiones son JWT **HMAC-SHA256**.

## Estructura

```
reference/                 vectores de prueba (fuente de verdad) e implementaciones de referencia de SigMix
src/SignalTrace.Core/      SigMix, handshake, sobre AEAD, DTOs, criptoanálisis de SigMix (C#)
src/SignalTrace.Server/    ASP.NET Core: API, SignalR, SQLite, analítica, laboratorio, puente MQTT
  wwwroot/                 cliente web sin framework (login, agente, dashboard, consola, laboratorio)
src/SignalTrace.Simulator/ N dispositivos virtuales por HTTP o MQTT
tests/                     xUnit (Core y Server) y node --test (crypto del navegador)
```

## Requisitos

- **.NET SDK 10.0.402** o superior. La versión se fija en `global.json`.
- **Node.js ≥ 20**. Solo hace falta para las pruebas de JavaScript y para `reference/verify.mjs`.

## Ejecutar el servidor

```bash
dotnet run --project src/SignalTrace.Server --launch-profile http
```

- El servidor escucha en **http://localhost:5090**.
- En el primer arranque crea el usuario `admin` e imprime su contraseña **una sola vez**. También puedes fijarla con `--Admin:Password=...` o con la variable `Admin__Password`.
- También imprime la **huella SHA-256** de la llave del servidor. Los clientes la comparan antes de fijarla.
- Los datos se guardan en `src/SignalTrace.Server/data/`, que el repositorio ignora:
  - la base SQLite,
  - la clave JWT,
  - la llave ECDSA de largo plazo.
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
| `Alerts:WebhookUrl` | vacío | Envía cada alerta por POST a esa URL |

## Agente web en un teléfono

Web Crypto, el GPS y los sensores solo funcionan en un **contexto seguro**, es decir, HTTPS o `localhost`. Hay dos formas de lograrlo:

1. **Con cable USB:** ejecuta el comando de abajo y abre `http://localhost:5090/agent.html` en Chrome del teléfono.

   ```bash
   adb reverse tcp:5090 tcp:5090
   ```

2. **Sin cable:** abre un túnel HTTPS, por ejemplo con el comando de abajo.

   ```bash
   cloudflared tunnel --url http://localhost:5090
   ```

Pasos en el agente:

1. Inicia sesión.
2. Registra el navegador.
3. Compara y **fija la huella**.
4. Pulsa **Iniciar**.

El interruptor **Simulador** genera datos sintéticos para hacer la demostración desde una computadora de escritorio.

Con la pantalla bloqueada o la pestaña oculta, el navegador detiene los sensores y la telemetría queda **en pausa**. Es una limitación de la web, no un error. Para telemetría continua está la app Android.

## Simulador

```bash
dotnet run --project src/SignalTrace.Simulator -- --user admin --password <contraseña> --fingerprint <huella> --devices 10 --rate 1 --anomalies
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

- **Lista de dispositivos:** se actualiza en vivo por SignalR.
- **Mosaicos:** muestran la última lectura, la señal con su pendiente por mínimos cuadrados, el RTT y el jitter, la batería con el tiempo restante estimado, la distancia (Haversine), la velocidad y la frecuencia dominante.
- **Mapa (Leaflet + OpenStreetMap):** muestra la ruta, puntos coloreados por la intensidad de la señal y un mapa de calor de cobertura por celda de 25 m. Necesita internet para las teselas.
- **Gráficas:** RSSI con su EMA, RTT y pérdida medidos, movimiento con SMA y EMA, espectro DFT/FFT de la vibración y batería con la proyección de la regresión.
- **Malla temporal regular:** los puntos **interpolados** aparecen en naranja y los huecos largos como cortes.
- **Alertas:** se reciben en vivo y se pueden marcar como atendidas.
- **Exportaciones:** CSV, GeoJSON y KML.
- **QR de vinculación:** sirve para la app Android.

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

## Límites honestos

- SigMix es un cifrador **académico**: es afín sobre Z₂₅₆, así que un ataque de texto claro conocido lo rompe. Por eso siempre va dentro de AES-GCM. El laboratorio demuestra el ataque.
- La primera conexión es de confianza en el primer uso (TOFU). Por eso la huella se compara fuera de banda.
- Desde el navegador no existe API para leer el RSSI de Wi-Fi ni la señal celular. Solo la app Android puede medirlos.
