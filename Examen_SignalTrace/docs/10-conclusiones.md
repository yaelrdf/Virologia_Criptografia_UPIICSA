# Conclusiones

SignalTrace integra en un sistema funcional un algoritmo propio (Yael Crypto March1) y siete primitivas criptográficas estándar. Cada una cumple una función concreta:

- **ECDH + HKDF** acuerdan llaves nuevas en cada sesión.
- **ECDSA** con huella fijada impide el ataque de intermediario.
- **AES-256-GCM** (o ChaCha20-Poly1305) da confidencialidad e integridad a cada paquete y autentica sus metadatos.
- **PBKDF2** protege las contraseñas.
- **HMAC-SHA256** firma las sesiones.
- **SHA-256** une todo con huellas y derivaciones: la huella de la llave del servidor y la del certificado TLS viajan en el QR de vinculación.

El ejercicio de **inventar un algoritmo** dejó una lección clara. Yael Crypto March1 es elegante para explicar en clase:

- su matriz tiene determinante 1, así que se invierte sin calcular inversos;
- el LCG tiene periodo completo por Hull–Dobell;
- la rotación es una permutación.

Pero su linealidad sobre Z₂₅₆ lo hace vulnerable a un ataque de texto claro conocido. El laboratorio lo demuestra en milisegundos. Por eso el diseño lo coloca *dentro* de AES-GCM y deriva parámetros distintos para cada paquete. La seguridad del sistema no depende del algoritmo propio.

El **orden de validación** importa tanto como los algoritmos. El servidor:

1. verifica la etiqueta AEAD antes de tocar el contenido;
2. consume el número de secuencia solo después de autenticar y validar, así un atacante no puede «quemar» secuencias;
3. no guarda las llaves de sesión.

Las tres implementaciones independientes (C#, JavaScript y Kotlin) coinciden byte a byte con los mismos vectores de prueba. Eso da confianza en la interoperabilidad y en la corrección.

Respecto al **sistema híbrido**, la plataforma también impone límites:

- el navegador no puede leer la señal Wi-Fi o celular ni varios sensores de entorno;
- el navegador pausa la telemetría con la pantalla bloqueada;
- la app Android lee todo el banco de sensores y sigue enviando con la pantalla bloqueada mediante un servicio en primer plano.

Esta diferencia se documentó y se refleja en la interfaz y en los datos:

- el agente web dice qué sensores ofrece el navegador y cuáles solo existen en la app;
- los huecos se interpolan y se marcan en el dashboard.

En la segunda iteración el sistema se ajustó a su propósito:

- se quitaron el GPS y el mapa, porque **no hace falta la ubicación** para rastrear señales y sensores, y así se protege mejor la privacidad;
- se quitaron las alertas, porque no había nada a qué alertar; el z-score se conserva para marcar anomalías;
- se quitó el contraste con César;
- se agregaron la gestión de usuarios y la vinculación por QR con una URL normal;
- el algoritmo propio tomó su nombre definitivo, **Yael Crypto March1**, con vectores de prueba regenerados y verificados en cuatro implementaciones.

**Trabajo futuro:**

- Probar la app y el agente web en dispositivos físicos con distintas versiones de Android y navegadores.
- Firmware para ESP32 (el sobre cifrado no depende del transporte).
- Autenticación mutua con certificados.
- Certificado de una autoridad (o un proxy) en lugar del autofirmado de la red local, y comprobar el rol en cada petición para que un cambio de rol se aplique de inmediato.

# Referencias

- NIST FIPS 197: *Advanced Encryption Standard (AES)*. NIST SP 800-38D: *Galois/Counter Mode (GCM) and GMAC*.
- NIST SP 800-56A Rev. 3: *Pair-Wise Key-Establishment Schemes Using Discrete Logarithm Cryptography* (ECDH). FIPS 186-5: *Digital Signature Standard* (ECDSA).
- RFC 5869: *HMAC-based Extract-and-Expand Key Derivation Function (HKDF)*. RFC 2104: *HMAC*. FIPS 180-4: *Secure Hash Standard* (SHA-256).
- RFC 8018: *PKCS #5: Password-Based Cryptography Specification v2.1* (PBKDF2). OWASP Password Storage Cheat Sheet (iteraciones recomendadas).
- RFC 7519: *JSON Web Token (JWT)*. RFC 8439: *ChaCha20 and Poly1305 for IETF Protocols*. RFC 4303 (ventana anti-replay de IPsec ESP).
- Hull, T. E. y Dobell, A. R. (1962). *Random Number Generators*. SIAM Review 4(3) (periodo completo de los LCG).
- Hill, L. S. (1929). *Cryptography in an Algebraic Alphabet*. American Mathematical Monthly 36(6).
- Cooley, J. W. y Tukey, J. W. (1965). *An Algorithm for the Machine Calculation of Complex Fourier Series*.
- Documentación de Microsoft .NET: `System.Security.Cryptography` (AesGcm, ECDiffieHellman, ECDsa, HKDF, Rfc2898DeriveBytes) y ASP.NET Core SignalR.
- MDN Web Docs: Web Crypto API, Geolocation API, DeviceMotionEvent, Screen Wake Lock API, Network Information API.
- Android Developers: Foreground services, `TelephonyManager`, `ConnectivityManager`/`WifiInfo`, Android Keystore.
- Material del curso «Virología y Criptografía», UPIICSA-IPN, grupo 6NV68.
