# Arquitectura

**SignalTrace** es un sistema de **rastreo de señales**. Teléfonos (navegador o app Android) y dispositivos simulados envían cada pocos segundos telemetría **cifrada de extremo a extremo** a un servidor .NET. La telemetría incluye:

- posición GPS;
- vibración del acelerómetro;
- batería;
- calidad del enlace medida por la propia app;
- intensidad de Wi-Fi y celular (solo Android).

El servidor verifica y descifra cada paquete, lo guarda, calcula métricas con métodos numéricos y muestra todo en vivo en un dashboard. Una **consola de administrador** muestra la encriptación paso a paso, y un **laboratorio** ejecuta ataques reales contra el protocolo.

![Arquitectura cliente-servidor de SignalTrace](diagramas/png/01_arquitectura.png)

## Componentes

| Componente | Tecnología | Función |
|---|---|---|
| Servidor | C# / .NET 10, ASP.NET Core Minimal APIs, SignalR, EF Core + SQLite | API REST, handshake, recepción y validación de paquetes, analítica, alertas, tiempo real |
| `SignalTrace.Core` | Biblioteca C# sin dependencias externas (solo `System.Security.Cryptography`) | SigMix, handshake, sobre AEAD, DTOs, criptoanálisis de SigMix. La usan el servidor, el simulador y las pruebas |
| Cliente web | HTML + CSS + JavaScript (módulos ES, sin framework), Web Crypto | Login, agente de telemetría (PWA), dashboard, consola, laboratorio, usuarios |
| App Android | Kotlin + Jetpack Compose, JCA (`javax.crypto`), servicio en primer plano | Telemetría continua con la pantalla bloqueada; Wi-Fi y celular |
| Simulador | Consola .NET, MQTTnet | N dispositivos virtuales con anomalías y huecos, por HTTP o MQTT |
| Broker MQTT | MQTTnet embebido en el servidor | Transporte alternativo: el mismo sobre cifrado en `st/{deviceId}/up` |
| Base de datos | SQLite | Usuarios, dispositivos, lecturas, paquetes (incluidos los rechazados) y alertas. Las llaves de sesión **nunca** se guardan |

## Flujo general

![Secuencia: registro, handshake y envío](diagramas/png/02_secuencia_handshake.png)

1. El usuario inicia sesión. La contraseña se verifica con PBKDF2 y recibe un JWT firmado con HMAC-SHA256.
2. Registra el teléfono como dispositivo y recibe un token de dispositivo.
3. El teléfono descarga la llave pública ECDSA del servidor. El usuario **compara la huella** con la que el servidor imprime al arrancar (o la toma del QR) y la **fija**.
4. **Handshake:** ECDH efímero, HKDF para las llaves de sesión y firma ECDSA del transcript, que el cliente verifica con la llave fijada.
5. Cada lectura se cifra con **SigMix** y luego con **AES-256-GCM**, y se envía en serie. Cada paquete lleva un `seq` estrictamente creciente.
6. El servidor valida en un orden estricto (siguiente diagrama), guarda la lectura, actualiza la analítica y la difunde por SignalR.

![Recepción y validación de un paquete en el servidor](diagramas/png/05_recepcion.png)

## Formato del sobre y del contenido

El sobre (`POST /api/telemetry`, con `Authorization: Bearer <token de dispositivo>`):

```
{ "v": 1, "sid": "a1b2c3d4e5f60718", "did": "6f1c2a3e-8b4d-4c5e-9a7f-0123456789ab",
  "seq": 1, "ts": 1760000000000, "ct": "<base64(ciphertext ‖ tag)>" }
```

El contenido en claro (antes de cifrar) es un JSON compacto. Todos los campos son opcionales salvo `t` y `src`:

```
{ "t": 1760000000000, "src": "android",
  "gps":  { "lat": 19.3955, "lon": -99.0915, "acc": 6.0, "spd": 1.3, "hdg": 87 },
  "mot":  { "ax": 0.1, "ay": 0.0, "az": 9.8, "rms": 0.05, "peak": 0.3, "fs": 32, "mag": [ … 64 muestras … ] },
  "bat":  { "lvl": 0.87, "chg": false },
  "net":  { "type": "wifi", "probe": { "rtt": 41.2, "jit": 3.1, "loss": 0.0, "kbps": 5200 } },
  "wifi": { "rssi": -61, "freq": 5180, "link": 433, "ssid": "h:…", "bssid": "h:…" },
  "cell": { "type": "LTE", "dbm": -98, "level": 3, "rsrp": -98, "rsrq": -11, "snr": 8 },
  "app":  { "vis": true, "ver": "1.0.0" } }
```

## Estructura del repositorio

```
Examen_SignalTrace/
├─ reference/                vectores de prueba (fuente de verdad) e implementaciones de referencia
├─ src/SignalTrace.Core/     criptografía y protocolo (C#)
├─ src/SignalTrace.Server/   API, SignalR, SQLite, analítica, laboratorio, MQTT, wwwroot (cliente web)
├─ src/SignalTrace.Simulator/
├─ android/                  app Kotlin + Compose
├─ tests/                    xUnit (Core y Server) y node --test (JS)
├─ docs/                     esta documentación, diagramas y capturas
└─ deploy/                   Dockerfile, docker-compose, guía de despliegue
```
