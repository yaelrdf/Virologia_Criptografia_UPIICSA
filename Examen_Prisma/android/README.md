# ABCO · App Android

Cliente Android (Kotlin + Jetpack Compose, Material 3) del sistema ABCO (antes PRISMA-L). Es un cliente
delgado: toda la criptografía se calcula en el servidor ASP.NET Core (contrato en `../docs/API.md`).

Paquete `mx.ipn.upiicsa.prisma` · minSdk 26 · target/compileSdk 37. El paquete, el `applicationId` y la
firma no cambiaron con el nuevo nombre, así que el APK se instala encima de las versiones anteriores.

## Funciones

- **Ocultar / Revelar**: modo ABCO (con frase) o esteganografía clásica (bits menos significativos en orden).
- **César** (cifrar, descifrar, romper con χ²) y **Comparador** (César, esteganografía clásica y ABCO).
- **Cuenta**: perfil, tema, cambiar contraseña y doble factor con imagen-llave.
- **Contraseñas**: «¿Olvidaste tu contraseña?» en el login indica contactar al administrador. Si el
  administrador la restableció (`mustChangePassword`), la app muestra la pantalla obligatoria
  «Cambia tu contraseña» antes de la navegación y usa el token nuevo que devuelve el servidor. Cambiar la
  contraseña desactiva el 2FA (hay que generar otra imagen-llave). Todos los campos de contraseña y frase
  van ocultos, con botón **Ver**.
- **Admin** (solo rol `admin`): usuarios con nombre ofuscado y botón **Revelar**; restablecer contraseña
  (muestra una sola vez la contraseña temporal, con **Copiar**), hacer o quitar administrador (muestra el
  error 409 del servidor) y restablecer 2FA; auditoría de las últimas 50 operaciones en hora local; traza
  de la última operación ABCO y su mapa de diferencias. Botón **Actualizar**.

## Tema

Paleta común con la web (verde sobre grises). En **Cuenta** y en el diálogo de ajustes del login hay un
selector **Automático / Claro / Oscuro** que se guarda en el teléfono (`prisma_settings`, el mismo archivo
que la URL del servidor). Automático sigue el modo del sistema. Las barras del sistema se ajustan al tema
elegido para que sus íconos se lean en ambos modos.

## Diseño adaptable

- Ventana < 600 dp de ancho: barra de navegación inferior (con 6 destinos, solo el seleccionado lleva
  etiqueta). ≥ 600 dp: NavigationRail con scroll vertical; con altura < 480 dp (teléfono en horizontal)
  solo el ítem seleccionado lleva etiqueta.
- Pantallas con formulario y resultado: con ≥ 560 dp de panel y sin resultado, las tarjetas del formulario
  se reparten en 2 columnas; con resultado, formulario | resultado. Ancho máximo 1200 dp.
- Login en 2 columnas (logo y descripción | formulario) desde 720 dp.
- Los contenedores con scroll usan `imePadding()` y los campos se desplazan a la vista al recibir el foco,
  para que el teclado no los tape.

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

`./gradlew :app:testDebugUnitTest` ejecuta contra `http://localhost:5080` o `PRISMA_TEST_SERVER`:

- el flujo completo de la API (registro, login con 2FA, ocultar, revelar, César, comparador de 3 resultados);
- restablecer contraseña como administrador → login con `mustChangePassword` → ruta bloqueada (403) →
  cambiar contraseña → el token nuevo funciona; además, rol (409 al quitarse el admin a uno mismo) y desofuscar.

El administrador se toma de `PRISMA_TEST_ADMIN` / `PRISMA_TEST_ADMIN_PASSWORD` (por defecto
`admin` / `Admin#Prisma2026`). Si el servidor no responde, las pruebas se omiten. Crea usuarios
`android_test_*` y `android_rst_*` en el servidor. Hace 8 peticiones a `/api/auth/*` (límite: 10 por minuto).

## Cambiar el servidor desde la app

En la pantalla de inicio de sesión, toca el ícono de ajustes (engrane; ahí también está el tema), escribe la URL,
usa **Probar conexión** (`GET /api/health`) y **Guardar**. La URL se guarda en el teléfono;
**Restablecer** vuelve al valor con el que se compiló.

La app acepta servidores `http://` (por ejemplo `http://dev-services.lan:5080` en la red local) y
`https://` (`res/xml/network_security_config.xml`). Con `http://` muestra un aviso de conexión sin cifrar:
úsalo solo en una red de confianza; en la nube usa HTTPS.

## Estructura

```
app/src/main/java/mx/ipn/upiicsa/prisma/
├── MainActivity.kt          Activity única (Compose)
├── AppViewModel.kt          sesión, servidor, catálogo y lógica de cada pantalla
├── data/
│   ├── ApiClient.kt         OkHttp + kotlinx.serialization, manejo de errores (401/413/429/red)
│   ├── Models.kt            modelos del contrato
│   ├── Files.kt             content:// URIs, decodificación, guardar y compartir (FileProvider)
│   └── ServerPrefs.kt       AppPrefs: URL del servidor y modo de tema en SharedPreferences
└── ui/
    ├── PrismaApp.kt         navegación (barra inferior / NavigationRail ≥ 600 dp)
    ├── FeatureStates.kt     estado observable de cada pantalla
    ├── components/          AdaptivePane (1 o 2 columnas), campos, selector de imágenes
    ├── screens/             Login, Ocultar, Revelar, César, Comparador, Cuenta (y cambio obligatorio de contraseña), Admin
    └── theme/               paleta común con la web, claro/oscuro
```

Íconos: `res/mipmap-*/ic_launcher_foreground.png` (panda dentro de la zona segura) sobre
`@color/ic_launcher_background` (#2B2D2F), más `ic_launcher`/`ic_launcher_round` PNG heredados.
El logo del login es `res/drawable-nodpi/logo_panda.png`.
