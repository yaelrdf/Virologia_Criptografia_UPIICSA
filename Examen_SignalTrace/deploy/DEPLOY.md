# Despliegue de SignalTrace

## 1. Local (demo en clase)

```bash
dotnet run --project src/SignalTrace.Server --launch-profile http
```

El servidor escucha en `http://0.0.0.0:5090` y en `https://0.0.0.0:5443`. Al arrancar imprime la huella SHA-256 de su llave. El usuario inicial es `admin` / `SignalTrace2026!`.

El teléfono necesita un **contexto seguro** para Web Crypto y los sensores:

- **HTTPS de la red local (recomendado):** el QR del dashboard ya apunta a `https://<IP>:5443`. El certificado es autofirmado: el navegador avisa una vez y la app Android lo fija por su huella.
- **Con cable:**

  ```bash
  adb reverse tcp:5090 tcp:5090
  ```

  y abre `http://localhost:5090/agent.html` en el teléfono.
- **Túnel HTTPS:**

  ```bash
  cloudflared tunnel --url http://localhost:5090
  ```

  Arranca el servidor con `--Server:PublicUrl=https://….trycloudflare.com` para que el QR use esa URL.

## 2. Contenedor

```bash
docker build -f deploy/Dockerfile -t signaltrace .
```

```bash
ST_HTTPS_URL=https://<IP-del-host>:5443 docker compose -f deploy/docker-compose.yml up --build
```

- La web y la API quedan en `http://localhost:8080`, el HTTPS para teléfonos en `5443` y el broker MQTT en el puerto `1883`.
- Dentro del contenedor no se conoce la IP de la red local, así que `ST_HTTPS_URL` (`Https__AdvertiseUrl`) le dice al QR a dónde apuntar.
- La contraseña inicial del admin es `SignalTrace2026!` salvo que se defina `ADMIN_PASSWORD`.
- **Monta siempre el volumen `/app/data`.** Ahí viven la base SQLite, la clave JWT y la llave ECDSA de largo plazo.
  - También vive ahí el certificado TLS de la red local; si se pierde, hay que volver a vincular las apps con un QR nuevo.
  - Si se pierde la llave, cambia la huella y los clientes que ya la fijaron rechazarán el servidor. Es lo correcto, porque una llave nueva no se distingue de un ataque.
- Para producción, pon un proxy TLS delante (Caddy, Nginx o el túnel).

## 3. Variables de entorno (`Seccion__Clave`)

| Variable | Uso |
|---|---|
| `Admin__Password` | Contraseña del admin inicial (por defecto `SignalTrace2026!`; solo se usa si no existe ningún administrador) |
| `Jwt__Key` | Clave HS256 en Base64 (32 B). Si falta, se genera en `/app/data/jwt.key` |
| `Server__PublicUrl` | URL pública (túnel o proxy con certificado real) que va en el QR de vinculación |
| `Https__Enabled`, `Https__Port`, `Https__AdvertiseUrl` | HTTPS autofirmado para la red local y la URL que se anuncia en el QR |
| `Lab__Enabled`, `Lab__EnableMitmDemo`, `Lab__ExposeKeys` | Configuran el laboratorio. Desactívalo fuera de clase |
| `Console__ExposePlaintext` | Muestra el texto claro en la consola (`false` en producción) |
| `Telemetry__ReplayWindow` | Ventana anti-replay de 64 bits |
| `Storage__EncryptAtRest`, `Storage__MasterKey` | Cifrado AES-GCM de las lecturas guardadas |
| `Mqtt__Enabled`, `Mqtt__Port` | Broker MQTT embebido |
| `Retention__Days` | Días que se conservan las lecturas y los paquetes |

## 4. Comprobación después de desplegar

```bash
curl -s http://localhost:8080/healthz
```

```bash
curl -s http://localhost:8080/api/server-key
```

Luego compara la huella con la del log de arranque, inicia sesión en `/` y ejecuta «Ejecutar todos» en `/lab.html`.
