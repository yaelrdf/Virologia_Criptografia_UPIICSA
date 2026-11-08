/**
 * Genera la documentación técnica del sistema ABCO en Word (.docx) con la librería docx.
 *
 * El contenido (portada, índice y capítulos, numerados automáticamente por lib.js) se escribe aquí con
 * los constructores de lib.js; los diagramas, las capturas y la traza de ejemplo se leen de la carpeta docs. Los nombres "Prisma" de rutas y proyectos son los nombres internos de ABCO.
 *
 * Uso (desde Examen_Prisma):  NODE_PATH=<carpeta con node_modules/docx> node docs/word/generar_documento.js <salida.docx>
 */
const fs = require("fs");
const path = require("path");
const L = require("./lib");
const { D, P, H1, H1keep, H2, H3, bullets, steps, code, tbl, img, markdown, pageBreak, runs } = L;
const { Document, Packer, Paragraph, TextRun, AlignmentType, TableOfContents, Header, Footer, PageNumber, HeadingLevel, BorderStyle } = D;

/** Raíz del proyecto (Examen_Prisma). */
const ROOT = path.resolve(__dirname, "..", "..");
/** Rutas de un diagrama exportado a PNG (por nombre, sin extensión) y de una captura de pantalla. */
const DIAG = (n) => path.join(ROOT, "docs", "diagramas", "png", n + ".png");
const SHOT = (n) => path.join(ROOT, "docs", "capturas", n);
/** Archivo de salida: primer argumento o, por defecto, Documentacion_Sistema_ABCO.docx en la raíz. */
const out = process.argv[2] || path.join(ROOT, "Documentacion_Sistema_ABCO.docx");
/** Traza de ejemplo de una operación ABCO (impresa por el servidor en modo demo). */
const traza = fs.readFileSync(path.join(ROOT, "docs", "traza_ejemplo.txt"), "utf8");

/** Elementos del cuerpo del documento, en orden. `add` acepta elementos sueltos o arreglos y los aplana. */
const body = [];
const add = (...xs) => xs.flat().forEach((x) => body.push(x));

// ───────────────────────── Portada ─────────────────────────
/**
 * Renglón centrado de la portada.
 * @param {string} t Texto.
 * @param {number} size Tamaño en medios puntos.
 * @param {{after?: number, bold?: boolean, color?: string}} [opts] Espacio posterior (DXA), negrita y color.
 */
const center = (t, size, opts = {}) => new Paragraph({ alignment: AlignmentType.CENTER, spacing: { after: opts.after ?? 120 }, children: [new TextRun({ text: t, size, bold: opts.bold, color: opts.color })] });
const field = (label, value) => new Paragraph({ alignment: AlignmentType.CENTER, spacing: { after: 80 },
  children: [new TextRun({ text: `${label}: `, bold: true, size: 22 }), new TextRun({ text: value, size: 22 })] });
add(
  center("INSTITUTO POLITÉCNICO NACIONAL", 30, { bold: true, after: 60 }),
  center("Unidad Profesional Interdisciplinaria de Ingeniería y Ciencias Sociales y Administrativas (UPIICSA)", 20, { after: 900 }),
  new Paragraph({ alignment: AlignmentType.CENTER, spacing: { after: 200 },
    children: [new D.ImageRun({ type: "png", data: fs.readFileSync(path.join(__dirname, "logo.png")), transformation: { width: 150, height: 150 } })] }),
  center("ABCO", 64, { bold: true, color: L.ACCENT, after: 120 }),
  center("Cifrado César, esteganografía y un algoritmo propio", 28, { after: 40 }),
  center("con autenticación de dos factores por imagen-llave", 28, { after: 160 }),
  center("Examen 2do parcial", 24, { after: 1000 }),
  field("Alumno(a)", "______________________________"),
  field("Materia", "Virología y Criptografía"),
  field("Grupo", "6NV68"),
  field("Docente", "Gustavo Martínez Vázquez"),
  field("Semestre", "2027-1"),
  new Paragraph({ spacing: { before: 900 }, children: [] }),
  center("Ciudad de México, octubre de 2026", 22),
);

// ───────────────────────── Índice ─────────────────────────
add(
  new Paragraph({ pageBreakBefore: true, children: [new TextRun({ text: "Contenido", bold: true, size: 32, color: L.ACCENT })], spacing: { after: 200 } }),
  new TableOfContents("Contenido", { hyperlink: true, headingStyleRange: "1-3" }),
  P("*Si el índice aparece vacío, en Word usa clic derecho → Actualizar campo (o F9).*", { align: AlignmentType.LEFT }),
);

// ───────────────────────── 2. Arquitectura ─────────────────────────
add(
  H1("2. Arquitectura del sistema"),
  P("ABCO sigue una arquitectura **cliente-servidor**. Todo el cálculo criptográfico vive en una sola biblioteca de .NET (`Prisma.Core`), así que hay **una sola implementación** de cada algoritmo; la web y la app Android son clientes delgados que suben y descargan imágenes y JSON por HTTPS."),
  ...img(DIAG("arquitectura"), "Arquitectura cliente-servidor de ABCO.", { maxH: 3 }),
  ...tbl(["Capa", "Tecnología", "Responsabilidad"], [
    ["Núcleo", "C# / .NET 10 (`Prisma.Core`), SkiaSharp para PNG", "César, esteganografía clásica, ABCO, métricas y estegoanálisis (solo para medir)"],
    ["API", "ASP.NET Core 10 Minimal APIs, EF Core + SQLite, JWT", "Autenticación y 2FA, ocultar/revelar, César, comparador, administración y auditoría"],
    ["Web", "HTML + CSS + JavaScript sin framework (`wwwroot`)", "Interfaz responsiva servida por la misma API"],
    ["Móvil", "Kotlin + Jetpack Compose, OkHttp, kotlinx.serialization", "App Android con las mismas funciones; URL del servidor configurable"],
    ["Despliegue", "Docker (imagen `aspnet:10.0`), volumen `/app/data`", "Servidor en la nube detrás de HTTPS"],
    ["Pruebas", "xUnit (29 pruebas), prototipo en Python, prueba de integración Android", "Vectores Python = C#, propiedades matemáticas, ida y vuelta, ataques, contrato de la API"],
  ], [0.14, 0.4, 0.46], "Tecnologías por capa."),
  H2("2.1 Modelo de datos"),
  ...tbl(["Tabla", "Campos", "Notas"], [
    ["Users", "Id, Username, PwdHash, PwdSalt, TwoFactorEnabled, KeyTokenHash, Role, CreatedUtc", "La contraseña se guarda con PBKDF2-HMAC-SHA256 (600 000 iteraciones) y sal. Del 2FA solo se guarda **SHA-256 del token**, nunca el token."],
    ["Audit", "Id, UserId, Username, Action, Mode, Bytes, Ms, Ok, Utc", "Bitácora sin secretos; en la consola el usuario se muestra ofuscado con César."],
  ], [0.12, 0.43, 0.45], "Tablas de la base de datos SQLite."),
);

// ───────────────────────── 3. Fundamentos matemáticos ─────────────────────────
add(
  H1("3. Fundamentos matemáticos"),
  P("La consigna pide apoyarse en matemáticas discretas, álgebra lineal y métodos numéricos. La tabla resume dónde aparece cada área en el sistema y cómo se comprueba."),
  ...tbl(["Área", "Dónde se usa", "Evidencia"], [
    ["Matemáticas discretas", "Aritmética modular en ℤ₂₆ (César) y ℤ₂₅₆ (ABCO). Unidades de ℤ₂₅₆: los impares (φ(256) = 128). **Euclides extendido** para inversos. **Teorema de Hull–Dobell** para el periodo del generador congruencial. **Permutaciones** (grupo simétrico Sₙ) con Fisher–Yates. XOR como suma en GF(2). Conteo |GL₂(ℤ₂₅₆)| = 2²⁸·6.", "Pruebas: periodo 256 del LCG, el recorrido no repite posiciones, conteo de GL₂ por fuerza bruta en ℤ₁₆."],
    ["Álgebra lineal", "Cifrado de Hill: producto matriz-vector, determinante, invertibilidad, **matriz adjunta** y A·A⁻¹ = I. Ataque de texto claro conocido A = C·P⁻¹.", "Prueba A·A⁻¹ = I con 2 000 matrices; ataque que recupera la matriz de Hill puro y falla contra ABCO."],
    ["Métodos numéricos", "**Entropía de Shannon**, **MSE/PSNR**, **χ²** contra frecuencias del español, estimación **Monte Carlo** de la avalancha, función **gamma incompleta regularizada** (serie y fracción continua de Lentz) y **aproximación de Lanczos** de ln Γ para el estegoanálisis.", "Prueba P(1, x) = 1 − e⁻ˣ y Γ(5) = 24 (sección de pruebas)."],
  ], [0.18, 0.52, 0.30], "Justificación matemática."),
  H2("3.1 Aritmética modular y el inverso en ℤ₂₅₆"),
  P("En ℤ₂₅₆ un número x tiene inverso multiplicativo si y solo si mcd(x, 256) = 1, es decir, si x es **impar**. El inverso se obtiene con el algoritmo de Euclides extendido, que encuentra enteros s, t con s·x + t·256 = 1; entonces x⁻¹ ≡ s (mod 256). Por ejemplo, 11·163 = 1793 = 7·256 + 1, así que 11⁻¹ = 163."),
  H2("3.2 Matrices invertibles e inversa por adjunta"),
  P("Una matriz A = [[a, b], [c, d]] sobre ℤ₂₅₆ es invertible si y solo si det(A) = ad − bc es invertible, es decir, impar. Su inversa es A⁻¹ = det(A)⁻¹ · [[d, −b], [−c, a]] (mod 256). El número de matrices invertibles es |GL₂(ℤ₂₅₆)| = 2⁴⁽⁸⁻¹⁾ · |GL₂(ℤ₂)| = 2²⁸ · 6 = 1 610 612 736; la fórmula se verificó por fuerza bruta en ℤ₁₆ (24 576 matrices)."),
  H2("3.3 Generador congruencial y teorema de Hull–Dobell"),
  P("El generador sᵢ = (a·sᵢ₋₁ + c) mod m tiene periodo completo m si y solo si: (1) mcd(c, m) = 1; (2) a − 1 es divisible por todos los factores primos de m; (3) si 4 divide a m, 4 divide a a − 1. Con m = 256 = 2⁸ las condiciones se reducen a **c impar** y **a ≡ 1 (mod 4)**. ABCO elige a = 4·(K₅ mod 63 + 1) + 1 y c = 2·(K₆ mod 128) + 1, de modo que los 256 desplazamientos aparecen antes de repetirse; se excluye a = 1, que daría una progresión aritmética trivial."),
  H2("3.4 Entropía, PSNR y χ²"),
  ...bullets([
    "**Entropía de Shannon:** H = −Σ pᵢ·log₂ pᵢ, en bits por byte (máximo 8). Un texto en español ronda 4 bits/byte; un buen cifrado se acerca a 8. Para mensajes cortos, el máximo alcanzable es log₂(n), porque n bytes no pueden mostrar los 256 valores.",
    "**PSNR:** 10·log₁₀(255² / MSE) en dB, con MSE = (1/N)·Σ(xᵢ − yᵢ)². Por encima de ~40 dB la diferencia es imperceptible.",
    "**χ² de Pearson:** Σ (Oᵢ − Eᵢ)² / Eᵢ. En César compara las letras observadas con las esperadas en español; en el estegoanálisis compara las frecuencias de los pares de valores (2k, 2k+1).",
  ]),
);

// ───────────────────────── 4. César ─────────────────────────
add(
  H1("4. Algoritmo 1: cifrado César"),
  H2("4.1 Qué es y para qué se usa en el sistema"),
  P("Es un cifrado clásico de sustitución monoalfabética: cada letra se desplaza k posiciones en un alfabeto de n letras, C = (P + k) mod n. Solo hay n claves posibles, por lo que **no protege información real**; en ABCO tiene tres usos declarados:"),
  ...bullets([
    "**Laboratorio César** (web y Android): cifrar, descifrar y **romper** automáticamente con fuerza bruta y χ².",
    "**Ofuscación de caracteres** en la consola de administración: los nombres de usuario se muestran desplazados 13 posiciones sobre los 95 caracteres ASCII imprimibles. Es ofuscación, no cifrado: el botón \"Revelar\" la deshace.",
    "**Pieza interna de ABCO**: el desplazamiento sᵢ que se suma a cada bloque es un César que cambia en cada bloque.",
  ]),
  H2("Seudocódigo"),
  ...steps([
    "Elegir el alfabeto: A–Z (n = 26) o A–Z con Ñ (n = 27).",
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
  ...img(DIAG("cesar_crack"), "Diagrama de flujo del ataque χ² a César.", { maxH: 6.5 }),
);

// ───────────────────────── 5. Esteganografía ─────────────────────────
add(
  H1("5. Algoritmo 2: esteganografía"),
  H2("5.1 Qué es y para qué se usa en el sistema"),
  P("La esteganografía oculta la **existencia** del mensaje. La técnica usada reemplaza el bit menos significativo de los bytes de una imagen por los bits del mensaje: el valor de cada canal cambia como mucho en 1/255, lo cual es invisible. El sistema la usa de dos formas:"),
  ...bullets([
    "**Esteganografía clásica** (Ocultar/Revelar): bits menos significativos en orden y **sin clave**. Sirve para demostrar que cualquiera puede leer el mensaje.",
    "**Capa de transporte de ABCO**: el cifrado de ABCO se escribe en los bits menos significativos, pero en un orden secreto (sección 5.6).",
  ]),
  H2("Seudocódigo"),
  ...steps([
    "Normalizar la imagen a RGB de 8 bits: N = ancho × alto × 3 canales, en orden R, G, B por fila.",
    "Formar la trama: longitud del mensaje (4 bytes, big-endian) seguida del mensaje en UTF-8.",
    "Convertir la trama en bits, del más significativo al menos significativo de cada byte.",
    "Para j = 0, 1, …: canal[j] ← (canal[j] AND 11111110₂) OR bit[j].",
    "Guardar como PNG (formato sin pérdida).",
    "Para extraer: leer el bit menos significativo de los primeros 32 canales (longitud L) y después 8·L bits más.",
  ]),
  H2("5.3 Ejemplo resuelto"),
  ...tbl(["Paso", "Valor"], [
    ["Canal original", "104 = 01101000₂"],
    ["Bit secreto", "1"],
    ["Reemplazar el bit menos significativo", "0110100**1**₂"],
    ["Canal modificado", "105 (cambio de 1/255, invisible)"],
  ], [0.4, 0.6], "Inserción de un bit en el bit menos significativo."),
  H2("Diagrama de flujo"),
  ...img(DIAG("lsb_clasico"), "Diagrama de flujo de la esteganografía clásica (ocultar).", { maxH: 6.5 }),
);

// ───────────────────────── 6. ABCO ─────────────────────────
add(
  H1("6. Algoritmo 3 (inventado): ABCO"),
  H2("6.1 Idea y motivación"),
  P("ABCO generaliza César con álgebra lineal. César hace C = P + k sobre **un** símbolo; el cifrado afín, C = a·P + k; el cifrado de Hill, C = A·P sobre un vector. ABCO hace"),
  new Paragraph({ alignment: AlignmentType.CENTER, spacing: { before: 80, after: 160 }, children: [new TextRun({ text: "Cᵢ = A · (Pᵢ ⊕ Cᵢ₋₁) + sᵢ   (mod 256),     sᵢ = (a · sᵢ₋₁ + c) mod 256", bold: true, size: 24, font: L.MONO })] }),
  P("sobre **pares de bytes**, donde A es una matriz secreta 2×2 y sᵢ es un desplazamiento César **distinto en cada bloque**. Después, en lugar de escribir el resultado en orden (como la esteganografía clásica), lo esconde en los bits menos significativos de la imagen siguiendo un **recorrido de píxeles barajado con la clave**, y lo autentica con un HMAC."),
  ...tbl(["Problema de los algoritmos base", "Qué aporta ABCO"], [
    ["César conserva el histograma y se rompe con χ² en ~1 ms.", "La matriz y el encadenamiento aplanan las frecuencias: entropía de 4.10 a 7.93 bits/byte."],
    ["Hill puro cae con un ataque de texto claro conocido (A = C·P⁻¹).", "El César dinámico y el encadenamiento rompen la linealidad simple: el mismo ataque predice 0 de 1 000 bloques."],
    ["Dos bloques iguales dan el mismo cifrado (César, Hill).", "El encadenamiento con el bloque anterior y sᵢ variable hacen que \"AAAA…\" produzca bloques distintos."],
    ["La esteganografía clásica se lee sin clave.", "Sin la frase no se conoce el orden de los bits: la lectura secuencial da ruido."],
    ["Ni César ni la esteganografía clásica detectan alteraciones.", "HMAC-SHA256 (truncado a 8 B) rechaza una frase incorrecta o un solo bit alterado."],
  ], [0.48, 0.52], "Qué corrige ABCO de los algoritmos base."),
  H2("6.2 Componentes"),
  ...tbl(["Componente", "Herramienta", "Área matemática"], [
    ["Derivación de claves", "PBKDF2-HMAC-SHA256 (100 000 it.) y SHA-256", "—"],
    ["Matriz de Hill 2×2 (det impar)", "Muestreo con rechazo; inversa por adjunta y Euclides extendido", "Álgebra lineal, discretas"],
    ["César dinámico", "LCG mod 256 con Hull–Dobell", "Teoría de números"],
    ["Encadenamiento", "XOR con el bloque cifrado anterior", "Suma en GF(2)"],
    ["Recorrido secreto", "Fisher–Yates con LCG de 64 bits y muestreo con rechazo", "Permutaciones (Sₙ)"],
    ["Transporte", "Reemplazo del bit menos significativo", "—"],
    ["Integridad", "HMAC-SHA256 truncado a 8 bytes", "—"],
  ], [0.3, 0.45, 0.25], "Componentes de ABCO."),
  H2("6.3 Derivación de claves"),
  ...steps([
    "Generar una sal aleatoria de 16 bytes (se guarda en la imagen).",
    "K = PBKDF2-HMAC-SHA256(frase, sal, 100 000 iteraciones, 32 bytes).",
    "Matriz: h = SHA-256(K ‖ \"hill\" ‖ ctr), A = [[h₀, h₁], [h₂, h₃]]. Si det(A) es par, ctr ← ctr + 1 y repetir (muestreo con rechazo).",
    "A⁻¹ = det(A)⁻¹ · adj(A) mod 256.",
    "César dinámico: s₀ = K[4], a = 4·(K[5] mod 63 + 1) + 1, c = 2·(K[6] mod 128) + 1.",
    "Bloque inicial: C₀ = (K[7], K[8]). Semilla del recorrido: K[16..23] (little-endian). Clave del HMAC: k_mac = SHA-256(K ‖ \"mac\").",
  ]),
  ...img(DIAG("prisma_derivacion"), "Derivación de claves de ABCO.", { maxH: 6.2 }),
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
  ...img(DIAG("prisma_bloque"), "Cifrado por bloques de ABCO.", { maxH: 6.6 }),
  ...img(DIAG("prisma_descifrar"), "Descifrado por bloques de ABCO.", { maxH: 5.8 }),
  H2("Ejemplo"),
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
  ], [0.18, 0.55, 0.27], "Ejemplo numérico de ABCO."),
  H2("6.6 Capa esteganográfica: recorrido secreto"),
  ...steps([
    "Normalizar la portadora a RGB de 8 bits (las fotos de más de 1 280 px por lado se reducen).",
    "Escribir la **sal** en el bit menos significativo de los canales 0..127, en orden: el receptor la necesita antes de conocer la clave.",
    "Calcular el recorrido R = FisherYates([128, …, N − 1], semilla), con un LCG de 64 bits y muestreo con rechazo para no sesgar el módulo.",
    "Trama = longitud (4 B) ‖ C ‖ etiqueta, con etiqueta = HMAC-SHA256(k_mac, C)[0..7].",
    "El bit j de la trama va al bit menos significativo del canal R[j]. Se guarda como PNG.",
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
  ...img(DIAG("prisma_ocultar"), "ABCO: ocultar un mensaje.", { maxH: 7.2 }),
  ...img(DIAG("prisma_revelar"), "ABCO: revelar un mensaje.", { maxH: 7.2 }),
  H2("Seudocódigo completo"),
  P("Cada función del algoritmo, en pasos numerados."),
  H3("Derivar claves (frase, sal)"),
  ...steps([
    "Calcular K = PBKDF2-HMAC-SHA256(frase en UTF-8, sal, 100 000 iteraciones, 32 bytes).",
    "Iniciar el contador ctr en 0.",
    "Calcular h = SHA-256(K ‖ \"hill\" ‖ ctr) y formar la matriz A = [[h₀, h₁], [h₂, h₃]].",
    "Si det(A) es par, aumentar ctr en 1 y volver al paso 3 (muestreo con rechazo).",
    "Calcular la inversa A⁻¹ = det(A)⁻¹ · adj(A) mod 256.",
    "Tomar s₀ = K[4], a = 4·(K[5] mod 63 + 1) + 1 y c = 2·(K[6] mod 128) + 1.",
    "Tomar C₀ = (K[7], K[8]), la semilla = K[16..23] (little-endian) y k_mac = SHA-256(K ‖ \"mac\").",
    "Devolver (A, A⁻¹, s₀, a, c, C₀, semilla, k_mac).",
  ]),
  H3("Cifrar (mensaje P, claves)"),
  ...steps([
    "Si P tiene longitud impar, agregar un byte 0 al final.",
    "Iniciar prev = C₀, s = s₀ y el cifrado C vacío.",
    "Tomar el siguiente par de bytes (P[i], P[i+1]), con i = 0, 2, 4, …",
    "Actualizar el desplazamiento: s = (a·s + c) mod 256.",
    "Encadenar con el bloque anterior: X = (P[i] ⊕ prev₁, P[i+1] ⊕ prev₂).",
    "Aplicar la matriz: Y = A·X mod 256.",
    "Desplazar: Cᵢ = ((Y₁ + s) mod 256, (Y₂ + s) mod 256); agregar Cᵢ a C y hacer prev = Cᵢ.",
    "Si quedan pares, volver al paso 3; si no, devolver C.",
  ]),
  H3("Descifrar (cifrado C, claves)"),
  ...steps([
    "Iniciar prev = C₀, s = s₀ y el mensaje P vacío.",
    "Tomar el siguiente par (C[i], C[i+1]) y actualizar s = (a·s + c) mod 256.",
    "Quitar el desplazamiento y la matriz: X = A⁻¹ · ((C[i] − s) mod 256, (C[i+1] − s) mod 256) mod 256.",
    "Deshacer el encadenamiento: agregar (X₁ ⊕ prev₁, X₂ ⊕ prev₂) a P y hacer prev = (C[i], C[i+1]).",
    "Si quedan pares, volver al paso 2; si no, devolver P.",
  ]),
  H3("Ocultar (imagen, mensaje, frase)"),
  ...steps([
    "Reducir la imagen si mide más de 1 280 px por lado y normalizarla a canales RGB de 8 bits (N canales).",
    "Generar una sal aleatoria de 16 bytes y derivar las claves con la frase.",
    "Cifrar el mensaje en UTF-8: C = Cifrar(mensaje, claves).",
    "Formar la trama: longitud del mensaje (4 bytes, big-endian) ‖ C ‖ HMAC-SHA256(k_mac, C)[0..7].",
    "Si 8·|trama| > N − 128, terminar con el error «el mensaje no cabe».",
    "Escribir los 128 bits de la sal en el bit menos significativo de los canales 0..127.",
    "Calcular el recorrido R = Fisher–Yates([128, …, N − 1], semilla).",
    "Escribir el bit j de la trama en el bit menos significativo del canal R[j].",
    "Codificar la imagen como PNG y devolverla.",
  ]),
  H3("Revelar (imagen, frase)"),
  ...steps([
    "Normalizar la imagen a canales RGB de 8 bits.",
    "Leer la sal de los canales 0..127 y derivar las claves con la frase.",
    "Calcular el recorrido R con la semilla.",
    "Leer la longitud L (4 bytes) siguiendo R; si es negativa o no cabe, terminar con el error genérico.",
    "Leer el cifrado C y la etiqueta T siguiendo R.",
    "Comparar T con HMAC-SHA256(k_mac, C)[0..7] en tiempo constante; si no coinciden, terminar con el error genérico.",
    "Descifrar C y devolver los primeros L bytes.",
  ]),
  H3("Fisher–Yates (lista, semilla)"),
  ...steps([
    "Iniciar el generador con x = semilla y tomar i = |lista| − 1.",
    "Avanzar el generador: x = (x·6364136223846793005 + 1442695040888963407) mod 2⁶⁴ y tomar v = x div 2³².",
    "Si v ≥ ⌊2³² / (i+1)⌋·(i+1), volver al paso 2 (muestreo con rechazo, para no sesgar el módulo).",
    "Calcular j = v mod (i+1) e intercambiar lista[i] con lista[j].",
    "Restar 1 a i; si i > 0, volver al paso 2. Al terminar, devolver la lista.",
  ]),
  H2("6.8 Traza real del sistema"),
  P("Con el modo demo activo, cada operación ABCO deja una traza que se ve en la consola de administración y se imprime en la consola del servidor. Esta es la traza real de ocultar \"Hola Bob, nos vemos a las 18:00\" en `oceano.png` con la frase `girasol-azul-42` (el bloque 0 se puede verificar a mano: 72 ⊕ 167 = 239, 70·239 + 245·152 = 53 970 ≡ 210, (197·136 + 111) mod 256 = 23 y 210 + 23 = 233):"),
  ...code(traza),
  H2("6.9 Implementación y validación"),
  ...bullets([
    "El algoritmo se diseñó primero como **prototipo en Python** (`tools/prototipo/prisma_lite_proto.py`). Ese prototipo genera vectores de prueba deterministas (`vectors.json`) y la implementación en C# debe reproducir **bit a bit** la matriz, su inversa, los parámetros del LCG, la semilla, el HMAC, el cifrado y el recorrido.",
    "Clases: `PrismaKeys` (derivación), `PrismaCipher` (bloques), `Lcg64` y `FisherYates` (recorrido), `PrismaStego` (trama, sal, HMAC, bits menos significativos), `PrismaTrace` (traza para la consola admin).",
    "Comparaciones de etiquetas en tiempo constante (`CryptographicOperations.FixedTimeEquals`) y error genérico: el sistema no revela si falló la frase, la imagen o la integridad.",
  ]),
  H2("6.10 Lo que ABCO no es"),
  ...bullets([
    "No es comparable con un cifrador estándar auditado: no tiene análisis diferencial ni lineal publicado. Es un diseño **didáctico**.",
    "Su difusión es local y **hacia adelante**: un bit cambiado afecta a su bloque y a los siguientes (≈ 26 % de los bits desde ese bloque; ≈ 13 % sobre el mensaje completo), no al mensaje entero.",
    "Con muchos pares de texto claro y cifrado y suficiente esfuerzo, la estructura lineal en ℤ₂₅₆ admite ataques algebraicos más elaborados que el de Hill puro.",
    "Su seguridad práctica depende de la frase y de PBKDF2, no del tamaño del espacio de matrices.",
  ]),
);

// ───────────────────────── 7. Autenticación ─────────────────────────
add(
  H1("7. Autenticación con segundo factor (imagen-llave)"),
  P("El inicio de sesión usa usuario y contraseña. Cada usuario puede activar, de forma **opcional**, un segundo factor: una imagen-llave. Quien lo tenga activado debe presentar **usuario, contraseña e imagen**; ninguna de las tres basta por sí sola."),
  H2("7.1 Activación"),
  ...steps([
    "El usuario, con sesión iniciada, confirma su contraseña y elige una imagen (del catálogo o propia).",
    "El servidor genera un token aleatorio T de 32 bytes con un generador criptográfico.",
    "Oculta T en la imagen con ABCO usando la **contraseña** como frase, y devuelve `llave_<usuario>.png`.",
    "Guarda SHA-256(T) y marca el 2FA como activo. El token **nunca** se almacena.",
  ]),
  ...img(DIAG("activar_2fa"), "Activación del 2FA con imagen-llave.", { maxH: 5.2 }),
  H2("7.2 Inicio de sesión"),
  ...steps([
    "El cliente envía usuario y contraseña a `POST /api/auth/login`. El servidor verifica PBKDF2.",
    "Si el usuario **no** tiene 2FA, responde con el token JWT.",
    "Si lo tiene, responde `requires2fa: true` (sin token). El cliente pide la imagen-llave.",
    "El cliente envía usuario, contraseña e imagen a `POST /api/auth/login/2fa`.",
    "El servidor verifica de nuevo la contraseña, revela T' con ABCO usando la contraseña, y compara SHA-256(T') con el hash guardado en tiempo constante.",
    "Si coincide, entrega el JWT; si no, responde 401 con un mensaje genérico.",
  ]),
  ...img(DIAG("login_2fa"), "Inicio de sesión con segundo factor.", { maxH: 7 }),
  H2("7.3 Propiedades de seguridad"),
  ...tbl(["Situación del atacante", "Resultado"], [
    ["Conoce la contraseña, no tiene la imagen", "No entra: falta el token."],
    ["Tiene la imagen, no conoce la contraseña", "No entra: sin la contraseña no puede revelar el token (y el primer paso ya falla)."],
    ["Roba la base de datos", "Obtiene hashes PBKDF2 y SHA-256(T): no puede reconstruir T ni la imagen."],
    ["Altera o recomprime la imagen", "El HMAC falla: la llave deja de servir (hay que regenerarla)."],
    ["Fuerza bruta en línea", "Limitada a 10 intentos por minuto por IP en las rutas de autenticación (HTTP 429)."],
  ], [0.45, 0.55], "Qué protege la imagen-llave."),
  P("Consecuencias operativas: si el usuario cambia su contraseña o pierde la imagen, debe generar una llave nueva; un administrador puede restablecer el 2FA desde la consola."),
  H2("7.4 Contraseña olvidada y restablecimiento"),
  P("ABCO no envía correos: la recuperación pasa por un administrador, lo que mantiene el sistema simple y deja registro en la auditoría."),
  ...steps([
    "En el inicio de sesión (web y app), el enlace **«¿Olvidaste tu contraseña?»** muestra: «Contacta al administrador para que restablezca tu contraseña».",
    "El administrador abre **Admin → Usuarios** y elige **Restablecer contraseña**.",
    "El servidor genera una **contraseña temporal** aleatoria de 12 caracteres (sin caracteres ambiguos como 0/O o 1/l), guarda su hash PBKDF2, marca `MustChangePassword` y **desactiva el 2FA**, porque la imagen-llave estaba cifrada con la contraseña anterior.",
    "El administrador ve la temporal **una sola vez** (con botón para copiarla) y se la entrega al usuario por un medio seguro.",
    "El usuario entra con la temporal. El token que recibe lleva una marca: el servidor solo le permite consultar su perfil y cambiar la contraseña (cualquier otra ruta responde 403).",
    "La web y la app muestran la pantalla obligatoria **«Cambia tu contraseña»**. Al guardarla, el servidor entrega una sesión nueva sin la marca.",
  ]),
  ...img(DIAG("restablecer_contrasena"), "Contraseña olvidada: restablecimiento por el administrador y cambio obligatorio.", { maxH: 6.8 }),
  P("Cualquier usuario puede además cambiar su contraseña en **Cuenta**. Si tenía 2FA, se desactiva por la misma razón y el sistema le avisa que genere otra imagen-llave."),
  H2("7.5 Roles"),
  P("Hay dos roles: **usuario** y **administrador**. En **Admin → Usuarios**, un administrador puede hacer administrador a un usuario o quitarle el rol. El servidor no permite que un administrador se quite el rol a sí mismo ni que el sistema se quede sin administradores (responde 409). Los cambios quedan en la auditoría, identificando al usuario afectado por su número y no por su nombre."),
);

// ───────────────────────── Pantallas ─────────────────────────
/**
 * Una pantalla: subtítulo, descripción breve y la captura con su pie de figura.
 * @param {string} title Nombre de la pantalla.
 * @param {string} file Archivo en docs/capturas.
 * @param {string} text Qué muestra y qué se puede hacer en ella.
 * @param {object} [size] Tamaño máximo de la imagen en pulgadas ({ maxW, maxH }).
 */
const screen = (title, file, text, size) => [
  new Paragraph({ heading: HeadingLevel.HEADING_3, children: [new TextRun(title)], keepNext: true, keepLines: true }),
  P(text, { para: { keepNext: true, keepLines: true } }),   // título, texto e imagen siempre en la misma página
  ...img(SHOT(file), title + ".", size),
];
const WEB = { maxW: 6.0, maxH: 3.45 }, PHONE = { maxW: 2.9, maxH: 5.2 }, PHONE_WIDE = { maxW: 6.3, maxH: 3.2 };
add(
  H1("Pantallas (Interfaz web)"),
  P("La interfaz web se sirve desde el mismo servidor y funciona en computadora y en teléfono. Todas las capturas se tomaron del sistema en ejecución; salvo la última, en tema claro."),
  ...screen("Inicio de sesión", "web_01_login.jpg", "Usuario y contraseña, con botón para mostrar u ocultar la contraseña, enlace para crear cuenta y «¿Olvidaste tu contraseña?». La barra superior muestra el logo y el botón de tema.", WEB),
  ...screen("¿Olvidaste tu contraseña?", "web_02_olvide.jpg", "Al pulsar el enlace aparece el aviso para contactar al administrador, quien genera una contraseña temporal.", WEB),
  ...screen("Inicio de sesión con imagen-llave", "web_13_login_2fa.jpg", "Si la cuenta tiene el segundo factor activado, después de la contraseña se pide la imagen-llave (llave.png).", WEB),
  ...screen("Cambio obligatorio de contraseña", "web_11_cambiar.jpg", "Quien entra con una contraseña temporal solo ve esta pantalla hasta elegir una nueva; el servidor bloquea las demás rutas.", WEB),
  ...screen("Ocultar mensaje", "web_03_ocultar.jpg", "Se elige la portadora (catálogo o imagen propia), se escribe el mensaje, se elige ABCO o la esteganografía clásica y la frase. El resultado muestra la imagen, el PSNR, los bytes usados y el botón para descargar el PNG.", WEB),
  ...screen("Revelar mensaje", "web_04_revelar.jpg", "Se carga el PNG recibido, se elige el modo y se escribe la frase; el sistema muestra el mensaje oculto.", WEB),
  ...screen("Revelar con frase incorrecta", "web_05_revelar_error.jpg", "Con una frase equivocada o una imagen alterada, el sistema responde con un error genérico y muestra los bytes que leería un atacante, que parecen ruido.", WEB),
  ...screen("Laboratorio César", "web_06_cesar.jpg", "Cifra, descifra y rompe textos con César. Al romper, prueba todas las claves y las ordena por χ²; la correcta queda primero.", WEB),
  ...screen("Comparador", "web_07_comparador.jpg", "El mismo mensaje con César, esteganografía clásica y ABCO: bytes de salida, tiempo, entropía y si se detecta una alteración.", WEB),
  ...screen("Cuenta y segundo factor", "web_12_cuenta_2fa.jpg", "Datos de la cuenta, cambio de contraseña y activación del 2FA: al activarlo se descarga la imagen-llave generada.", WEB),
  ...screen("Consola de administración", "web_08_admin.jpg", "Traza paso a paso de la última operación ABCO (matriz, desplazamientos, bloques y recorrido) y mapa de diferencias entre la imagen original y la imagen con el mensaje.", WEB),
  ...screen("Administración de usuarios", "web_10_usuarios.jpg", "Usuarios con el nombre ofuscado con César y botón para revelarlo. Acciones: restablecer contraseña, hacer o quitar administrador y restablecer el 2FA.", WEB),
  ...screen("Contraseña temporal", "web_09_temporal.jpg", "Al restablecer una contraseña, el administrador ve la temporal una sola vez, con botón para copiarla.", WEB),
  ...screen("Tema oscuro", "web_14_oscuro.jpg", "El botón de la barra superior alterna entre tema automático (sigue al sistema), claro y oscuro.", WEB),
  H1("Pantallas (Aplicación móvil)"),
  P("Capturas de la app Android tomadas en un teléfono real con Android 11, conectado al servidor de la red local."),
  ...screen("Inicio de sesión", "android_01_login.jpg", "Logo, usuario y contraseña, «¿Olvidaste tu contraseña?» y el botón de ajustes para elegir el tema y la URL del servidor.", PHONE),
  ...screen("Cambio obligatorio de contraseña", "android_08_cambiar.jpg", "Igual que en la web: tras entrar con una contraseña temporal hay que elegir una nueva antes de continuar.", PHONE),
  ...screen("Ocultar mensaje", "android_02_ocultar.jpg", "Catálogo de portadoras, galería, selector de modo (ABCO o esteganografía clásica) y la barra de navegación inferior.", PHONE),
  ...screen("Ocultar mensaje en horizontal", "android_03_ocultar_horizontal.jpg", "En pantallas anchas la navegación pasa a un riel lateral y las tarjetas se reparten en dos columnas.", PHONE_WIDE),
  ...screen("Revelar mensaje con el teclado abierto", "android_04_revelar_teclado.jpg", "La pantalla se desplaza para que el campo de la frase quede visible sobre el teclado.", PHONE),
  ...screen("Administración", "android_05_admin.jpg", "Usuarios con nombre ofuscado y sus acciones, visible solo para administradores.", PHONE),
  ...screen("Administración en horizontal", "android_06_admin_horizontal.jpg", "Usuarios y auditoría lado a lado.", PHONE_WIDE),
  ...screen("Contraseña temporal", "android_07_temporal.jpg", "La contraseña temporal generada, con botón para copiarla.", PHONE),
  ...screen("Cuenta en tema oscuro", "android_09_cuenta_oscuro.jpg", "Perfil, selector de tema (automático, claro u oscuro) y cambio de contraseña.", PHONE),
);

// ───────────────────────── 10. API ─────────────────────────
add(
  H1("10. API REST"),
  P("Todas las rutas responden JSON (camelCase) y los errores tienen la forma `{\"error\": \"mensaje\"}`. Las rutas protegidas requieren `Authorization: Bearer <token>`. El contrato completo está en `docs/API.md`."),
  ...tbl(["Método y ruta", "Uso"], [
    ["POST /api/auth/register", "Registro (usuario 3–32 caracteres, contraseña ≥ 8)."],
    ["POST /api/auth/login", "Paso 1: devuelve el token o `requires2fa: true`."],
    ["POST /api/auth/login/2fa", "Paso 2 (multipart): usuario, contraseña e imagen-llave."],
    ["GET /api/account/me", "Perfil, estado del 2FA y si debe cambiar la contraseña."],
    ["POST /api/account/password", "Cambiar contraseña; devuelve una sesión nueva (desactiva el 2FA si estaba activo)."],
    ["POST /api/account/2fa/enable · /disable", "Activar (devuelve la llave PNG) o desactivar el 2FA."],
    ["GET /api/catalog · /api/catalog/{id}", "Catálogo de imágenes (generadas por código)."],
    ["POST /api/stego/capacity · /embed · /extract", "Capacidad, ocultar y revelar."],
    ["POST /api/caesar/encrypt · /decrypt · /crack", "Laboratorio César."],
    ["POST /api/compare", "César, esteganografía clásica y ABCO sobre el mismo mensaje."],
    ["GET /api/admin/audit · /users · /trace/last · /diffmap/last", "Consola de administración (rol admin)."],
    ["POST /api/admin/deobfuscate · /users/{id}/reset-2fa", "Revelar la ofuscación y restablecer el 2FA."],
    ["POST /api/admin/users/{id}/reset-password", "Contraseña temporal (se muestra una vez) y cambio obligatorio."],
    ["POST /api/admin/users/{id}/role", "Hacer administrador o quitar el rol (no a uno mismo ni al último administrador)."],
    ["GET /api/health", "Estado del servicio."],
  ], [0.45, 0.55], "Rutas de la API."),
);

// ───────────────────────── 11. Manual ─────────────────────────
add(
  H1("11. Manual de usuario"),
  H2("11.1 Cliente web"),
  P("La web se sirve desde el mismo servidor (por ejemplo, `http://localhost:5080` en local). Es responsiva: en el teléfono muestra una columna y un menú desplegable; desde 768 px, formulario y resultado lado a lado."),
  ...steps([
    "**Crear cuenta** e **iniciar sesión**.",
    "**Ocultar:** elegir una imagen del catálogo o propia, escribir el mensaje, elegir ABCO (con frase) o esteganografía clásica, y descargar el PNG.",
    "**Revelar:** cargar el PNG, elegir el modo y escribir la frase.",
    "**César:** cifrar, descifrar o romper un texto.",
    "**Comparador:** ver salida, tamaño, tiempo, entropía e integridad de cada algoritmo.",
    "**Cuenta:** cambiar la contraseña, activar o desactivar el 2FA y descargar la imagen-llave.",
    "**Tema:** el botón de la barra superior alterna entre **automático** (sigue el modo claro u oscuro del sistema), **claro** y **oscuro**; la elección se recuerda en el navegador.",
    "**¿Olvidaste tu contraseña?:** en el inicio de sesión; indica contactar al administrador.",
    "**Admin** (solo administradores): traza de ABCO, mapa de diferencias, usuarios (revelar nombre, restablecer contraseña, cambiar rol, restablecer 2FA) y auditoría.",
  ]),
  P("Las capturas de cada pantalla están en el capítulo «Pantallas (Interfaz web)»."),
  H2("11.2 App Android"),
  ...steps([
    "Instalar `ABCO.apk`: en el teléfono, permitir la instalación desde el origen (navegador o administrador de archivos) y abrir el APK.",
    "En la pantalla de inicio de sesión, tocar el ícono de ajustes y escribir la URL del servidor (por ejemplo `https://tu-dominio`); pulsar **Probar conexión**.",
    "Iniciar sesión. Si la cuenta tiene 2FA, la app pide elegir la imagen-llave (se puede tomar de Descargas).",
    "Usar las pestañas **Ocultar, Revelar, César, Comparador y Cuenta**. Para guardar un PNG, la app abre el selector de documentos; para enviarlo, usa **Compartir como archivo**.",
  ]),
  ...tbl(["Pantalla", "Qué hace"], [
    ["Inicio de sesión", "Usuario y contraseña; ícono de ajustes para cambiar y probar la URL del servidor. Si la cuenta tiene 2FA, aparece el paso 2 para elegir la imagen-llave con el selector de documentos."],
    ["Registro", "Crea la cuenta con las mismas validaciones que la web."],
    ["Ocultar", "Portadora del catálogo (rejilla de miniaturas) o de la galería; mensaje; modo ABCO o esteganografía clásica; capacidad; resultado con PSNR y botones Guardar y Compartir como archivo."],
    ["Revelar", "Elegir el PNG, modo y frase; muestra el mensaje o el error genérico junto con los bytes que vería un atacante."],
    ["César", "Clave con control deslizante, interruptor de Ñ, cifrar, descifrar y romper con χ² (las 5 mejores claves)."],
    ["Comparador", "Tarjetas por algoritmo con bytes, tiempo, entropía e integridad."],
    ["Cuenta", "Estado del 2FA; activar (elige imagen y confirma contraseña, guarda `llave_<usuario>.png`) o desactivar; cambiar contraseña; tema (automático, claro u oscuro); cerrar sesión."],
    ["Admin (solo administradores)", "Usuarios con nombre ofuscado y botón Revelar; restablecer contraseña (temporal con botón copiar), cambiar rol y restablecer 2FA; auditoría; traza de la última operación ABCO y mapa de diferencias."],
  ], [0.2, 0.8], "Pantallas de la app Android."),
  P("**Diseño adaptativo.** Con menos de 600 dp de ancho (teléfono en vertical) la navegación va en una barra inferior; con 600 dp o más (teléfono en horizontal, tabletas) pasa a un riel lateral con desplazamiento propio, de modo que los seis destinos nunca se desbordan aunque la pantalla sea baja (en alturas menores a 480 dp solo el destino seleccionado muestra su etiqueta). El contenido decide su distribución por el ancho disponible: en pantallas anchas las tarjetas del formulario se reparten en dos columnas y, al haber resultado, se muestra formulario | resultado, hasta un ancho máximo de 1 200 dp. Los contenedores con desplazamiento reservan el espacio del teclado y llevan el campo enfocado a la vista. Los selectores segmentados usan una sola línea que reduce su tamaño si no cabe, así funcionan también con la fuente del sistema agrandada."),
  P("**Tema.** En Cuenta (y en los ajustes del inicio de sesión) se elige **Automático**, **Claro** u **Oscuro**; Automático sigue al teléfono. La paleta es la misma que la de la web: verde de terminal como acento y carbón y gris acero como neutros, tomados del logo."),
  P("Las capturas de la app están en el capítulo «Pantallas (Aplicación móvil)»."),
);

// ───────────────────────── 12. Despliegue ─────────────────────────
add(
  H1("12. Instalación y despliegue"),
  H2("12.1 Servidor en local"),
  ...code(`cd Examen_Prisma
dotnet test                                              # 29 pruebas
dotnet run --project src/Prisma.Api --launch-profile http # http://localhost:5080`),
  P("En desarrollo se crea el administrador `admin` con la contraseña definida en `appsettings.Development.json`; en producción se define con la variable `Admin__Password`."),
  H2("12.2 Servidor en la nube (Docker)"),
  ...steps([
    "Construir la imagen: `docker build -f deploy/Dockerfile -t abco .`",
    "Definir las variables `Jwt__Key` (≥ 32 caracteres aleatorios) y `Admin__Password`.",
    "Montar un volumen persistente en `/app/data` (base de datos y catálogo).",
    "Publicar el puerto 8080 detrás del HTTPS del proveedor (Render, Railway, Azure App Service, Cloud Run…).",
    "Comprobar `GET https://<dominio>/api/health`.",
  ]),
  P("La guía completa está en `deploy/DEPLOY.md`."),
  H2("12.3 App Android"),
  ...code(`cd android
./gradlew :app:assembleRelease -PserverUrl=https://<dominio>   # URL por defecto del APK
# APK firmado: app/build/outputs/apk/release/ (se entrega como ABCO.apk)`),
  P("Sin recompilar, la URL también se cambia en la app. La app acepta servidores `http://` (por ejemplo, un equipo de la red local) y `https://`; con `http://` muestra un aviso de conexión sin cifrar. En la nube debe usarse HTTPS, porque con HTTP la contraseña y la imagen-llave viajarían sin cifrar."),
);

// ───────────────────────── 13. Pruebas ─────────────────────────
add(
  H1("13. Pruebas"),
  P("El proyecto `tests/Prisma.Core.Tests` contiene **29 pruebas xUnit**, todas en verde. Se agrupan así:"),
  ...tbl(["Grupo", "Qué verifica"], [
    ["Vectores del prototipo (3 casos)", "C# = Python: matriz, inversa, s₀, a, c, C₀, semilla, k_mac, cifrado, primeros valores del LCG y del recorrido."],
    ["Ejemplo \"Hi\"", "El ejemplo resuelto a mano de la sección 5.5."],
    ["Matemáticas", "A·A⁻¹ = I en 2 000 matrices; |GL₂(ℤ₁₆)| = 24 576; periodo 256 del LCG; el recorrido es una permutación; gamma incompleta y ln Γ."],
    ["Cifrado", "Ida y vuelta de 500 mensajes; bloques iguales → cifrados distintos; entropía de César vs. ABCO."],
    ["César", "CRIPTOGRAFIA → FULSWRJUDILD; HOLA → KROD; ida y vuelta con y sin Ñ; χ² encuentra la clave; ofuscación; las tildes no se filtran."],
    ["Esteganografía", "Ida y vuelta por PNG real; frase incorrecta y bit alterado → error genérico; capacidad; esteganografía clásica legible y ABCO no; χ² detecta una imagen saturada; reducción de portadoras grandes."],
  ], [0.3, 0.7], "Pruebas automatizadas."),
  P("La app Android tiene una prueba de integración (`ApiClientIntegrationTest`, JVM) que usa el mismo cliente HTTP de la app contra el servidor en ejecución: registro, login, sesión expirada, catálogo, capacidad, ocultar y revelar en ambos modos, frase incorrecta (422 con los bytes del atacante), César, comparador y el ciclo completo del 2FA. Pasó sin fallos. Además, la app se probó en un **teléfono real con Android 11** (Samsung SM-A305G) contra el servidor en la red local (`http://dev-services.lan:5080`): conexión, registro, inicio de sesión, ocultar y guardar el PNG en Descargas, revelarlo en el teléfono y en el servidor, romper César, comparador, activar el 2FA y volver a entrar con usuario, contraseña e imagen-llave. Esa prueba detectó que la política de red solo permitía HTTP hacia el emulador; se corrigió para aceptar servidores `http://` de la red local, con un aviso de conexión sin cifrar. En la versión ABCO se repitió la prueba en el mismo teléfono: logo e íconos nuevos, selector de tema (claro, oscuro y automático), navegación en vertical y en horizontal (riel sin desbordes y contenido a todo el ancho), campo de la frase visible con el teclado abierto en Revelar, pantalla Admin (restablecer contraseña, contraseña temporal y cambio de rol) y el cambio obligatorio de contraseña al entrar con la temporal. Las pruebas de integración de Android también cubren el restablecimiento y el bloqueo de rutas con contraseña temporal (3 pruebas, sin fallos)."),
  P("Además se probaron de punta a punta, contra el servidor en ejecución: registro, login sin y con 2FA (llave correcta, imagen equivocada, contraseña equivocada), ocultar y revelar en ambos modos, comparador, laboratorio César, rutas de administración (un usuario normal recibe 403) y la versión Release en modo producción con variables de entorno, igual que en el contenedor."),
);

// ───────────────────────── 14. Límites ─────────────────────────
add(
  H1("14. Límites y modelo de amenaza"),
  ...bullets([
    "**ABCO es didáctico.** Para proteger datos reales debe usarse un cifrador estándar y auditado. La sección «Lo que ABCO no es» (5.10) resume sus límites.",
    "**Servidor de confianza.** El mensaje y la frase viajan por HTTPS y el servidor los procesa en memoria sin guardarlos; quien controle el servidor podría verlos. Es el modelo habitual de los servicios web de cifrado de archivos.",
    "**Detectabilidad.** Todo ocultamiento por reemplazo del bit menos significativo es detectable estadísticamente con ocupación alta. El recorrido secreto impide **extraer** el mensaje, no ocultar que la imagen fue modificada.",
    "**Fragilidad.** Recompresión, redimensionado o capturas de pantalla destruyen el mensaje y la imagen-llave.",
    "**\"Solo nuestra app puede leerlo\"** no es una propiedad criptográfica (principio de Kerckhoffs): el algoritmo es público. La propiedad real es que, sin la frase, no se puede localizar, leer ni alterar el mensaje sin que se note.",
    "**César** nunca protege contraseñas: solo ofusca nombres en la consola y sirve como laboratorio.",
  ]),
);

// ───────────────────────── 15. Conclusiones ─────────────────────────
add(
  H1("15. Conclusiones"),
  P("ABCO muestra cómo dos técnicas clásicas y frágiles, el cifrado César y la esteganografía, pueden combinarse con herramientas de álgebra lineal (matriz de Hill invertible en ℤ₂₅₆), matemáticas discretas (Euclides extendido, Hull–Dobell, permutaciones de Fisher–Yates) y métodos numéricos (entropía, χ², PSNR, Monte Carlo, gamma incompleta) para obtener un algoritmo con propiedades **medibles**: aplana las frecuencias del idioma, resiste el ataque que rompe a Hill, oculta el orden de los bits y detecta alteraciones."),
  P("También deja claro, con números, dónde no llega: su difusión es local y su seguridad depende de la frase. Integrado en un sistema real (backend .NET, web responsiva y app Android) y aplicado a un segundo factor de autenticación, el algoritmo cumple una función concreta y verificable dentro del sistema."),
);

// ───────────────────────── 16. Referencias ─────────────────────────
add(
  H1("16. Referencias"),
  ...[
    "Barker, E. (2020). *Recommendation for key management: Part 1 – General* (NIST SP 800-57 Part 1 Rev. 5). National Institute of Standards and Technology. https://doi.org/10.6028/NIST.SP.800-57pt1r5",
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
// Se crea al final porque `numberingConfigs` ya debe tener las listas numeradas que agregó steps().
// Página carta con márgenes de 1 in; la portada no lleva encabezado ni pie (titlePage); el campo
// del índice se actualiza al abrir el documento (updateFields).
const doc = new Document({
  creator: "Equipo ABCO",
  title: "Documentación del sistema ABCO",
  description: "Virología y Criptografía · 6NV68 · UPIICSA–IPN",
  features: { updateFields: true },
  styles: {
    default: { document: { run: { font: L.FONT, size: 21 } } },
    paragraphStyles: [
      { id: "Heading1", name: "Heading 1", basedOn: "Normal", next: "Normal", quickFormat: true,
        run: { size: 34, bold: true, color: L.ACCENT, font: L.FONT }, paragraph: { spacing: { before: 120, after: 240 }, outlineLevel: 0 } },
      { id: "Heading2", name: "Heading 2", basedOn: "Normal", next: "Normal", quickFormat: true,
        run: { size: 27, bold: true, color: "1F4A14", font: L.FONT }, paragraph: { spacing: { before: 280, after: 120 }, outlineLevel: 1, keepNext: true } },
      { id: "Heading3", name: "Heading 3", basedOn: "Normal", next: "Normal", quickFormat: true,
        run: { size: 23, bold: true, color: "3B3B55", font: L.FONT }, paragraph: { spacing: { before: 200, after: 100 }, outlineLevel: 2, keepNext: true } },
    ],
  },
  numbering: { config: L.numberingConfigs },
  sections: [{
    properties: { page: { size: { width: 12240, height: 15840 }, margin: { top: 1440, bottom: 1440, left: 1440, right: 1440 } }, titlePage: true },
    headers: {
      default: new Header({ children: [new Paragraph({ alignment: AlignmentType.RIGHT, border: { bottom: { style: BorderStyle.SINGLE, size: 4, color: "DDDBEE", space: 4 } },
        children: [new TextRun({ text: "ABCO · Virología y Criptografía · 6NV68", size: 16, color: "777777" })] })] }),
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

// Empaqueta el .docx y lo escribe en disco.
Packer.toBuffer(doc).then((buf) => { fs.writeFileSync(out, buf); console.log("→", out, `(${(buf.length / 1024).toFixed(0)} KB)`); });
