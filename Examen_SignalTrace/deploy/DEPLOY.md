# Despliegue de SignalTrace

## 1. Local (demo en clase)

```bash
dotnet run --project src/SignalTrace.Server --launch-profile http
```

El servidor escucha en `http://0.0.0.0:5090`. Al arrancar imprime la huella SHA-256 de su llave. La primera vez también imprime la contraseña de `admin`.

El teléfono necesita un **contexto seguro** para Web Crypto, el GPS y los sensores. Hay dos formas de dárselo:

- **Con cable:**

  ```bash
  adb reverse tcp:5090 tcp:5090
  ```

  y abre `http://localhost:5090/agent.html` en el teléfono.
- **Túnel HTTPS:**

  ```bash
  cloudflared tunnel --url http://localhost:5090
  ```

  y abre la URL `https://…trycloudflare.com/agent.html`.

## 2. Contenedor

```bash
docker build -f deploy/Dockerfile -t signaltrace .
```

```bash
ADMIN_PASSWORD='cambia-esto' docker compose -f deploy/docker-compose.yml up --build
```

- La web y la API quedan en `http://localhost:8080` y el broker MQTT en el puerto `1883`.
- **Monta siempre el volumen `/app/data`.** Ahí viven la base SQLite, la clave JWT y la llave ECDSA de largo plazo.
  - Si se pierde la llave, cambia la huella y los clientes que ya la fijaron rechazarán el servidor. Es lo correcto, porque una llave nueva no se distingue de un ataque.
- Para producción, pon un proxy TLS delante (Caddy, Nginx o el túnel).

## 3. Variables de entorno (`Seccion__Clave`)

| Variable | Uso |
|---|---|
| `Admin__Password` | Contraseña del admin inicial (solo se usa si no existe ningún usuario) |
| `Jwt__Key` | Clave HS256 en Base64 (32 B). Si falta, se genera en `/app/data/jwt.key` |
| `Server__PublicUrl` | URL pública que va en el QR de vinculación (p. ej. la del túnel) |
| `Lab__Enabled`, `Lab__EnableMitmDemo`, `Lab__ExposeKeys` | Configuran el laboratorio. Desactívalo fuera de clase |
| `Console__ExposePlaintext` | Muestra el texto claro en la consola (`false` en producción) |
| `Telemetry__ReplayWindow` | Ventana anti-replay de 64 bits |
| `Storage__EncryptAtRest`, `Storage__MasterKey` | Cifrado AES-GCM de las lecturas guardadas |
| `Mqtt__Enabled`, `Mqtt__Port` | Broker MQTT embebido |
| `Alerts__WebhookUrl` | Envía cada alerta por POST |
| `Retention__Days` | Días que se conservan las lecturas y los paquetes |

## 4. Comprobación después de desplegar

```bash
curl -s http://localhost:8080/healthz
```

```bash
curl -s http://localhost:8080/api/server-key
```

Luego compara la huella con la del log de arranque, inicia sesión en `/` y ejecuta «Ejecutar todos» en `/lab.html`.
