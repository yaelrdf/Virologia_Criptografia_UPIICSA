# App Android y API

## App Android (sistema híbrido)

La app **SignalTrace** para Android (Kotlin + Jetpack Compose) usa **el mismo protocolo y la misma API** que el cliente web. Por eso el sistema es un híbrido real web + móvil. Aporta lo que un navegador no puede medir:

| Dato | Web (Chrome) | App Android |
|---|---|---|
| GPS | Sí, solo con la página visible | Sí, también con la pantalla bloqueada (servicio en primer plano) |
| Acelerómetro | Sí, solo visible | Sí (`SensorManager`, remuestreado a 32 Hz) |
| Batería | Sí, solo en Chrome | Sí (`ACTION_BATTERY_CHANGED`) |
| **Wi-Fi: RSSI, frecuencia, velocidad, SSID/BSSID** | **No existe API** | Sí (`ConnectivityManager` + `WifiInfo`) |
| **Celular: dBm, RSRP, RSRQ, SNR, tecnología, celda** | **No existe API** | Sí (`TelephonyManager`, `CellInfo`) |
| Calidad de enlace propia (RTT, jitter, pérdida, kbps) | Sí | Sí |
| Con la pantalla bloqueada | En pausa | Continúa |

**Criptografía en la app** (`crypto/Protocol.kt`, `crypto/SigMix.kt`). Es la tercera implementación del protocolo, escrita con el JCA de Android:

- `KeyPairGenerator("EC")` con `secp256r1` y `KeyAgreement("ECDH")`, que da la coordenada X cruda de 32 B.
- HKDF-SHA256 escrito paso a paso (Extract y Expand con `HmacSHA256`).
- `Signature("SHA256withECDSA")`. La firma P1363 (r‖s) del servidor se convierte a DER antes de verificar.
- `AES/GCM/NoPadding` y `ChaCha20-Poly1305` (Android 9 o superior).
- **Android Keystore:** el token del dispositivo y la llave de anonimización se guardan cifrados con AES-256-GCM, con una llave que no se puede exportar del almacén seguro del teléfono.
- **Anonimización:** si se activa, el SSID y el BSSID se envían como `HMAC-SHA256(llave local, valor)`, y el identificador de celda no se envía.

**Componentes:**



- `TelemetryService`: servicio en primer plano de tipo `location`, con notificación permanente, acción «Detener» y *wake lock* parcial.
- `TelemetryEngine`: muestreo, cola persistente en archivo (máximo 500), envío en serie, re-handshake y *backoff*.
- Recolectores: GPS, movimiento, batería, red, Wi-Fi y celular.
- Tres pantallas: Estado, Configurar (QR, huella y registro) y Ajustes.

**Validación sin dispositivo:** en esta computadora no se pudo ejecutar un emulador. La app se validó con **pruebas JVM**:

1. `VectorTest`: 9 pruebas que reproducen los vectores del Apéndice A byte a byte.
2. `ServerIntegrationTest`: con las mismas clases `Api` y `Protocol` de la app, contra el servidor real hace:
   - login;
   - registro;
   - handshake con AES-GCM y con ChaCha20 y envío de paquetes (202);
   - replay (409);
   - rechazo de una llave fijada distinta (MITM);
   - contraseña incorrecta (401).

Lo único que falta comprobar en un teléfono real es la lectura de los sensores de radio y el servicio con la pantalla bloqueada.

![QR de vinculación que muestra el dashboard: URL, huella de la llave y código de un solo uso](capturas/web_qr.png)

**Compilar la APK:**

```
cd android
./gradlew :app:assembleRelease -PserverUrl=https://mi-tunel   # URL por defecto (opcional; se cambia en la app)
apksigner verify --print-certs app/build/outputs/apk/release/app-release.apk
```

La firma release se lee de `keystore.properties` y `keystore/`, que no se suben al repositorio.

## API REST

Las respuestas de error tienen la forma `{ "code": "…", "message": "…" }`. Los binarios van en Base64 y las fechas en milisegundos epoch. La documentación interactiva está en `/scalar` y el OpenAPI en `/openapi/v1.json`.

![Documentación interactiva de la API (Scalar + OpenAPI)](capturas/web_api.png)

| Método y ruta | Autorización | Descripción |
|---|---|---|
| `POST /api/auth/login` | — | `{username, password}` → `{token, expiresAt, role}`; 401 o 429 |
| `GET /api/auth/me` | usuario | Usuario y rol actuales |
| `GET /api/server-key` | — | `{publicKey, fingerprint, suites}` |
| `GET/POST /api/users`, `DELETE /api/users/{id}` | admin | Gestión de usuarios (PBKDF2) |
| `POST /api/devices` | usuario | `{name, kind}` → `{deviceId, deviceToken}` |
| `GET /api/devices` | usuario | Lista con estado, batería y última señal |
| `DELETE /api/devices/{id}` | admin | Revoca y borra |
| `POST /api/enroll` · `POST /api/enroll/redeem` | usuario · — | Código QR de un solo uso (10 min) · canje por un token de dispositivo |
| `POST /api/handshake` | dispositivo | `{deviceId, clientPublicKey, clientNonce, suite}` → `{sessionId, serverPublicKey, serverNonce, signature, expiresAt, suite}` |
| `POST /api/telemetry` | dispositivo | Sobre cifrado → `202 {ack}`; 400, 401, 404, 409, 422 o 429 |
| `GET /api/readings` · `/api/readings/grid` | usuario | Lecturas descifradas · malla regular con puntos interpolados |
| `GET /api/alerts` · `POST /api/alerts/{id}/ack` | usuario | Alertas |
| `GET /api/heatmap` | usuario | Media de la señal por celda |
| `GET /api/export/readings.csv` · `track.geojson` · `track.kml` | usuario | Exportaciones |
| `GET /api/packets` · `/api/console/traces` | admin | Historial de paquetes · trazas para la consola |
| `POST /api/ping` · `GET /api/ping/download` | — (con límite de tasa) | Sondeo de enlace |
| `GET /api/lab/config` · `POST /api/lab/reset-sessions` · `POST /api/lab/sigmix-attack` | admin + `Lab:Enabled` | Laboratorio |
| `GET /healthz` | — | Estado |
| SignalR `/hubs/live` | usuario | Eventos `Reading`, `DeviceStatus`, `Alert`, `Spectrum` y `PacketTrace` (solo admin); métodos `Subscribe` y `Unsubscribe` |

**Códigos de rechazo de telemetría:**

| Situación | HTTP | `code` |
|---|---|---|
| JSON mal formado o campos faltantes | 400 | `malformed` |
| Token inválido o revocado, o `did` distinto | 401 | `unauthorized` |
| Sesión desconocida o expirada | 404 | `unknown_session` |
| `seq` repetido o menor | 409 | `replay` |
| Etiqueta AEAD inválida | 422 | `auth_failed` |
| Contenido inválido (rango o esquema) | 422 | `invalid_payload` |
| Demasiadas solicitudes | 429 | `rate_limited` |
