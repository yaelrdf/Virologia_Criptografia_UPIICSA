# Limitaciones, modelo de amenazas y ética

## Modelo de amenazas

| Atacante | Capacidad | Defensa | Resultado |
|---|---|---|---|
| Atacante pasivo | Lee el tráfico de la red o del túnel | AES-256-GCM sobre Yael Crypto March1, con llaves de sesión ECDH | Solo ve `ct` y metadatos autenticados |
| Atacante activo: modifica | Altera bytes del sobre | Etiqueta GCM y AAD con did, seq y ts | `422 auth_failed`; no consume `seq` |
| Atacante activo: reenvía | Reenvía paquetes válidos | `seq` estrictamente creciente por sesión (o ventana de 64) | `409 replay` |
| Atacante activo: intermediario (MITM) | Sustituye la llave del handshake | Firma ECDSA del transcript y huella fijada | El cliente aborta |
| Robo del token de dispositivo | Envía en nombre del dispositivo | Token revocable y ligado a un `did` | Revocación desde el dashboard |
| Captura del QR de vinculación | Canjea el código antes que el teléfono | Código de un solo uso que expira a los 10 min; el QR solo se muestra en una sesión iniciada | Ventana corta; el dispositivo ajeno se puede revocar |
| Certificado TLS falso (app) | Intercepta el HTTPS de la red local | La app fija el certificado por su huella SHA-256 | Conexión rechazada |
| Fuerza bruta de contraseñas | Prueba contraseñas | PBKDF2 con 600 000 iteraciones, espera creciente y límite por IP | Muy costoso |
| Acceso a la base de datos | Lee SQLite | Hash PBKDF2; las llaves de sesión nunca se guardan; cifrado en reposo opcional | Sin contraseñas ni llaves |

## Límites honestos

- **Primera conexión (TOFU).** Si un atacante controla la red en el primer uso y el usuario no compara la huella, puede hacerse pasar por el servidor. La defensa es obtener la huella fuera de banda: en la consola del servidor o con el QR del dashboard, que ya está autenticado. Con el QR la comparación es automática.
- **Yael Crypto March1 es académico.** Es afín sobre Z₂₅₆ y se rompe con texto claro conocido (ver el capítulo de criptoanálisis). La seguridad real la da AES-GCM.
- **HTTPS autofirmado en la red local.** El servidor sirve HTTPS en el puerto 5443 con un certificado autofirmado:
  - la app Android lo fija por su huella;
  - el navegador pide aceptarlo una vez;
  - en el puerto 5090 (HTTP) los JWT viajan en claro, aunque la telemetría vaya cifrada de extremo a extremo.
  Fuera del laboratorio conviene un certificado de una autoridad (proxy o túnel) y `Server:PublicUrl`.
- **Cambios de rol.** El rol va dentro del JWT del usuario. Si un administrador degrada a alguien, el token de esa persona conserva el rol anterior hasta expirar (60 min como máximo).
- **Metadatos visibles.** El tamaño y la frecuencia de los paquetes, y el `did`, delatan actividad (análisis de tráfico). Esto queda fuera del alcance.
- **Sin canales laterales.** No hay endurecimiento contra canales laterales más allá de las comparaciones en tiempo constante.

## Lo que la web NO puede hacer

- No hay API web para el **RSSI de Wi-Fi ni la señal celular**, ni para la proximidad, la presión, la temperatura, la humedad o los pasos. Solo la app Android los obtiene. El magnetómetro y la luz ambiental solo existen en navegadores con Generic Sensor API.
- **Con la pantalla bloqueada o la pestaña oculta** el navegador detiene los sensores y los temporizadores. Los *service workers* no tienen acceso a sensores. La telemetría web queda **en pausa** y lo indica. El dashboard muestra el hueco, que interpola si es corto.
- `navigator.connection` (`downlink`, `rtt`) está topado por el navegador y no existe en Safari ni en Firefox. Por eso la calidad del enlace se **mide** con sondas propias (`/api/ping`).

## Lo que no se probó en esta entrega

- La app Android **no se ejecutó en un dispositivo ni en un emulador**: la computadora de desarrollo no tiene recursos suficientes. Sí se compiló, se firmó y se validó con pruebas JVM contra los vectores y contra el servidor real. Falta comprobar en un teléfono los sensores físicos, la intensidad Wi-Fi y celular, los permisos por versión de Android y el servicio con la pantalla bloqueada por 10 minutos o más.
- El agente web se probó en el navegador de escritorio, también con la vista de teléfono y en modo simulador, incluida la vinculación por QR sin sesión. Falta probarlo con los sensores reales de un teléfono por el HTTPS de la red local.
- La imagen Docker no se construyó aquí. Sí se verificaron sus pasos con `dotnet restore --locked-mode` y `dotnet publish`, y el binario publicado arrancó correctamente.

## Privacidad y ética

La ubicación, el SSID, el BSSID y los identificadores de celda permiten localizar a una persona. Por eso **SignalTrace no los recolecta**: no hay GPS ni mapa, y la app no pide permisos de ubicación. Reglas del proyecto:

- Usar SignalTrace **solo con dispositivos propios o con consentimiento**.
- La telemetría activa siempre es visible: notificación permanente en Android e indicador en la web.
- El usuario decide qué sensores expone, y solo esos salen del teléfono.
- Retención configurable (`Retention:Days`).
- Es un sistema académico.
