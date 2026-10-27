# Introducción

La asignación del segundo parcial pide **inventar un algoritmo de encriptación** y construir un pequeño sistema que trabaje con **todos** los algoritmos: al menos dos comerciales más el inventado. Debe tener front-end con login, back-end en .NET y documentación paso a paso, con escenarios, pseudocódigo y diagramas de flujo.

**SignalTrace** responde con un escenario realista de **rastreo de señales**. Teléfonos y dispositivos envían, cifradas de extremo a extremo, las lecturas de su **banco de sensores**:

- movimiento y vibración, orientación y campo magnético;
- luz, presión, temperatura, humedad y pasos;
- batería, calidad del enlace e intensidad de señal Wi-Fi y celular.

Un servidor las verifica, las analiza con métodos numéricos y muestra en vivo los valores instantáneos de cada dispositivo junto con su historial. El usuario elige qué sensores expone y el sistema **no recolecta la ubicación**.

## Objetivos

- Diseñar e implementar un algoritmo propio, **Yael Crypto March1** (YCM1), que combina aritmética modular, una mezcla matricial invertible y una permutación, y analizar honestamente sus debilidades.
- Integrar Yael Crypto March1 con algoritmos comerciales estándar en un protocolo completo:
  - **AES-256-GCM**;
  - **ECDH P-256 + HKDF**;
  - **ECDSA**;
  - **PBKDF2**;
  - **HMAC-SHA256 (JWT)**;
  - **SHA-256**;
  - **ChaCha20-Poly1305**.
- Construir un sistema cliente-servidor **multiplataforma e híbrido**: web/PWA responsiva, app Android nativa y simulador, contra el mismo back-end .NET.
- Mostrar la encriptación **paso a paso** en una consola de administrador y demostrar ataques reales (escucha pasiva, modificación, reenvío e intermediario) en un laboratorio.
- Aplicar **álgebra lineal, matemáticas discretas y métodos numéricos** en el algoritmo y en la analítica.

## Matriz de requisitos

| Requisito de la asignación | Cómo lo cumple SignalTrace |
|---|---|
| Al menos 2 algoritmos comerciales | AES-256-GCM, ECDH P-256 + HKDF-SHA256, ECDSA P-256, PBKDF2-HMAC-SHA256, HMAC-SHA256 (JWT), SHA-256 y ChaCha20-Poly1305 |
| 1 algoritmo inventado | **Yael Crypto March1**: 3 rondas de flujo LCG, matriz 2×2 con det = 1 y rotación |
| Sistema que trabaje con todos los algoritmos | Cada paquete pasa por Yael Crypto March1 → AES-GCM con llaves ECDH/HKDF; el handshake se firma con ECDSA; el login usa PBKDF2 y JWT; el QR fija la llave y el certificado por su huella SHA-256 |
| Front-end con login | `index.html` (login), dashboard, agente, consola, laboratorio y usuarios (roles, restablecer y cambiar contraseña) en HTML + JS |
| Back-end .NET | ASP.NET Core (.NET 10), Minimal APIs, SignalR y EF Core |
| Base de datos (opcional) | SQLite: usuarios, dispositivos, lecturas, paquetes y alertas |
| Documentar los algoritmos paso a paso | Capítulo «Algoritmos de cifrado»: idea matemática, pasos, pseudocódigo, ejemplo numérico y archivo |
| Escenarios | Capítulo «Escenarios»: 14 escenarios, 12 de ellos ejecutados automáticamente en el laboratorio |
| Pseudocódigo y diagramas de flujo | En cada algoritmo, más 10 diagramas (arquitectura, secuencia, login, envío, recepción, Yael Crypto March1 cifrar y descifrar, analítica, ECDH/HKDF, ataque) |
| Opcional: consola que muestra la encriptación | `console.html`: cada paquete etapa por etapa y ronda por ronda, con entropía y animación |
| Opcional: login y otras funciones | Roles admin y viewer con gestión de usuarios, valores en tiempo real e historial, selección de sensores, QR de vinculación (URL normal, sin login), HTTPS con certificado fijado, exportación CSV, MQTT y Docker |
| Puntos extra: sistema híbrido web + móvil | Cliente web/PWA y **app Android nativa** (Kotlin) con el mismo protocolo y el mismo banco de sensores |
| Métodos numéricos | SMA, EMA, interpolación lineal, z-score, DFT/FFT, mínimos cuadrados, fórmula barométrica |
| Matemáticas discretas | Aritmética modular, LCG y Hull–Dobell, permutaciones, gcd y Euclides extendido, curvas elípticas, ventana anti-replay |
| Álgebra lineal | Matriz invertible e inversa en Yael Crypto March1, sistemas afines (criptoanálisis), DFT como transformación lineal, mínimos cuadrados, matriz de rotación (orientación) |
| Responsivo y adaptativo | CSS *mobile-first* con temas claro, oscuro y automático; probado a 375 px y en escritorio |
| Arquitectura cliente-servidor y multiplataforma | Navegador (cualquier SO), Android, simulador .NET y MQTT; servidor en Linux, Windows o Docker |

## Tecnologías

- **Servidor:** C# / .NET 10, ASP.NET Core, SignalR, Entity Framework Core + SQLite, MQTTnet, Scalar (documentación de la API).
- **Web:** HTML5, CSS, JavaScript (módulos ES, sin framework), Web Crypto, DeviceMotion/DeviceOrientation y Generic Sensor API, Chart.js, @microsoft/signalr, @noble/ciphers y qrcode-generator, todas copiadas localmente.
- **Android:** Kotlin, Jetpack Compose (Material 3), JCA (`javax.crypto`), `SensorManager`, Android Keystore, OkHttp con *certificate pinning*, kotlinx.serialization y zxing (QR).
- **Pruebas:** xUnit y WebApplicationFactory, `node:test`, JUnit (JVM) y una verificación independiente con Web Crypto.
