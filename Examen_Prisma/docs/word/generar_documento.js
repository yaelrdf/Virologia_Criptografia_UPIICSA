// Genera la documentación del sistema PRISMA-L en Word.
// Uso (desde Examen_Prisma):  NODE_PATH=<carpeta con node_modules/docx> node docs/word/generar_documento.js <salida.docx>
const fs = require("fs");
const path = require("path");
const L = require("./lib");
const { D, P, H1, H1keep, H2, H3, bullets, steps, code, tbl, img, markdown, pageBreak, runs } = L;
const { Document, Packer, Paragraph, TextRun, AlignmentType, TableOfContents, Header, Footer, PageNumber, HeadingLevel, BorderStyle } = D;

const ROOT = path.resolve(__dirname, "..", "..");
const DIAG = (n) => path.join(ROOT, "docs", "diagramas", "png", n + ".png");
const SHOT = (n) => path.join(ROOT, "docs", "capturas", n);
const out = process.argv[2] || path.join(ROOT, "Documentacion_Sistema_PRISMA-L.docx");
const metricas = fs.readFileSync(path.join(ROOT, "docs", "metricas.md"), "utf8");
const traza = fs.readFileSync(path.join(ROOT, "docs", "traza_ejemplo.txt"), "utf8");
const androidShots = fs.existsSync(path.join(ROOT, "docs", "capturas"))
  ? fs.readdirSync(path.join(ROOT, "docs", "capturas")).filter((f) => f.startsWith("android_")).sort() : [];
const androidCaptions = {
  "android_01_login.png": "App Android: inicio de sesión con acceso a la configuración del servidor.",
  "android_02_login_2fa.png": "App Android: segundo paso del inicio de sesión (selección de la imagen-llave).",
  "android_03_ocultar.png": "App Android: ocultar un mensaje con una imagen del catálogo.",
  "android_04_resultado.png": "App Android: resultado con PSNR y opciones de guardar o compartir como archivo.",
  "android_05_revelar.png": "App Android: revelar un mensaje.",
  "android_06_cesar.png": "App Android: laboratorio César con ataque χ².",
  "android_07_comparador.png": "App Android: comparador de algoritmos.",
  "android_08_cuenta.png": "App Android: cuenta y activación del 2FA.",
};

const body = [];
const add = (...xs) => xs.flat().forEach((x) => body.push(x));

// ───────────────────────── Portada ─────────────────────────
const center = (t, size, opts = {}) => new Paragraph({ alignment: AlignmentType.CENTER, spacing: { after: opts.after ?? 120 }, children: [new TextRun({ text: t, size, bold: opts.bold, color: opts.color })] });
add(
  new Paragraph({ spacing: { before: 1200 }, children: [] }),
  center("INSTITUTO POLITÉCNICO NACIONAL", 32, { bold: true }),
  center("Unidad Profesional Interdisciplinaria de Ingeniería y Ciencias Sociales y Administrativas (UPIICSA)", 22, { after: 600 }),
  center("Virología y Criptografía · Grupo 6NV68", 26, { after: 900 }),
  center("Sistema PRISMA-L", 56, { bold: true, color: L.ACCENT, after: 200 }),
  center("Cifrado César, esteganografía LSB y un algoritmo propio", 30, { after: 120 }),
  center("con autenticación de dos factores por imagen-llave", 30, { after: 900 }),
  center("Documentación técnica · Proyecto del segundo parcial", 24, { after: 1200 }),
  center("Integrante(s): ____________________________________", 22),
  center("Profesor(a): ____________________________________", 22, { after: 600 }),
  center("Octubre de 2026", 22),
);

// ───────────────────────── Índice ─────────────────────────
add(
  new Paragraph({ pageBreakBefore: true, children: [new TextRun({ text: "Contenido", bold: true, size: 32, color: L.ACCENT })], spacing: { after: 200 } }),
  new TableOfContents("Contenido", { hyperlink: true, headingStyleRange: "1-3" }),
  P("*Si el índice aparece vacío, en Word usa clic derecho → Actualizar campo (o F9).*", { align: AlignmentType.LEFT }),
);

// ───────────────────────── 1. Introducción ─────────────────────────
add(
  H1("1. Introducción"),
  P("Este documento describe **PRISMA-L**, un sistema cliente-servidor que trabaja con tres algoritmos de ocultamiento y cifrado: dos tomados del material base del curso (**cifrado César** y **esteganografía LSB**) y uno inventado por el equipo (**PRISMA-L**), que combina a los dos anteriores con álgebra lineal, matemáticas discretas y métodos numéricos. Como referencia comercial, el sistema incluye además **AES-256-GCM**."),
  P("El sistema permite ocultar un mensaje dentro de una imagen PNG, revelarlo con una frase secreta, estudiar y romper el cifrado César, comparar los algoritmos con métricas, y proteger las cuentas con un **segundo factor de autenticación (2FA) basado en una imagen-llave**: un PNG que contiene, oculto con PRISMA-L, un secreto que solo el servidor puede verificar."),
  H2("1.1 Objetivos"),
  ...bullets([
    "Implementar y documentar, paso a paso, al menos dos algoritmos del material base (César y LSB) y un algoritmo propio (PRISMA-L).",
    "Construir un sistema completo en el que participen **todos** los algoritmos: backend .NET con API REST, cliente web responsivo y aplicación móvil Android (Kotlin).",
    "Justificar el algoritmo propio con matemáticas discretas, álgebra lineal y métodos numéricos, y **medir** sus propiedades en lugar de suponerlas.",
    "Declarar con honestidad los límites de cada algoritmo y el modelo de amenaza del sistema.",
  ]),
  H2("1.2 Cumplimiento de requisitos"),
  ...tbl(["Requisito", "Cómo se cumple"], [
    ["Mínimo 2 algoritmos del material base", "**César** (guía §3, PDF §2.3.1) y **esteganografía LSB** (guía §15, PDF §2.3.2), cada uno con funciones propias en el sistema."],
    ["1 algoritmo inventado", "**PRISMA-L**: Hill 2×2 mod 256 + César dinámico (LCG) + encadenamiento XOR, oculto con LSB en un recorrido Fisher–Yates y autenticado con HMAC (sección 6)."],
    ["Un sistema que trabaje con todos", "Login con imagen-llave (PRISMA-L sobre LSB), Ocultar/Revelar (PRISMA-L o LSB clásico), laboratorio César, ofuscación César en la consola admin y comparador con los cuatro algoritmos."],
    ["Front-end con login", "Web responsiva y app Android; login con usuario y contraseña, más imagen-llave cuando el usuario activa el 2FA."],
    ["Backend .NET, REST, cliente-servidor", "ASP.NET Core 10 Web API con SQLite y JWT; la web y Android consumen la misma API."],
    ["Documentar paso a paso, escenarios, seudocódigo y diagramas de flujo", "Secciones 4 a 8 (algoritmos y autenticación) y 10 (escenarios)."],
    ["Métodos numéricos, matemáticas discretas, álgebra lineal", "Sección 3 y métricas medidas en la sección 9."],
    ["Opcional: consola de administrador que muestre la encriptación", "Consola admin web con traza paso a paso de PRISMA-L, mapa de diferencias, estegoanálisis χ² y auditoría; la traza también se imprime en la consola del servidor."],
    ["Puntos adicionales: sistema híbrido web + móvil", "Web responsiva y APK Android firmado, contra el mismo backend."],
  ], [0.32, 0.68], "Matriz de cumplimiento de la consigna."),
);

// ───────────────────────── 2. Arquitectura ─────────────────────────
add(
  H1("2. Arquitectura del sistema"),
  P("PRISMA-L sigue una arquitectura **cliente-servidor**. Todo el cálculo criptográfico vive en una sola biblioteca de .NET (`Prisma.Core`), así que hay **una sola implementación** de cada algoritmo; la web y la app Android son clientes delgados que suben y descargan imágenes y JSON por HTTPS."),
  ...img(DIAG("arquitectura"), "Arquitectura cliente-servidor de PRISMA-L.", { maxH: 3 }),
  ...tbl(["Capa", "Tecnología", "Responsabilidad"], [
    ["Núcleo", "C# / .NET 10 (`Prisma.Core`), SkiaSharp para PNG", "César, LSB clásico, PRISMA-L, AES-GCM, métricas y estegoanálisis"],
    ["API", "ASP.NET Core 10 Minimal APIs, EF Core + SQLite, JWT", "Autenticación y 2FA, ocultar/revelar, César, comparador, administración y auditoría"],
    ["Web", "HTML + CSS + JavaScript sin framework (`wwwroot`)", "Interfaz responsiva servida por la misma API"],
    ["Móvil", "Kotlin + Jetpack Compose, OkHttp, kotlinx.serialization", "App Android con las mismas funciones; URL del servidor configurable"],
    ["Despliegue", "Docker (imagen `aspnet:10.0`), volumen `/app/data`", "Servidor en la nube detrás de HTTPS"],
    ["Pruebas", "xUnit (30 pruebas), prototipo en Python, prueba de integración Android", "Vectores Python = C#, propiedades matemáticas, ida y vuelta, ataques, contrato de la API"],
  ], [0.14, 0.4, 0.46], "Tecnologías por capa."),
  H2("2.1 Modelo de datos"),
  ...tbl(["Tabla", "Campos", "Notas"], [
    ["Users", "Id, Username, PwdHash, PwdSalt, TwoFactorEnabled, KeyTokenHash, Role, CreatedUtc", "La contraseña se guarda con PBKDF2-HMAC-SHA256 (600 000 iteraciones) y sal. Del 2FA solo se guarda **SHA-256 del token**, nunca el token."],
    ["Audit", "Id, UserId, Username, Action, Mode, Bytes, Ms, Ok, Utc", "Bitácora sin secretos; en la consola el usuario se muestra ofuscado con César."],
  ], [0.12, 0.43, 0.45], "Tablas de la base de datos SQLite."),
  H2("2.2 Estructura del repositorio"),
  ...code(`Examen_Prisma/
  src/Prisma.Core/          algoritmos: Caesar, LsbClassic, PrismaKeys, PrismaCipher,
                            Lcg64/FisherYates, PrismaStego, AesGcmRef, Metrics, Steganalysis
  src/Prisma.Api/           API REST + wwwroot (web) + SQLite + JWT
  tests/Prisma.Core.Tests/  pruebas xUnit y vectores del prototipo
  tools/prototipo/          prototipo de referencia en Python y generador de vectores
  tools/Prisma.Metrics/     mide las métricas de este documento
  android/                  app Kotlin + Jetpack Compose
  deploy/                   Dockerfile, docker-compose y guía de despliegue
  docs/                     API, métricas, diagramas, capturas y este documento`),
);

// ───────────────────────── 3. Fundamentos matemáticos ─────────────────────────
add(
  H1("3. Fundamentos matemáticos"),
  P("La consigna pide apoyarse en matemáticas discretas, álgebra lineal y métodos numéricos. La tabla resume dónde aparece cada área en el sistema y cómo se comprueba."),
  ...tbl(["Área", "Dónde se usa", "Evidencia"], [
    ["Matemáticas discretas", "Aritmética modular en ℤ₂₆ (César) y ℤ₂₅₆ (PRISMA-L). Unidades de ℤ₂₅₆: los impares (φ(256) = 128). **Euclides extendido** para inversos. **Teorema de Hull–Dobell** para el periodo del generador congruencial. **Permutaciones** (grupo simétrico Sₙ) con Fisher–Yates. XOR como suma en GF(2). Conteo |GL₂(ℤ₂₅₆)| = 2²⁸·6.", "Pruebas: periodo 256 del LCG, el recorrido no repite posiciones, conteo de GL₂ por fuerza bruta en ℤ₁₆."],
    ["Álgebra lineal", "Cifrado de Hill: producto matriz-vector, determinante, invertibilidad, **matriz adjunta** y A·A⁻¹ = I. Ataque de texto claro conocido A = C·P⁻¹.", "Prueba A·A⁻¹ = I con 2 000 matrices; ataque que recupera la matriz de Hill puro y falla contra PRISMA-L."],
    ["Métodos numéricos", "**Entropía de Shannon**, **MSE/PSNR**, **χ²** contra frecuencias del español, estimación **Monte Carlo** de la avalancha, función **gamma incompleta regularizada** (serie y fracción continua de Lentz) y **aproximación de Lanczos** de ln Γ para el estegoanálisis.", "Prueba P(1, x) = 1 − e⁻ˣ y Γ(5) = 24; tablas de la sección 9."],
  ], [0.18, 0.52, 0.30], "Justificación matemática."),
  H2("3.1 Aritmética modular y el inverso en ℤ₂₅₆"),
  P("En ℤ₂₅₆ un número x tiene inverso multiplicativo si y solo si mcd(x, 256) = 1, es decir, si x es **impar**. El inverso se obtiene con el algoritmo de Euclides extendido, que encuentra enteros s, t con s·x + t·256 = 1; entonces x⁻¹ ≡ s (mod 256). Por ejemplo, 11·163 = 1793 = 7·256 + 1, así que 11⁻¹ = 163."),
  ...code(`FUNCIÓN InversoMod(x, m = 256):
    (r0, r1, s0, s1) ← (x mod m, m, 1, 0)
    MIENTRAS r1 ≠ 0:
        q ← r0 div r1
        (r0, r1) ← (r1, r0 − q·r1)
        (s0, s1) ← (s1, s0 − q·s1)
    SI r0 ≠ 1 ENTONCES ERROR "no invertible"
    DEVOLVER s0 mod m`),
  H2("3.2 Matrices invertibles e inversa por adjunta"),
  P("Una matriz A = [[a, b], [c, d]] sobre ℤ₂₅₆ es invertible si y solo si det(A) = ad − bc es invertible, es decir, impar. Su inversa es A⁻¹ = det(A)⁻¹ · [[d, −b], [−c, a]] (mod 256). El número de matrices invertibles es |GL₂(ℤ₂₅₆)| = 2⁴⁽⁸⁻¹⁾ · |GL₂(ℤ₂)| = 2²⁸ · 6 = 1 610 612 736; la fórmula se verificó por fuerza bruta en ℤ₁₆ (24 576 matrices)."),
  H2("3.3 Generador congruencial y teorema de Hull–Dobell"),
  P("El generador sᵢ = (a·sᵢ₋₁ + c) mod m tiene periodo completo m si y solo si: (1) mcd(c, m) = 1; (2) a − 1 es divisible por todos los factores primos de m; (3) si 4 divide a m, 4 divide a a − 1. Con m = 256 = 2⁸ las condiciones se reducen a **c impar** y **a ≡ 1 (mod 4)**. PRISMA-L elige a = 4·(K₅ mod 63 + 1) + 1 y c = 2·(K₆ mod 128) + 1, de modo que los 256 desplazamientos aparecen antes de repetirse; se excluye a = 1, que daría una progresión aritmética trivial."),
  H2("3.4 Entropía, PSNR y χ²"),
  ...bullets([
    "**Entropía de Shannon:** H = −Σ pᵢ·log₂ pᵢ, en bits por byte (máximo 8). Un texto en español ronda 4 bits/byte; un buen cifrado se acerca a 8. Para mensajes cortos, el máximo alcanzable es log₂(n), porque n bytes no pueden mostrar los 256 valores.",
    "**PSNR:** 10·log₁₀(255² / MSE) en dB, con MSE = (1/N)·Σ(xᵢ − yᵢ)². Por encima de ~40 dB la diferencia es imperceptible.",
    "**χ² de Pearson:** Σ (Oᵢ − Eᵢ)² / Eᵢ. En César compara las letras observadas con las esperadas en español; en estegoanálisis compara las frecuencias de los pares de valores (2k, 2k+1).",
  ]),
);

// ───────────────────────── 4. César ─────────────────────────
add(
  H1("4. Algoritmo 1: cifrado César (material base)"),
  H2("4.1 Qué es y para qué se usa en el sistema"),
  P("Es un cifrado clásico de sustitución monoalfabética: cada letra se desplaza k posiciones en un alfabeto de n letras, C = (P + k) mod n. Solo hay n claves posibles, por lo que **no protege información real**; en PRISMA-L tiene tres usos declarados:"),
  ...bullets([
    "**Laboratorio César** (web y Android): cifrar, descifrar y **romper** automáticamente con fuerza bruta y χ².",
    "**Ofuscación de caracteres** en la consola de administración: los nombres de usuario se muestran desplazados 13 posiciones sobre los 95 caracteres ASCII imprimibles. Es ofuscación, no cifrado: el botón \"Revelar\" la deshace.",
    "**Pieza interna de PRISMA-L**: el desplazamiento sᵢ que se suma a cada bloque es un César que cambia en cada bloque.",
  ]),
  H2("4.2 Paso a paso"),
  ...steps([
    "Elegir el alfabeto: A–Z (n = 26, como el material base) o A–Z con Ñ (n = 27).",
    "Quitar tildes y diéresis (á → a, ü → u) para que ninguna letra escape al cifrado. La Ñ se conserva solo si el alfabeto la incluye.",
    "Para cada carácter: si está en el alfabeto, obtener su posición i (A = 0, …, Z = n − 1).",
    "Calcular j = (i + k) mod n y escribir la letra en la posición j, conservando mayúscula o minúscula.",
    "Los caracteres fuera del alfabeto (espacios, dígitos, signos) se copian sin cambio.",
    "Para descifrar, usar −k: i = (j − k) mod n.",
  ]),
  H2("4.3 Ejemplo resuelto"),
  P("Mensaje **CRIPTOGRAFIA** con k = 3 y n = 26:"),
  ...tbl(["Letra", "C", "R", "I", "P", "T", "O", "G", "R", "A", "F", "I", "A"], [
    ["i", "2", "17", "8", "15", "19", "14", "6", "17", "0", "5", "8", "0"],
    ["(i+3) mod 26", "5", "20", "11", "18", "22", "17", "9", "20", "3", "8", "11", "3"],
    ["Cifrada", "F", "U", "L", "S", "W", "R", "J", "U", "D", "I", "L", "D"],
  ], null, "Cifrado César de CRIPTOGRAFIA con k = 3."),
  P("Resultado: **FULSWRJUDILD**. (El PDF del curso muestra «FULswrjudild»; la versión correcta, en mayúsculas, es la de la tabla, y la prueba `MaterialExample_CRIPTOGRAFIA` lo verifica.)"),
  H2("4.4 Seudocódigo"),
  ...code(`FUNCIÓN CesarCifrar(texto, k, conEñe):
    alfabeto ← SI conEñe ENTONCES "ABCDEFGHIJKLMNÑOPQRSTUVWXYZ" SI NO "ABCDEFGHIJKLMNOPQRSTUVWXYZ"
    n ← |alfabeto|
    texto ← QuitarTildes(texto, conEñe)
    salida ← ""
    PARA cada carácter x EN texto:
        i ← Posición(Mayúscula(x), alfabeto)
        SI i = −1 ENTONCES salida ← salida + x            # espacio, dígito, signo
        SI NO
            y ← alfabeto[(i + k) mod n]
            salida ← salida + (SI EsMinúscula(x) ENTONCES Minúscula(y) SI NO y)
    DEVOLVER salida

FUNCIÓN CesarDescifrar(texto, k, conEñe):  DEVOLVER CesarCifrar(texto, −k, conEñe)`),
  H2("4.5 Diagrama de flujo"),
  ...img(DIAG("cesar"), "Diagrama de flujo del cifrado César.", { maxH: 6.8 }),
  H2("4.6 Ataque: fuerza bruta + χ²"),
  P("Como solo hay n claves, se descifra con todas y se elige la que produce un texto cuyas frecuencias de letras se parecen más a las del español, medido con χ². La implementación (`CaesarCracker`) usa la tabla de frecuencias del español (E 13.68 %, A 12.53 %, O 8.68 %, S 7.98 %, …)."),
  ...steps([
    "Para cada k = 0, …, n − 1: Pₖ = Descifrar(C, k).",
    "Contar Oᵢ, las apariciones de cada letra i en Pₖ, y N, el total de letras.",
    "Calcular Eᵢ = N · fᵢ, donde fᵢ es la frecuencia de la letra i en español.",
    "χ²(k) = Σ (Oᵢ − Eᵢ)² / Eᵢ.",
    "La clave es la de menor χ². En el sistema, romper un texto de 200 caracteres tarda alrededor de 1 ms.",
  ]),
  ...code(`FUNCIÓN RomperCesar(C, conEñe):
    candidatos ← []
    PARA k ← 0 HASTA n − 1:
        P ← CesarDescifrar(C, k, conEñe)
        candidatos.agregar((k, ChiCuadrada(P), P))
    ORDENAR candidatos POR χ² ASCENDENTE
    DEVOLVER candidatos[0]`),
  ...img(DIAG("cesar_crack"), "Diagrama de flujo del ataque χ² a César.", { maxH: 6.5 }),
);

// ───────────────────────── 5. LSB ─────────────────────────
add(
  H1("5. Algoritmo 2: esteganografía LSB (material base)"),
  H2("5.1 Qué es y para qué se usa en el sistema"),
  P("La esteganografía oculta la **existencia** del mensaje. La técnica LSB (*least significant bit*) reemplaza el bit menos significativo de los bytes de una imagen por los bits del mensaje: el valor de cada canal cambia como mucho en 1/255, lo cual es invisible. El sistema la usa de dos formas:"),
  ...bullets([
    "**Modo clásico** (Ocultar/Revelar): LSB secuencial **sin clave**, exactamente como en el material base. Sirve para demostrar que cualquiera puede leer el mensaje (escenario E2).",
    "**Capa de transporte de PRISMA-L**: el cifrado de PRISMA-L se escribe en los LSB, pero en un orden secreto (sección 6.6).",
  ]),
  H2("5.2 Paso a paso (modo clásico)"),
  ...steps([
    "Normalizar la imagen a RGB de 8 bits: N = ancho × alto × 3 canales, en orden R, G, B por fila.",
    "Formar la trama: longitud del mensaje (4 bytes, big-endian) seguida del mensaje en UTF-8.",
    "Convertir la trama en bits, del más significativo al menos significativo de cada byte.",
    "Para j = 0, 1, …: canal[j] ← (canal[j] AND 11111110₂) OR bit[j].",
    "Guardar como PNG (formato sin pérdida).",
    "Para extraer: leer los LSB de los primeros 32 canales (longitud L) y después 8·L bits más.",
  ]),
  H2("5.3 Ejemplo resuelto"),
  ...tbl(["Paso", "Valor"], [
    ["Canal original", "104 = 01101000₂"],
    ["Bit secreto", "1"],
    ["Reemplazar el LSB", "0110100**1**₂"],
    ["Canal modificado", "105 (cambio de 1/255, invisible)"],
  ], [0.4, 0.6], "Inserción LSB de un bit (ejemplo del material base)."),
  H2("5.4 Seudocódigo"),
  ...code(`FUNCIÓN LsbOcultar(imagen, mensaje):
    canales ← NormalizarRGB(imagen)
    trama ← EnteroBigEndian(|mensaje|, 4 bytes) ‖ UTF8(mensaje)
    bits ← BitsMSBPrimero(trama)
    SI |bits| > |canales| ENTONCES ERROR "no cabe"
    PARA j ← 0 HASTA |bits| − 1:
        canales[j] ← (canales[j] AND 0xFE) OR bits[j]
    DEVOLVER CodificarPNG(canales)

FUNCIÓN LsbExtraer(imagen):
    canales ← NormalizarRGB(imagen)
    L ← Bytes(LSB de canales[0..31])
    SI L < 0 O L > capacidad ENTONCES ERROR "no hay mensaje legible"
    DEVOLVER Bytes(LSB de canales[32 .. 32 + 8L − 1])`),
  ...img(DIAG("lsb_clasico"), "Diagrama de flujo del LSB clásico (ocultar).", { maxH: 6.5 }),
  H2("5.5 Límites"),
  ...bullets([
    "Sin clave: cualquiera que sepa que se usó LSB secuencial puede leer el mensaje.",
    "Escribe al inicio de la imagen: el ataque χ² de Westfeld–Pfitzmann lo detecta en esa región en cuanto se llena (sección 9).",
    "Cualquier recompresión con pérdida (por ejemplo, JPEG) destruye el mensaje.",
  ]),
);

// ───────────────────────── 6. PRISMA-L ─────────────────────────
add(
  H1("6. Algoritmo 3 (inventado): PRISMA-L"),
  H2("6.1 Idea y motivación"),
  P("PRISMA-L generaliza César con álgebra lineal. César hace C = P + k sobre **un** símbolo; el cifrado afín, C = a·P + k; el cifrado de Hill, C = A·P sobre un vector. PRISMA-L hace"),
  new Paragraph({ alignment: AlignmentType.CENTER, spacing: { before: 80, after: 160 }, children: [new TextRun({ text: "Cᵢ = A · (Pᵢ ⊕ Cᵢ₋₁) + sᵢ   (mod 256),     sᵢ = (a · sᵢ₋₁ + c) mod 256", bold: true, size: 24, font: L.MONO })] }),
  P("sobre **pares de bytes**, donde A es una matriz secreta 2×2 y sᵢ es un desplazamiento César **distinto en cada bloque**. Después, en lugar de escribir el resultado en orden (como el LSB clásico), lo esconde en los LSB de la imagen siguiendo un **recorrido de píxeles barajado con la clave**, y lo autentica con un HMAC."),
  ...tbl(["Problema de los algoritmos base", "Qué aporta PRISMA-L"], [
    ["César conserva el histograma y se rompe con χ² en ~1 ms.", "La matriz y el encadenamiento aplanan las frecuencias: entropía de 4.10 a 7.93 bits/byte."],
    ["Hill puro cae con un ataque de texto claro conocido (A = C·P⁻¹).", "El César dinámico y el encadenamiento rompen la linealidad simple: el mismo ataque predice 0 de 1 000 bloques."],
    ["Dos bloques iguales dan el mismo cifrado (César, Hill).", "El encadenamiento con el bloque anterior y sᵢ variable hacen que \"AAAA…\" produzca bloques distintos."],
    ["El LSB clásico se lee sin clave.", "Sin la frase no se conoce el orden de los bits: la lectura secuencial da ruido."],
    ["Ni César ni LSB detectan alteraciones.", "HMAC-SHA256 (truncado a 8 B) rechaza una frase incorrecta o un solo bit alterado."],
  ], [0.48, 0.52], "Qué corrige PRISMA-L de los algoritmos base."),
  H2("6.2 Componentes"),
  ...tbl(["Componente", "Herramienta", "Área matemática"], [
    ["Derivación de claves", "PBKDF2-HMAC-SHA256 (100 000 it.) y SHA-256", "—"],
    ["Matriz de Hill 2×2 (det impar)", "Muestreo con rechazo; inversa por adjunta y Euclides extendido", "Álgebra lineal, discretas"],
    ["César dinámico", "LCG mod 256 con Hull–Dobell", "Teoría de números"],
    ["Encadenamiento", "XOR con el bloque cifrado anterior", "Suma en GF(2)"],
    ["Recorrido secreto", "Fisher–Yates con LCG de 64 bits y muestreo con rechazo", "Permutaciones (Sₙ)"],
    ["Transporte", "LSB por reemplazo", "—"],
    ["Integridad", "HMAC-SHA256 truncado a 8 bytes", "—"],
  ], [0.3, 0.45, 0.25], "Componentes de PRISMA-L."),
  H2("6.3 Derivación de claves"),
  ...steps([
    "Generar una sal aleatoria de 16 bytes (se guarda en la imagen).",
    "K = PBKDF2-HMAC-SHA256(frase, sal, 100 000 iteraciones, 32 bytes).",
    "Matriz: h = SHA-256(K ‖ \"hill\" ‖ ctr), A = [[h₀, h₁], [h₂, h₃]]. Si det(A) es par, ctr ← ctr + 1 y repetir (muestreo con rechazo).",
    "A⁻¹ = det(A)⁻¹ · adj(A) mod 256.",
    "César dinámico: s₀ = K[4], a = 4·(K[5] mod 63 + 1) + 1, c = 2·(K[6] mod 128) + 1.",
    "Bloque inicial: C₀ = (K[7], K[8]). Semilla del recorrido: K[16..23] (little-endian). Clave del HMAC: k_mac = SHA-256(K ‖ \"mac\").",
  ]),
  ...img(DIAG("prisma_derivacion"), "Derivación de claves de PRISMA-L.", { maxH: 6.2 }),
  H2("6.4 Capa de cifrado: un bloque paso a paso"),
  ...steps([
    "Si el mensaje tiene longitud impar, agregar un byte 0 (la longitud real viaja en la trama).",
    "prev ← C₀, s ← s₀.",
    "Para cada bloque Pᵢ = (p₁, p₂):",
    "César dinámico: s ← (a·s + c) mod 256.",
    "Encadenamiento: X = (p₁ ⊕ prev₁, p₂ ⊕ prev₂).",
    "Hill: Y = A·X mod 256.",
    "Desplazamiento: Cᵢ = ((Y₁ + s) mod 256, (Y₂ + s) mod 256); prev ← Cᵢ.",
    "Descifrado: s se genera igual; Y = (Cᵢ − s) mod 256; X = A⁻¹·Y; Pᵢ = X ⊕ prev; prev ← Cᵢ.",
  ]),
  ...img(DIAG("prisma_bloque"), "Cifrado por bloques de PRISMA-L.", { maxH: 6.6 }),
  ...img(DIAG("prisma_descifrar"), "Descifrado por bloques de PRISMA-L.", { maxH: 5.8 }),
  H2("6.5 Ejemplo resuelto a mano"),
  P("Datos: A = [[3, 2], [5, 7]], s₀ = 7, a = 5, c = 3, C₀ = (10, 20). Mensaje **\"Hi\"** = (72, 105). Este ejemplo está verificado por la prueba `HandWorkedExample_Hi`."),
  ...tbl(["Paso", "Cálculo", "Resultado"], [
    ["det(A)", "3·7 − 2·5 = 11 (impar ✔)", "11"],
    ["det⁻¹", "11·163 = 1793 = 7·256 + 1", "163"],
    ["A⁻¹", "163·[[7, −2], [−5, 3]] mod 256", "[[117, 186], [209, 233]]"],
    ["s₁", "(5·7 + 3) mod 256", "38"],
    ["X = P ⊕ C₀", "(72 ⊕ 10, 105 ⊕ 20)", "(66, 125)"],
    ["Y = A·X", "(3·66 + 2·125, 5·66 + 7·125) = (448, 1205) mod 256", "(192, 181)"],
    ["C₁ = Y + s₁", "(192 + 38, 181 + 38)", "**(230, 219)**"],
    ["Descifrar: Y", "(230 − 38, 219 − 38)", "(192, 181)"],
    ["X = A⁻¹·Y", "(117·192 + 186·181, 209·192 + 233·181) mod 256", "(66, 125)"],
    ["P = X ⊕ C₀", "(66 ⊕ 10, 125 ⊕ 20)", "(72, 105) = \"Hi\" ✔"],
  ], [0.18, 0.55, 0.27], "Ejemplo numérico de PRISMA-L."),
  H2("6.6 Capa esteganográfica: recorrido secreto"),
  ...steps([
    "Normalizar la portadora a RGB de 8 bits (las fotos de más de 1 280 px por lado se reducen).",
    "Escribir la **sal** en los LSB de los canales 0..127, en orden: el receptor la necesita antes de conocer la clave.",
    "Calcular el recorrido R = FisherYates([128, …, N − 1], semilla), con un LCG de 64 bits y muestreo con rechazo para no sesgar el módulo.",
    "Trama = longitud (4 B) ‖ C ‖ etiqueta, con etiqueta = HMAC-SHA256(k_mac, C)[0..7].",
    "El bit j de la trama va al LSB del canal R[j]. Se guarda como PNG.",
    "Capacidad: ⌊(N − 128)/8⌋ − 12 bytes (115 172 B en una imagen de 640×480).",
  ]),
  H3("Ejemplo de Fisher–Yates con el generador real"),
  P("Con semilla 12 345 y 8 posiciones [128, …, 135], el generador produce las elecciones de la tabla (el valor v se reduce módulo i + 1):"),
  ...tbl(["i", "v (32 bits altos del LCG)", "j = v mod (i+1)", "Lista después del intercambio"], [
    ["7", "470 636 529", "1", "128, 135, 130, 131, 132, 133, 134, 129"],
    ["6", "1 139 821 166", "1", "128, 134, 130, 131, 132, 133, 135, 129"],
    ["5", "3 803 726 085", "3", "128, 134, 130, 133, 132, 131, 135, 129"],
    ["4", "3 589 464 842", "2", "128, 134, 132, 133, 130, 131, 135, 129"],
    ["3", "1 398 574 760", "0", "133, 134, 132, 128, 130, 131, 135, 129"],
    ["2", "2 407 209 900", "0", "132, 134, 133, 128, 130, 131, 135, 129"],
    ["1", "3 409 638 966", "0", "134, 132, 133, 128, 130, 131, 135, 129"],
  ], [0.08, 0.3, 0.17, 0.45], "Fisher–Yates paso a paso (semilla 12 345)."),
  ...img(DIAG("fisher_yates"), "Fisher–Yates con muestreo con rechazo.", { maxH: 6.6 }),
  ...img(DIAG("prisma_ocultar"), "PRISMA-L: ocultar un mensaje.", { maxH: 7.2 }),
  ...img(DIAG("prisma_revelar"), "PRISMA-L: revelar un mensaje.", { maxH: 7.2 }),
  H2("6.7 Seudocódigo completo"),
  ...code(`FUNCIÓN DerivarClaves(frase, sal):
    K ← PBKDF2_HMAC_SHA256(UTF8(frase), sal, 100000, 32)
    ctr ← 0
    REPETIR
        h ← SHA256(K ‖ "hill" ‖ ctr);  A ← [[h0, h1], [h2, h3]];  ctr ← ctr + 1
    HASTA QUE Det(A) es impar
    s0 ← K[4];  a ← 4·(K[5] mod 63 + 1) + 1;  c ← 2·(K[6] mod 128) + 1
    C0 ← (K[7], K[8]);  semilla ← LittleEndian64(K[16..23]);  kmac ← SHA256(K ‖ "mac")
    DEVOLVER (A, InversaAdjunta(A), s0, a, c, C0, semilla, kmac)

FUNCIÓN CifrarPRISMA(P, claves):
    SI |P| impar ENTONCES P ← P ‖ 0
    prev ← C0;  s ← s0;  C ← []
    PARA i ← 0 HASTA |P| − 1 PASO 2:
        s ← (a·s + c) mod 256
        X ← (P[i] ⊕ prev1, P[i+1] ⊕ prev2)
        Y ← A·X mod 256
        prev ← ((Y1 + s) mod 256, (Y2 + s) mod 256);  C ← C ‖ prev
    DEVOLVER C

FUNCIÓN DescifrarPRISMA(C, claves):
    prev ← C0;  s ← s0;  P ← []
    PARA i ← 0 HASTA |C| − 1 PASO 2:
        s ← (a·s + c) mod 256
        X ← A⁻¹ · ((C[i] − s) mod 256, (C[i+1] − s) mod 256) mod 256
        P ← P ‖ (X1 ⊕ prev1, X2 ⊕ prev2);  prev ← (C[i], C[i+1])
    DEVOLVER P

FUNCIÓN Ocultar(imagen, mensaje, frase):
    canales ← NormalizarRGB(Reducir(imagen, 1280))
    sal ← Aleatorio(16);  claves ← DerivarClaves(frase, sal)
    C ← CifrarPRISMA(UTF8(mensaje), claves)
    trama ← BigEndian32(|mensaje|) ‖ C ‖ HMAC_SHA256(kmac, C)[0..7]
    SI 8·|trama| > |canales| − 128 ENTONCES ERROR "no cabe"
    PARA i ← 0 HASTA 127:  canales[i].lsb ← bit i de sal
    R ← FisherYates([128 .. |canales| − 1], semilla)
    PARA j ← 0 HASTA 8·|trama| − 1:  canales[R[j]].lsb ← bit j de trama
    DEVOLVER CodificarPNG(canales)

FUNCIÓN Revelar(imagen, frase):
    canales ← NormalizarRGB(imagen)
    sal ← LSB(canales[0..127]);  claves ← DerivarClaves(frase, sal)
    R ← FisherYates([128 .. |canales| − 1], semilla)
    L ← BigEndian32(LSB por R de los bits 0..31)
    SI L < 0 O L > capacidad ENTONCES ERROR genérico
    C, T ← leer los bits siguientes por R
    SI NO IgualTiempoConstante(T, HMAC_SHA256(kmac, C)[0..7]) ENTONCES ERROR genérico
    DEVOLVER DescifrarPRISMA(C, claves)[0..L − 1]

FUNCIÓN FisherYates(lista, semilla):
    x ← semilla
    PARA i ← |lista| − 1 HASTA 1 PASO −1:
        REPETIR
            x ← (x·6364136223846793005 + 1442695040888963407) mod 2⁶⁴
            v ← x div 2³²
        HASTA QUE v < ⌊2³² / (i+1)⌋·(i+1)              # muestreo con rechazo
        j ← v mod (i+1);  Intercambiar(lista[i], lista[j])
    DEVOLVER lista`),
  H2("6.8 Traza real del sistema"),
  P("Con el modo demo activo, cada operación PRISMA-L deja una traza que se ve en la consola de administración y se imprime en la consola del servidor. Esta es la traza real de ocultar \"Hola Bob, nos vemos a las 18:00\" en `oceano.png` con la frase `girasol-azul-42` (el bloque 0 se puede verificar a mano: 72 ⊕ 239 = 167, 65·167 + 97·24 = 13 183 ≡ 127, (45·80 + 231) mod 256 = 247 y 127 + 247 ≡ 118):"),
  ...code(traza),
  H2("6.9 Implementación y validación"),
  ...bullets([
    "El algoritmo se diseñó primero como **prototipo en Python** (`tools/prototipo/prisma_lite_proto.py`). Ese prototipo genera vectores de prueba deterministas (`vectors.json`) y la implementación en C# debe reproducir **bit a bit** la matriz, su inversa, los parámetros del LCG, la semilla, el HMAC, el cifrado y el recorrido.",
    "Clases: `PrismaKeys` (derivación), `PrismaCipher` (bloques), `Lcg64` y `FisherYates` (recorrido), `PrismaStego` (trama, sal, HMAC, LSB), `PrismaTrace` (traza para la consola admin).",
    "Comparaciones de etiquetas en tiempo constante (`CryptographicOperations.FixedTimeEquals`) y error genérico: el sistema no revela si falló la frase, la imagen o la integridad.",
  ]),
  H2("6.10 Lo que PRISMA-L no es"),
  ...bullets([
    "No es comparable con AES: no tiene análisis diferencial ni lineal publicado. Es un diseño **didáctico**.",
    "Su difusión es local y **hacia adelante**: un bit cambiado afecta a su bloque y a los siguientes (≈ 26 % de los bits desde ese bloque; ≈ 13 % sobre el mensaje completo), no al mensaje entero.",
    "Con muchos pares de texto claro y cifrado y suficiente esfuerzo, la estructura lineal en ℤ₂₅₆ admite ataques algebraicos más elaborados que el de Hill puro.",
    "Su seguridad práctica depende de la frase y de PBKDF2, no del tamaño del espacio de matrices.",
  ]),
);

// ───────────────────────── 7. AES-GCM ─────────────────────────
add(
  H1("7. Algoritmo de referencia: AES-256-GCM"),
  P("AES (Advanced Encryption Standard) es el cifrador simétrico por bloques estándar (bloques de 128 bits, claves de 128, 192 o 256 bits). Cada ronda aplica SubBytes, ShiftRows, MixColumns y AddRoundKey sobre un estado de 4×4 bytes. En modo **GCM** se usa como cifrador de flujo (contador) y además calcula una etiqueta de autenticación con GHASH sobre GF(2¹²⁸)."),
  P("En PRISMA-L se usa como **referencia comercial** en el comparador: la misma frase pasa por el mismo PBKDF2, de modo que la comparación con PRISMA-L es justa. Se implementa con `System.Security.Cryptography.AesGcm` de .NET."),
  ...steps([
    "Generar sal (16 B) y nonce (12 B) aleatorios.",
    "clave = PBKDF2-HMAC-SHA256(frase, sal, 100 000 iteraciones, 32 B).",
    "C, etiqueta = AES-256-GCM(clave, nonce, mensaje).",
    "Salida: sal ‖ nonce ‖ C ‖ etiqueta (16 B). Al descifrar, una etiqueta que no coincide lanza una excepción: detecta cualquier alteración.",
  ]),
  ...img(DIAG("aes_gcm"), "Flujo de AES-256-GCM en el comparador.", { maxH: 4.6 }),
);

// ───────────────────────── 8. Autenticación ─────────────────────────
add(
  H1("8. Autenticación con segundo factor (imagen-llave)"),
  P("El inicio de sesión usa usuario y contraseña. Cada usuario puede activar, de forma **opcional**, un segundo factor: una imagen-llave. Quien lo tenga activado debe presentar **usuario, contraseña e imagen**; ninguna de las tres basta por sí sola."),
  H2("8.1 Activación"),
  ...steps([
    "El usuario, con sesión iniciada, confirma su contraseña y elige una imagen (del catálogo o propia).",
    "El servidor genera un token aleatorio T de 32 bytes con un generador criptográfico.",
    "Oculta T en la imagen con PRISMA-L usando la **contraseña** como frase, y devuelve `llave_<usuario>.png`.",
    "Guarda SHA-256(T) y marca el 2FA como activo. El token **nunca** se almacena.",
  ]),
  ...img(DIAG("activar_2fa"), "Activación del 2FA con imagen-llave.", { maxH: 5.2 }),
  H2("8.2 Inicio de sesión"),
  ...steps([
    "El cliente envía usuario y contraseña a `POST /api/auth/login`. El servidor verifica PBKDF2.",
    "Si el usuario **no** tiene 2FA, responde con el token JWT.",
    "Si lo tiene, responde `requires2fa: true` (sin token). El cliente pide la imagen-llave.",
    "El cliente envía usuario, contraseña e imagen a `POST /api/auth/login/2fa`.",
    "El servidor verifica de nuevo la contraseña, revela T' con PRISMA-L usando la contraseña, y compara SHA-256(T') con el hash guardado en tiempo constante.",
    "Si coincide, entrega el JWT; si no, responde 401 con un mensaje genérico.",
  ]),
  ...img(DIAG("login_2fa"), "Inicio de sesión con segundo factor.", { maxH: 7 }),
  H2("8.3 Propiedades de seguridad"),
  ...tbl(["Situación del atacante", "Resultado"], [
    ["Conoce la contraseña, no tiene la imagen", "No entra: falta el token."],
    ["Tiene la imagen, no conoce la contraseña", "No entra: sin la contraseña no puede revelar el token (y el primer paso ya falla)."],
    ["Roba la base de datos", "Obtiene hashes PBKDF2 y SHA-256(T): no puede reconstruir T ni la imagen."],
    ["Altera o recomprime la imagen", "El HMAC falla: la llave deja de servir (hay que regenerarla)."],
    ["Fuerza bruta en línea", "Limitada a 10 intentos por minuto por IP en las rutas de autenticación (HTTP 429)."],
  ], [0.45, 0.55], "Qué protege la imagen-llave."),
  P("Consecuencias operativas: si el usuario cambia su contraseña o pierde la imagen, debe generar una llave nueva; un administrador puede restablecer el 2FA desde la consola."),
);

// ───────────────────────── 9. Métricas ─────────────────────────
add(
  H1("9. Métricas medidas"),
  P("Todas las cifras de esta sección las produce la herramienta `tools/Prisma.Metrics`, que ejecuta la implementación real de `Prisma.Core`. Ninguna es una estimación teórica. Se regeneran con `dotnet run --project tools/Prisma.Metrics -c Release`. Los tiempos dependen de la carga del equipo y sirven para comparar órdenes de magnitud, no como referencia absoluta."),
  ...markdown(metricas),
  H2("9.1 Interpretación"),
  ...bullets([
    "**Entropía:** César deja la entropía exactamente igual que el texto claro (solo renombra símbolos); PRISMA-L la lleva casi al máximo, al nivel de AES-GCM.",
    "**Ataque de texto claro conocido:** con 2 bloques conocidos se recupera la matriz de un Hill puro y se predicen todos los bloques nuevos; contra PRISMA-L el mismo ataque no acierta ninguno.",
    "**Sensibilidad a la clave:** cambiar una sola letra de la frase cambia ≈ 50 % de los bits, gracias a PBKDF2.",
    "**Difusión:** es el punto débil declarado: el cambio se propaga hacia adelante, no a todo el mensaje.",
    "**Esteganografía:** con mensajes de hasta decenas de KB, el PSNR supera 54 dB (imperceptible) y el χ² no detecta PRISMA-L; con la imagen casi llena, cualquier LSB por reemplazo se vuelve detectable.",
  ]),
);

// ───────────────────────── 10. Escenarios ─────────────────────────
const scenario = (id, title, actors, pre, stepsList, expected, evidence) => [
  H2(`${id}. ${title}`),
  ...tbl(["Elemento", "Descripción"], [
    ["Actores", actors], ["Precondiciones", pre], ["Resultado esperado", expected], ["Evidencia", evidence],
  ], [0.22, 0.78]),
  P("**Pasos:**", { align: AlignmentType.LEFT }),
  ...steps(stepsList),
];
add(
  H1("10. Escenarios"),
  P("Los actores siguen la convención del material base: **Alice** y **Bob** son los participantes legítimos, **Eve** es el atacante pasivo que escucha y **Mallory** el atacante activo que modifica mensajes."),
  ...scenario("E1", "Mensaje oculto entre dos personas", "Alice (emisora), Bob (receptor), Eve (intercepta la imagen).",
    "Alice y Bob acordaron una frase por otro medio. Ambos tienen cuenta en PRISMA-L.",
    ["Alice abre **Ocultar**, elige la imagen \"Atardecer\", escribe el mensaje y la frase, y pulsa **Ocultar en la imagen**.",
      "El sistema muestra la imagen resultante con PSNR ≈ 82 dB y la descarga como PNG.",
      "Alice envía el PNG a Bob **como archivo**.",
      "Bob abre **Revelar**, carga el PNG, escribe la frase y obtiene el mensaje.",
      "Eve intercepta el PNG: a simple vista es una foto normal y, sin la frase, no puede extraer nada."],
    "Bob lee el mensaje; Eve solo ve una imagen.", "Figuras de Ocultar y Revelar (sección 12) y tabla de integridad (sección 9)."),
  ...img(SHOT("web_02_ocultar.jpg"), "E1: Alice oculta el mensaje (PRISMA-L, PSNR 81.57 dB)."),
  ...img(SHOT("web_03_revelar.jpg"), "E1: Bob revela el mensaje con la frase correcta."),
  ...scenario("E2", "LSB clásico frente a PRISMA-L", "Eve, con un extractor de LSB secuencial.",
    "Eve tiene una imagen con mensaje en modo clásico y otra en modo PRISMA-L.",
    ["Eve extrae la imagen clásica con **Revelar → LSB clásico**: obtiene el mensaje completo sin ninguna clave.",
      "Eve intenta lo mismo con la imagen PRISMA-L: el sistema responde que la lectura secuencial no produce un mensaje legible y muestra los bytes que leyó, que parecen ruido aleatorio.",
      "Eve intenta revelar la imagen PRISMA-L con una frase incorrecta: recibe el error genérico."],
    "El modo clásico no protege nada; PRISMA-L sí.", "Figura del error genérico y prueba `Classic_IsReadableByAnyone_PrismaIsNot`."),
  ...img(SHOT("web_04_revelar_error.jpg"), "E2: frase incorrecta → error genérico y lo que vería un atacante."),
  ...scenario("E3", "Alteración de la imagen", "Mallory (modifica la imagen en tránsito).",
    "Una imagen con mensaje PRISMA-L.",
    ["Mallory cambia un solo bit de un canal que forma parte de la trama.",
      "Bob intenta revelar con la frase correcta.",
      "El HMAC no coincide y el sistema rechaza la imagen con el error genérico.",
      "Si Mallory toca un canal que la trama no usa, el mensaje sigue intacto (la marca no es frágil fuera de la trama)."],
    "Cualquier alteración de la trama o de la sal se detecta.", "Tabla \"Integridad y robustez\" de la sección 9 y prueba `Prisma_WrongPassphraseOrTamperingGivesGenericError`."),
  ...scenario("E4", "Inicio de sesión con imagen-llave", "Alicia (usuaria) y un atacante que robó su contraseña.",
    "Alicia activó el 2FA y guardó `llave_alicia.png`.",
    ["Alicia escribe usuario y contraseña: el sistema responde \"Contraseña correcta. Ahora carga tu imagen-llave\".",
      "Alicia selecciona `llave_alicia.png` y pulsa **Verificar imagen-llave**: entra al sistema.",
      "El atacante, con la contraseña pero con otra imagen, recibe 401 \"La imagen-llave no es válida para este usuario\".",
      "Con la imagen correcta y una contraseña equivocada, recibe 401 \"Usuario o contraseña incorrectos\"."],
    "Solo entra quien tiene **ambos** factores.", "Figuras del login en dos pasos y de la activación; registros de la auditoría."),
  ...img(SHOT("web_08_login_2fa.jpg"), "E4: segundo paso del login (imagen-llave)."),
  ...scenario("E5", "Ruptura de César", "Eve, con el laboratorio César.",
    "Un texto en español cifrado con César (k = 11).",
    ["Eve pega el texto cifrado en el laboratorio y pulsa **Romper (χ²)**.",
      "El sistema descifra con las 26 claves, calcula χ² contra las frecuencias del español y las ordena.",
      "La clave con menor χ² (k = 11, χ² = 12.98) recupera el texto en menos de un milisegundo."],
    "César se rompe de inmediato; por eso el sistema nunca lo usa para proteger contraseñas.", "Figura del laboratorio César."),
  ...img(SHOT("web_05_cesar.jpg"), "E5: César roto con χ²; la clave correcta destaca sobre las demás."),
  ...scenario("E6", "Recompresión por una app de mensajería", "Alice (envía la imagen \"como foto\").",
    "Una imagen con mensaje PRISMA-L.",
    ["Alice comparte el PNG por una app que lo convierte a JPEG.",
      "Bob intenta revelar el mensaje.",
      "La compresión con pérdida cambió ≈ 50 % de los LSB; el HMAC falla y el sistema muestra el error genérico."],
    "Falla esperada y documentada. Mitigación: enviar siempre **como archivo** (la web y la app lo advierten).", "Tabla \"Integridad y robustez\" de la sección 9 (JPEG calidad 90 y 100)."),
);

// ───────────────────────── 11. API ─────────────────────────
add(
  H1("11. API REST"),
  P("Todas las rutas responden JSON (camelCase) y los errores tienen la forma `{\"error\": \"mensaje\"}`. Las rutas protegidas requieren `Authorization: Bearer <token>`. El contrato completo está en `docs/API.md`."),
  ...tbl(["Método y ruta", "Uso"], [
    ["POST /api/auth/register", "Registro (usuario 3–32 caracteres, contraseña ≥ 8)."],
    ["POST /api/auth/login", "Paso 1: devuelve el token o `requires2fa: true`."],
    ["POST /api/auth/login/2fa", "Paso 2 (multipart): usuario, contraseña e imagen-llave."],
    ["GET /api/account/me", "Perfil y estado del 2FA."],
    ["POST /api/account/2fa/enable · /disable", "Activar (devuelve la llave PNG) o desactivar el 2FA."],
    ["GET /api/catalog · /api/catalog/{id}", "Catálogo de imágenes (generadas por código)."],
    ["POST /api/stego/capacity · /embed · /extract · /analyze", "Capacidad, ocultar, revelar y estegoanálisis χ²."],
    ["POST /api/caesar/encrypt · /decrypt · /crack", "Laboratorio César."],
    ["POST /api/compare", "Los cuatro algoritmos sobre el mismo mensaje."],
    ["GET /api/admin/audit · /users · /trace/last · /diffmap/last", "Consola de administración (rol admin)."],
    ["POST /api/admin/deobfuscate · /users/{id}/reset-2fa", "Revelar la ofuscación y restablecer el 2FA."],
    ["GET /api/health", "Estado del servicio."],
  ], [0.45, 0.55], "Rutas de la API."),
);

// ───────────────────────── 12. Manual ─────────────────────────
add(
  H1("12. Manual de usuario"),
  H2("12.1 Cliente web"),
  P("La web se sirve desde el mismo servidor (por ejemplo, `http://localhost:5080` en local). Es responsiva: en el teléfono muestra una columna y un menú desplegable; desde 768 px, formulario y resultado lado a lado."),
  ...steps([
    "**Crear cuenta** e **iniciar sesión**.",
    "**Ocultar:** elegir una imagen del catálogo o propia, escribir el mensaje, elegir PRISMA-L (con frase) o LSB clásico, y descargar el PNG.",
    "**Revelar:** cargar el PNG, elegir el modo y escribir la frase.",
    "**César:** cifrar, descifrar o romper un texto.",
    "**Comparador:** ver salida, tamaño, tiempo, entropía e integridad de cada algoritmo.",
    "**Cuenta:** activar o desactivar el 2FA y descargar la imagen-llave.",
    "**Admin** (solo administradores): traza de PRISMA-L, mapa de diferencias, usuarios, estegoanálisis y auditoría.",
  ]),
  ...img(SHOT("web_01_login.jpg"), "Web: inicio de sesión."),
  ...img(SHOT("web_06_comparador.jpg"), "Web: comparador de algoritmos."),
  ...img(SHOT("web_07_cuenta_2fa.jpg"), "Web: 2FA activado y la imagen-llave generada."),
  ...img(SHOT("web_09_admin.jpg"), "Web: consola de administración (traza y mapa de diferencias)."),
  ...img(SHOT("web_10_auditoria.jpg"), "Web: auditoría con usuarios ofuscados con César."),
  H2("12.2 App Android"),
  ...steps([
    "Instalar `Prisma-L.apk`: en el teléfono, permitir la instalación desde el origen (navegador o administrador de archivos) y abrir el APK.",
    "En la pantalla de inicio de sesión, tocar el ícono de ajustes y escribir la URL del servidor (por ejemplo `https://tu-dominio`); pulsar **Probar conexión**.",
    "Iniciar sesión. Si la cuenta tiene 2FA, la app pide elegir la imagen-llave (se puede tomar de Descargas).",
    "Usar las pestañas **Ocultar, Revelar, César, Comparador y Cuenta**. Para guardar un PNG, la app abre el selector de documentos; para enviarlo, usa **Compartir como archivo**.",
  ]),
  ...tbl(["Pantalla", "Qué hace"], [
    ["Inicio de sesión", "Usuario y contraseña; ícono de ajustes para cambiar y probar la URL del servidor. Si la cuenta tiene 2FA, aparece el paso 2 para elegir la imagen-llave con el selector de documentos."],
    ["Registro", "Crea la cuenta con las mismas validaciones que la web."],
    ["Ocultar", "Portadora del catálogo (rejilla de miniaturas) o de la galería; mensaje; modo PRISMA-L o LSB clásico; capacidad; resultado con PSNR y botones Guardar y Compartir como archivo."],
    ["Revelar", "Elegir el PNG, modo y frase; muestra el mensaje o el error genérico junto con los bytes que vería un atacante."],
    ["César", "Clave con control deslizante, interruptor de Ñ, cifrar, descifrar y romper con χ² (las 5 mejores claves)."],
    ["Comparador", "Tarjetas por algoritmo con bytes, tiempo, entropía e integridad."],
    ["Cuenta", "Estado del 2FA; activar (elige imagen y confirma contraseña, guarda `llave_<usuario>.png`) o desactivar; cerrar sesión."],
  ], [0.2, 0.8], "Pantallas de la app Android."),
  P("En pantallas anchas (≥ 600 dp) la app usa un riel de navegación lateral y muestra formulario y resultado lado a lado; en teléfono, barra inferior y una columna con desplazamiento."),
  ...(androidShots.length
    ? androidShots.flatMap((f) => img(SHOT(f), androidCaptions[f] || "App Android.", { maxW: 3.2, maxH: 5.6 }))
    : []),
);

// ───────────────────────── 13. Despliegue ─────────────────────────
add(
  H1("13. Instalación y despliegue"),
  H2("13.1 Servidor en local"),
  ...code(`cd Examen_Prisma
dotnet test                                              # 30 pruebas
dotnet run --project src/Prisma.Api --launch-profile http # http://localhost:5080`),
  P("En desarrollo se crea el administrador `admin` con la contraseña definida en `appsettings.Development.json`; en producción se define con la variable `Admin__Password`."),
  H2("13.2 Servidor en la nube (Docker)"),
  ...steps([
    "Construir la imagen: `docker build -f deploy/Dockerfile -t prisma-l .`",
    "Definir las variables `Jwt__Key` (≥ 32 caracteres aleatorios) y `Admin__Password`.",
    "Montar un volumen persistente en `/app/data` (base de datos y catálogo).",
    "Publicar el puerto 8080 detrás del HTTPS del proveedor (Render, Railway, Azure App Service, Cloud Run…).",
    "Comprobar `GET https://<dominio>/api/health`.",
  ]),
  P("La guía completa está en `deploy/DEPLOY.md`."),
  H2("13.3 App Android"),
  ...code(`cd android
./gradlew :app:assembleRelease -PserverUrl=https://<dominio>   # URL por defecto del APK
# APK firmado: app/build/outputs/apk/release/ (copiado a android/dist/Prisma-L.apk)`),
  P("Sin recompilar, la URL también se cambia en la app. La app acepta servidores `http://` (por ejemplo, un equipo de la red local) y `https://`; con `http://` muestra un aviso de conexión sin cifrar. En la nube debe usarse HTTPS, porque con HTTP la contraseña y la imagen-llave viajarían sin cifrar."),
);

// ───────────────────────── 14. Pruebas ─────────────────────────
add(
  H1("14. Pruebas"),
  P("El proyecto `tests/Prisma.Core.Tests` contiene **30 pruebas xUnit**, todas en verde. Se agrupan así:"),
  ...tbl(["Grupo", "Qué verifica"], [
    ["Vectores del prototipo (3 casos)", "C# = Python: matriz, inversa, s₀, a, c, C₀, semilla, k_mac, cifrado, primeros valores del LCG y del recorrido."],
    ["Ejemplo \"Hi\"", "El ejemplo resuelto a mano de la sección 6.5."],
    ["Matemáticas", "A·A⁻¹ = I en 2 000 matrices; |GL₂(ℤ₁₆)| = 24 576; periodo 256 del LCG; el recorrido es una permutación; gamma incompleta y ln Γ."],
    ["Cifrado", "Ida y vuelta de 500 mensajes; bloques iguales → cifrados distintos; entropía de César vs. PRISMA-L; AES-GCM e integridad."],
    ["César", "CRIPTOGRAFIA → FULSWRJUDILD; HOLA → KROD; ida y vuelta con y sin Ñ; χ² encuentra la clave; ofuscación; las tildes no se filtran."],
    ["Esteganografía", "Ida y vuelta por PNG real; frase incorrecta y bit alterado → error genérico; capacidad; LSB clásico legible y PRISMA-L no; χ² detecta un LSB saturado; reducción de portadoras grandes."],
  ], [0.3, 0.7], "Pruebas automatizadas."),
  P("La app Android tiene una prueba de integración (`ApiClientIntegrationTest`, JVM) que usa el mismo cliente HTTP de la app contra el servidor en ejecución: registro, login, sesión expirada, catálogo, capacidad, ocultar y revelar en ambos modos, frase incorrecta (422 con los bytes del atacante), César, comparador y el ciclo completo del 2FA. Pasó sin fallos. Además, la app se probó en un **teléfono real con Android 11** (Samsung SM-A305G) contra el servidor en la red local (`http://dev-services.lan:5080`): conexión, registro, inicio de sesión, ocultar y guardar el PNG en Descargas, revelarlo en el teléfono y en el servidor, romper César, comparador, activar el 2FA y volver a entrar con usuario, contraseña e imagen-llave. Esa prueba detectó que la política de red solo permitía HTTP hacia el emulador; se corrigió para aceptar servidores `http://` de la red local, con un aviso de conexión sin cifrar."),
  P("Además se probaron de punta a punta, contra el servidor en ejecución: registro, login sin y con 2FA (llave correcta, imagen equivocada, contraseña equivocada), ocultar y revelar en ambos modos, comparador, laboratorio César, rutas de administración (un usuario normal recibe 403) y la versión Release en modo producción con variables de entorno, igual que en el contenedor."),
);

// ───────────────────────── 15. Límites ─────────────────────────
add(
  H1("15. Límites y modelo de amenaza"),
  ...bullets([
    "**PRISMA-L es didáctico.** Para proteger datos reales, el sistema ofrece AES-256-GCM como referencia. Las métricas de la sección 9 muestran tanto sus fortalezas como su difusión limitada.",
    "**Servidor de confianza.** El mensaje y la frase viajan por HTTPS y el servidor los procesa en memoria sin guardarlos; quien controle el servidor podría verlos. Es el modelo habitual de los servicios web de cifrado de archivos.",
    "**Detectabilidad.** Todo LSB por reemplazo es detectable estadísticamente con ocupación alta. El recorrido secreto impide **extraer** el mensaje, no ocultar que la imagen fue modificada.",
    "**Fragilidad.** Recompresión, redimensionado o capturas de pantalla destruyen el mensaje y la imagen-llave.",
    "**\"Solo nuestra app puede leerlo\"** no es una propiedad criptográfica (principio de Kerckhoffs): el algoritmo es público. La propiedad real es que, sin la frase, no se puede localizar, leer ni alterar el mensaje sin que se note.",
    "**César** nunca protege contraseñas: solo ofusca nombres en la consola y sirve como laboratorio.",
  ]),
);

// ───────────────────────── 16. Conclusiones ─────────────────────────
add(
  H1("16. Conclusiones"),
  P("PRISMA-L muestra cómo dos técnicas clásicas y frágiles, el cifrado César y la esteganografía LSB, pueden combinarse con herramientas de álgebra lineal (matriz de Hill invertible en ℤ₂₅₆), matemáticas discretas (Euclides extendido, Hull–Dobell, permutaciones de Fisher–Yates) y métodos numéricos (entropía, χ², PSNR, Monte Carlo, gamma incompleta) para obtener un algoritmo con propiedades **medibles**: aplana las frecuencias como AES, resiste el ataque que rompe a Hill, oculta el orden de los bits y detecta alteraciones."),
  P("También deja claro, con números, dónde no llega: su difusión es local y su seguridad depende de la frase. Integrado en un sistema real (backend .NET, web responsiva y app Android) y aplicado a un segundo factor de autenticación, el algoritmo cumple una función concreta y verificable dentro del sistema."),
);

// ───────────────────────── 17. Referencias ─────────────────────────
add(
  H1("17. Referencias"),
  ...[
    "Barker, E. (2020). *Recommendation for key management: Part 1 – General* (NIST SP 800-57 Part 1 Rev. 5). National Institute of Standards and Technology. https://doi.org/10.6028/NIST.SP.800-57pt1r5",
    "Bernstein, D. J. (2008). ChaCha, a variant of Salsa20. *Workshop Record of SASC 2008*, 3–5.",
    "Dworkin, M. (2007). *Recommendation for block cipher modes of operation: Galois/Counter Mode (GCM) and GMAC* (NIST SP 800-38D). National Institute of Standards and Technology.",
    "Hill, L. S. (1929). Cryptography in an algebraic alphabet. *The American Mathematical Monthly, 36*(6), 306–312.",
    "Hull, T. E., & Dobell, A. R. (1962). Random number generators. *SIAM Review, 4*(3), 230–254.",
    "Knuth, D. E. (1997). *The art of computer programming, Vol. 2: Seminumerical algorithms* (3rd ed.). Addison-Wesley.",
    "Kaufman, C., Perlman, R., & Speciner, M. (2022). *Network security: Private communication in a public world* (3rd ed.). Addison-Wesley Professional.",
    "Moriarty, K., Kaliski, B., & Rusch, A. (2017). *PKCS #5: Password-based cryptography specification version 2.1* (RFC 8018). IETF.",
    "Paar, C., & Pelzl, J. (2010). *Understanding cryptography: A textbook for students and practitioners*. Springer.",
    "Press, W. H., Teukolsky, S. A., Vetterling, W. T., & Flannery, B. P. (2007). *Numerical recipes: The art of scientific computing* (3rd ed.). Cambridge University Press.",
    "Stallings, W. (2020). *Cryptography and network security: Principles and practice* (8th ed.). Pearson.",
    "Westfeld, A., & Pfitzmann, A. (2000). Attacks on steganographic systems. En *Information Hiding* (LNCS 1768, pp. 61–76). Springer.",
  ].map((r) => new Paragraph({ children: runs(r), spacing: { after: 120 }, indent: { left: 720, hanging: 720 } })),
);

// ───────────────────────── Documento ─────────────────────────
const doc = new Document({
  creator: "Equipo PRISMA-L",
  title: "Documentación del sistema PRISMA-L",
  description: "Virología y Criptografía · UPIICSA–IPN",
  features: { updateFields: true },
  styles: {
    default: { document: { run: { font: L.FONT, size: 21 } } },
    paragraphStyles: [
      { id: "Heading1", name: "Heading 1", basedOn: "Normal", next: "Normal", quickFormat: true,
        run: { size: 34, bold: true, color: L.ACCENT, font: L.FONT }, paragraph: { spacing: { before: 120, after: 240 }, outlineLevel: 0 } },
      { id: "Heading2", name: "Heading 2", basedOn: "Normal", next: "Normal", quickFormat: true,
        run: { size: 27, bold: true, color: "2B2370", font: L.FONT }, paragraph: { spacing: { before: 280, after: 120 }, outlineLevel: 1, keepNext: true } },
      { id: "Heading3", name: "Heading 3", basedOn: "Normal", next: "Normal", quickFormat: true,
        run: { size: 23, bold: true, color: "3B3B55", font: L.FONT }, paragraph: { spacing: { before: 200, after: 100 }, outlineLevel: 2, keepNext: true } },
    ],
  },
  numbering: { config: L.numberingConfigs },
  sections: [{
    properties: { page: { size: { width: 12240, height: 15840 }, margin: { top: 1440, bottom: 1440, left: 1440, right: 1440 } }, titlePage: true },
    headers: {
      default: new Header({ children: [new Paragraph({ alignment: AlignmentType.RIGHT, border: { bottom: { style: BorderStyle.SINGLE, size: 4, color: "DDDBEE", space: 4 } },
        children: [new TextRun({ text: "PRISMA-L · Virología y Criptografía · UPIICSA–IPN", size: 16, color: "777777" })] })] }),
      first: new Header({ children: [new Paragraph({ children: [] })] }),
    },
    footers: {
      default: new Footer({ children: [new Paragraph({ alignment: AlignmentType.CENTER,
        children: [new TextRun({ text: "Página ", size: 16, color: "777777" }), new TextRun({ children: [PageNumber.CURRENT], size: 16, color: "777777" }),
          new TextRun({ text: " de ", size: 16, color: "777777" }), new TextRun({ children: [PageNumber.TOTAL_PAGES], size: 16, color: "777777" })] })] }),
      first: new Footer({ children: [new Paragraph({ children: [] })] }),
    },
    children: body,
  }],
});

Packer.toBuffer(doc).then((buf) => { fs.writeFileSync(out, buf); console.log("→", out, `(${(buf.length / 1024).toFixed(0)} KB)`); });
