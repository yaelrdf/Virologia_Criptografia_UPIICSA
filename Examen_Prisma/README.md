# PRISMA-L — César + esteganografía LSB + algoritmo propio

Proyecto del 2.º parcial de **Virología y Criptografía (6NV68), UPIICSA–IPN**.

El sistema es cliente-servidor y trabaja con todos sus algoritmos:
- Dos algoritmos del material base:
  - **César**: laboratorio para cifrar, descifrar y romper con χ², y ofuscación de usuarios en la consola admin.
  - **Esteganografía LSB**: modo clásico, secuencial y sin clave.
- Un algoritmo inventado, **PRISMA-L**: un "César matricial" (Hill 2×2 mod 256 + desplazamiento César dinámico por LCG + encadenamiento XOR) oculto en un PNG con un recorrido de píxeles Fisher–Yates sembrado con la clave, más un HMAC que detecta frases incorrectas y alteraciones.
- **AES-256-GCM** como referencia comercial en el comparador.

La autenticación admite un **segundo factor opcional con imagen-llave**. Al activarlo, el servidor oculta con PRISMA-L un secreto aleatorio de 32 bytes en una imagen y solo guarda su SHA-256. A partir de entonces, para entrar hay que dar usuario, contraseña **y** la imagen.

```
src/Prisma.Core/         algoritmos (sin dependencias web)
src/Prisma.Api/          API REST ASP.NET Core 10 + SQLite + JWT; wwwroot = cliente web
tests/Prisma.Core.Tests/ pruebas xUnit (incluye vectores generados por el prototipo de Python)
tools/prototipo/         prototipo de referencia en Python y generador de vectores
tools/Prisma.Metrics/    mide las métricas que cita la documentación → docs/metricas.md
android/                 app Android (Kotlin + Jetpack Compose)
deploy/                  Dockerfile, docker-compose y guía de despliegue en la nube
docs/                    contrato de la API y métricas medidas
```

## Requisitos
- .NET SDK 10
- Para la app: JDK 17 o superior (sirve el JBR de Android Studio) y Android SDK 37

## Ejecutar el servidor en local
```bash
dotnet run --project src/Prisma.Api --launch-profile http
```
- Web: <http://localhost:5080> · API: `http://localhost:5080/api/...` · Salud: `/api/health`.
- En desarrollo se crea el usuario **`admin` / `Admin#Prisma2026`** (definido en `src/Prisma.Api/appsettings.Development.json`). **Cámbialo en producción** con la variable `Admin__Password`.
- La base SQLite y el catálogo de imágenes (generado por código la primera vez) quedan en `src/Prisma.Api/data/`.
- La traza paso a paso de PRISMA-L se imprime en la consola del servidor y se ve en **Admin** (desactívala con `Demo__Trace=false`).

## Pruebas y métricas
```bash
dotnet test
```
```bash
dotnet run --project tools/Prisma.Metrics -c Release -- src/Prisma.Api/data/catalog docs/metricas.md
```
Para regenerar los vectores de prueba desde el prototipo:
```bash
python3 -I tools/prototipo/gen_vectors.py > tests/Prisma.Core.Tests/vectors.json
```

## App Android
Consulta [`android/README.md`](android/README.md). La URL del servidor se cambia dentro de la app (ícono de ajustes en el login) o al compilar con `-PserverUrl=https://tu-dominio`.

## Despliegue en la nube
Consulta [`deploy/DEPLOY.md`](deploy/DEPLOY.md): un contenedor Docker, dos variables de entorno (`Jwt__Key`, `Admin__Password`) y un volumen en `/app/data`.

## API
Contrato completo en [`docs/API.md`](docs/API.md).

## Límites (declarados)
- PRISMA-L es **didáctico**: con suficientes pares de texto claro y cifrado admite ataques algebraicos, y su difusión es local, hacia adelante. Para proteger datos reales, usa AES-GCM.
- El LSB por reemplazo es detectable estadísticamente con ocupación alta (ver `docs/metricas.md`). Recomprimir la imagen (por ejemplo, enviarla "como foto" por mensajería) destruye el mensaje.
- El servidor procesa el mensaje en memoria durante la petición y no lo guarda (modelo de servidor de confianza).
