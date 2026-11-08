# SignalTrace para Android (Kotlin + Jetpack Compose)

La app usa el mismo protocolo que el cliente web: Yael Crypto March1 + AES-256-GCM o ChaCha20-Poly1305, llaves ECDH/HKDF y un handshake firmado con ECDSA y huella fijada. Expone **todo el banco de sensores** del teléfono (por defecto todos activos y configurables uno por uno en Ajustes):

- **Movimiento:** acelerómetro (con espectro), aceleración lineal, gravedad y giroscopio.
- **Orientación:** magnetómetro y orientación a partir del vector de rotación.
- **Entorno:** luz, proximidad, presión, temperatura, humedad y podómetro.
- **Dispositivo y señales:** batería (nivel, temperatura, voltaje, salud), red, sondeo del enlace e intensidad Wi-Fi y celular.

**No usa la ubicación.** No pide permisos de ubicación ni lee el SSID, el BSSID ni los identificadores de celda. Sigue enviando con la pantalla bloqueada gracias a un servicio en primer plano de tipo `specialUse`.

## Compilar

Hace falta JDK 17 o superior (sirve el JBR de Android Studio) y el Android SDK (platform 37).

```bash
export JAVA_HOME=/ruta/al/jbr
./gradlew :app:testDebugUnitTest                   # vectores YCM1 (y el servidor real si se define ST_TEST_SERVER)
./gradlew :app:assembleRelease [-PserverUrl=https://mi-servidor]
```

- **Prueba contra un servidor real:** define `ST_TEST_SERVER=http://localhost:5090` y `ST_TEST_PASSWORD=<contraseña de admin>` antes de correr las pruebas. Así se ejecuta el flujo completo de la app sin teléfono. Con `ST_TEST_TLS=https://localhost:5443` también se prueba la vinculación por QR sobre HTTPS con el certificado fijado.
- **Firma release:** se lee de `keystore.properties` y `keystore/`. Ninguno de los dos se sube al repositorio. Sin ellos, la APK release sale sin firmar.
- **Verificar la firma:**

  ```bash
  apksigner verify --print-certs app/build/outputs/apk/release/app-release.apk
  ```

- **APK de la entrega:** `dist/SignalTrace.apk`, que también está en la carpeta del examen.

## Estructura

- `crypto/YaelCryptoMarch1.kt` y `crypto/Protocol.kt`: Yael Crypto March1, HKDF, ECDH, ECDSA (conversión de P1363 a DER), AES-GCM y ChaCha20-Poly1305.
- `data/Api.kt`: cliente REST (JVM puro) con *pinning* del certificado TLS autofirmado (`PinnedCertTrust`). `data/Settings.kt`: preferencias y secretos cifrados con el Android Keystore.
- `telemetry/Collectors.kt`: acelerómetro, banco de sensores (`SensorBankCollector`), batería, red, Wi-Fi y celular.
- `telemetry/TelemetryEngine.kt`: muestreo, cola persistente, envío en serie, re-handshake y backoff.
- `telemetry/TelemetryService.kt`: servicio en primer plano de tipo `specialUse`.
- `ui/`: pantallas Estado, Configurar (QR, huella y registro) y Ajustes (sensores expuestos, intervalo y suite).

## Vincular con QR

El QR del dashboard es una URL normal, `https://<IP>:5443/agent.html#enroll=CÓDIGO&fp=HUELLA&tls=CERT`. La cámara de cualquier teléfono abre con ella el agente web. La app la lee con «Escanear QR» y hace lo siguiente:

1. Toma el origen como URL del servidor.
2. Fija el certificado TLS (`tls`).
3. Comprueba que la llave del servidor coincida con `fp` antes de fijarla.
4. Canjea el código de un solo uso.
