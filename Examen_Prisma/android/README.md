# PRISMA-L · App Android

Cliente Android (Kotlin + Jetpack Compose, Material 3) del sistema PRISMA-L. Es un cliente delgado:
toda la criptografía se calcula en el servidor ASP.NET Core (contrato en `../docs/API.md`).

Paquete `mx.ipn.upiicsa.prisma` · minSdk 26 · target/compileSdk 37.

## Requisitos

- JDK 17 o superior (probado con el JBR 25 de Android Studio).
- Android SDK con la plataforma 37 y build-tools 36.0.0. `local.properties` debe tener `sdk.dir=...`.
- Gradle 9.8.1 (lo descarga el wrapper `./gradlew`).

## Compilar

```bash
export JAVA_HOME=/ruta/al/jdk
./gradlew :app:assembleRelease                                   # servidor por defecto: http://10.0.2.2:5080
./gradlew :app:assembleRelease -PserverUrl=https://midominio     # otro servidor por defecto
```

APK: `app/build/outputs/apk/release/app-release.apk` (copia de distribución en `dist/Prisma-L.apk`).

### Firma release

Si existe `keystore.properties` (no se versiona) el release se firma con esa llave:

```properties
storeFile=keystore/prisma-release.jks
storePassword=...
keyAlias=prisma
keyPassword=...
```

Sin ese archivo el APK release sale sin firmar. Verificación:
`$ANDROID_HOME/build-tools/36.0.0/apksigner verify --print-certs dist/Prisma-L.apk`.

### Prueba de integración

`./gradlew :app:testDebugUnitTest` ejecuta el flujo completo de la API (registro, login con 2FA, ocultar,
revelar, César, comparador) contra `http://localhost:5080` o `PRISMA_TEST_SERVER`. Si el servidor no
responde, la prueba se omite. Crea un usuario `android_test_*` en el servidor.

## Cambiar el servidor desde la app

En la pantalla de inicio de sesión, toca el ícono de ajustes (engrane), escribe la URL,
usa **Probar conexión** (`GET /api/health`) y **Guardar**. La URL se guarda en el teléfono;
**Restablecer** vuelve al valor con el que se compiló.

HTTP sin cifrar solo se permite hacia `10.0.2.2`, `localhost` y `127.0.0.1`
(`res/xml/network_security_config.xml`); cualquier otro servidor debe usar HTTPS.

## Estructura

```
app/src/main/java/mx/ipn/upiicsa/prisma/
├── MainActivity.kt          Activity única (Compose)
├── AppViewModel.kt          sesión, servidor, catálogo y lógica de cada pantalla
├── data/
│   ├── ApiClient.kt         OkHttp + kotlinx.serialization, manejo de errores (401/413/429/red)
│   ├── Models.kt            modelos del contrato
│   ├── Files.kt             content:// URIs, decodificación, guardar y compartir (FileProvider)
│   └── ServerPrefs.kt       URL del servidor en SharedPreferences
└── ui/
    ├── PrismaApp.kt         navegación (barra inferior / NavigationRail ≥ 600 dp)
    ├── FeatureStates.kt     estado observable de cada pantalla
    ├── components/          componentes comunes y selector de imágenes
    ├── screens/             Login, Ocultar, Revelar, César, Comparador, Cuenta
    └── theme/               tema claro/oscuro Material 3
```
