# SignalTrace para Android (Kotlin + Jetpack Compose)

La app usa el mismo protocolo que el cliente web: SigMix + AES-256-GCM o ChaCha20-Poly1305, llaves ECDH/HKDF y un handshake firmado con ECDSA y huella fijada. Agrega la señal Wi-Fi y la celular, y sigue enviando con la pantalla bloqueada gracias a un servicio en primer plano.

## Compilar

Hace falta JDK 17 o superior (sirve el JBR de Android Studio) y el Android SDK (platform 37).

```bash
export JAVA_HOME=/ruta/al/jbr
./gradlew :app:testDebugUnitTest                   # vectores del Apéndice A (y el servidor real si se define ST_TEST_SERVER)
./gradlew :app:assembleRelease [-PserverUrl=https://mi-servidor]
```

- **Prueba contra un servidor real:** define `ST_TEST_SERVER=http://localhost:5090` y `ST_TEST_PASSWORD=<contraseña de admin>` antes de correr las pruebas. Así se ejecuta el flujo completo de la app sin teléfono.
- **Firma release:** se lee de `keystore.properties` y `keystore/`. Ninguno de los dos se sube al repositorio. Sin ellos, la APK release sale sin firmar.
- **Verificar la firma:**

  ```bash
  apksigner verify --print-certs app/build/outputs/apk/release/app-release.apk
  ```

- **APK de la entrega:** `dist/SignalTrace.apk`, que también está en la carpeta del examen.

## Estructura

- `crypto/SigMix.kt` y `crypto/Protocol.kt`: SigMix, HKDF, ECDH, ECDSA (conversión de P1363 a DER), AES-GCM y ChaCha20-Poly1305.
- `data/Api.kt`: cliente REST (JVM puro). `data/Settings.kt`: preferencias y secretos cifrados con el Android Keystore.
- `telemetry/Collectors.kt`: GPS, acelerómetro, batería, red, Wi-Fi y celular.
- `telemetry/TelemetryEngine.kt`: muestreo, cola persistente, envío en serie, re-handshake y backoff.
- `telemetry/TelemetryService.kt`: servicio en primer plano de tipo `location`.
- `ui/`: pantallas Estado, Configurar (QR, huella y registro) y Ajustes.
