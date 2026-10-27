# Despliegue de ABCO en la nube

El servidor es **un solo contenedor**: la API REST y la web estática salen del mismo proceso (`Prisma.Api`).
Sirve cualquier proveedor que ejecute contenedores Docker detrás de HTTPS (Render, Railway, Fly.io, Azure App Service, Google Cloud Run, etc.).

## 1. Variables de entorno

| Variable | Obligatoria | Descripción |
|---|---|---|
| `Jwt__Key` | Sí | Secreto para firmar las sesiones JWT; al menos 32 caracteres aleatorios. Si falta, se genera uno al arrancar y todas las sesiones se pierden en cada reinicio. |
| `Admin__Password` | Sí, en el primer arranque | Contraseña del usuario `admin` (≥ 8). Solo se usa si todavía no existe un administrador. |
| `Admin__Username` | No | Nombre del administrador (por defecto `admin`). |
| `Demo__Trace` | No | `true` (por defecto) muestra la traza paso a paso de ABCO en la consola admin y en el log. Ponlo en `false` fuera de la demostración. |
| `Jwt__Hours` | No | Duración de la sesión en horas (por defecto 8). |

Genera una clave segura con:
```bash
openssl rand -base64 48
```

## 2. Almacenamiento persistente
Monta un **volumen o disco persistente en `/app/data`**. Ahí viven `prisma.db` (usuarios, hashes de la imagen-llave, auditoría) y el catálogo de imágenes generado. Sin volumen, cada redepliegue borra a los usuarios.

## 3. Construir y probar la imagen
Desde la carpeta `Examen_Prisma`:
```bash
docker build -f deploy/Dockerfile -t abco .
```
```bash
docker run --rm -p 8080:8080 -e Jwt__Key="$(openssl rand -base64 48)" -e Admin__Password='CambiaEsto#2026' -v prisma-data:/app/data abco
```
Abre `http://localhost:8080` y prueba `http://localhost:8080/api/health`.

## 4. Despliegue con Docker Compose y Cloudflare Tunnel (Recomendado)

Esta opción permite publicar el servicio con HTTPS y protección DDoS/WAF de Cloudflare sin abrir puertos en el router o firewall del servidor:

1. Crea un túnel en **Cloudflare Zero Trust** (`Networks` → `Tunnels` → `Create a tunnel`).
2. Configura el hostname público:
   - **Service**: `HTTP`
   - **URL**: `prisma:8080`
3. Copia el token del túnel.
4. Crea tu archivo `.env` a partir de `.env.example`:
   ```bash
   cp deploy/.env.example .env
   # Llena JWT_KEY, ADMIN_PASSWORD y TUNNEL_TOKEN
   ```
5. Inicia los contenedores:
   ```bash
   docker compose up -d --build
   ```

### Seguridad en redes de Docker implementada
- **Sin puertos expuestos al host:** la app no publica puertos (`ports:`), impidiendo el escaneo directo de la IP del servidor.
- **Red interna aislada (`internal_net` con `internal: true`):** la aplicación no tiene gateway hacia Internet ni puede iniciar conexiones hacia el exterior.
- **Red de túnel (`tunnel_net`):** solo `cloudflared` tiene salida al exterior para comunicarse con la red de Cloudflare y reenviar las solicitudes a `prisma:8080`.

## 5. Ejemplo: Render (Web Service con Docker)
1. Sube el repositorio a GitHub (rama con `Examen_Prisma/`).
2. En Render: **New → Web Service →** el repositorio. *Root Directory*: `Examen_Prisma`. *Runtime*: Docker. *Dockerfile Path*: `deploy/Dockerfile`.
3. *Environment*: agrega `Jwt__Key` y `Admin__Password`.
4. *Disks*: agrega un disco persistente con *Mount Path* `/app/data` (1 GB es suficiente).
5. Render detecta el puerto 8080 y publica `https://<tu-servicio>.onrender.com` con HTTPS.

En otros proveedores los pasos son equivalentes: imagen desde `deploy/Dockerfile`, puerto 8080, las dos variables y un volumen en `/app/data`. El servidor ya acepta las cabeceras `X-Forwarded-*` del proxy HTTPS.

## 6. Apuntar la app Android al servidor
- **Sin recompilar:** en la pantalla de inicio de sesión, toca el ícono de ajustes, escribe `https://<tu-dominio>` y pulsa "Probar conexión".
- **Como valor por defecto del APK:** recompila con la URL final:
  ```bash
  cd android && ./gradlew :app:assembleRelease -PserverUrl=https://<tu-dominio>
  ```

La app acepta `http://` (útil en la red local, con un aviso de conexión sin cifrar) y `https://`. En la nube **usa HTTPS**: con HTTP, la contraseña y la imagen-llave viajarían sin cifrar.

## 7. Comprobación posterior
1. `GET https://<tu-dominio>/api/health` → `{"status":"ok",…}`.
2. Entra a la web como `admin`, abre **Admin** y revisa que la auditoría registre tu login.
3. Crea un usuario, activa el 2FA, cierra sesión y vuelve a entrar con la imagen-llave desde la web y desde la app.
