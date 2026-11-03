/**
 * Utilidades para construir el documento Word de ABCO con la librería docx.
 *
 * Ofrece constructores breves de párrafos, títulos, listas, bloques de código, tablas, imágenes con
 * pie de figura y un convertidor de Markdown sencillo, todos con el estilo común del documento.
 * generar_documento.js los usa para escribir el contenido. Las medidas siguen las unidades de docx:
 * DXA (veinteavos de punto, 1440 por pulgada) para anchos y sangrías, y medios puntos para `size`.
 */
const fs = require("fs");
const path = require("path");
const D = require("docx");
const {
  Paragraph, TextRun, HeadingLevel, ImageRun, Table, TableRow, TableCell, WidthType, ShadingType,
  AlignmentType, BorderStyle, LevelFormat, PageBreak, TableLayoutType,
} = D;

/** Fuente del texto, fuente monoespaciada para código y color de acento (verde de la paleta ABCO). */
const FONT = "Arial";
const MONO = "Courier New";
const ACCENT = "2E7D1F";
/** Ancho útil de la página: carta (8.5 in) menos márgenes de 1 in = 6.5 in = 9360 DXA. */
const CONTENT_WIDTH = 9360; // DXA: carta con márgenes de 1 in

/** Contadores de figuras, tablas y listas numeradas, para numerar los pies y las listas en orden. */
let figure = 0, table = 0, listCounter = 0;
/**
 * Configuraciones de numeración de docx. Arranca con la de viñetas (dos niveles); `steps()` agrega una
 * por lista numerada. Es mutable: el Document debe crearse después de generar todo el contenido.
 */
const numberingConfigs = [{
  reference: "bullets",
  levels: [
    { level: 0, format: LevelFormat.BULLET, text: "•", alignment: AlignmentType.LEFT, style: { paragraph: { indent: { left: 540, hanging: 270 } } } },
    { level: 1, format: LevelFormat.BULLET, text: "◦", alignment: AlignmentType.LEFT, style: { paragraph: { indent: { left: 1080, hanging: 270 } } } },
  ],
}];

/**
 * Convierte texto con marcado mínimo (**negrita**, *cursiva* y `código`) en TextRun de docx.
 * No admite anidar marcas.
 * @param {string} text Texto con marcado.
 * @param {object} [base={}] Propiedades de TextRun que se aplican a todos los fragmentos (tamaño, color…).
 * @returns {import("docx").TextRun[]} Fragmentos del párrafo.
 */
function runs(text, base = {}) {
  const out = [];
  const re = /(\*\*[^*]+\*\*|`[^`]+`|\*[^*]+\*)/g;
  let last = 0, m;
  while ((m = re.exec(text))) {
    if (m.index > last) out.push(new TextRun({ text: text.slice(last, m.index), ...base }));
    const t = m[0];
    if (t.startsWith("**")) out.push(new TextRun({ text: t.slice(2, -2), bold: true, ...base }));
    else if (t.startsWith("`")) out.push(new TextRun({ text: t.slice(1, -1), font: MONO, size: 19, color: "1F5C14", ...base }));
    else out.push(new TextRun({ text: t.slice(1, -1), italics: true, ...base }));
    last = m.index + t.length;
  }
  if (last < text.length) out.push(new TextRun({ text: text.slice(last), ...base }));
  return out;
}

/**
 * Párrafo de texto (justificado por defecto) con marcado mínimo.
 * @param {string} text Texto con marcado (ver `runs`).
 * @param {{run?: object, align?: string, para?: object}} [opts] Propiedades para los TextRun, alineación
 *   y propiedades extra del Paragraph.
 * @returns {import("docx").Paragraph}
 */
const P = (text, opts = {}) => new Paragraph({ children: runs(text, opts.run), spacing: { after: 120, line: 276 }, alignment: opts.align ?? AlignmentType.JUSTIFIED, ...opts.para });
/** Títulos: H1 abre página nueva (capítulo); H1keep es un H1 sin salto; H2 y H3 son subtítulos. */
const H1 = (t) => new Paragraph({ heading: HeadingLevel.HEADING_1, children: [new TextRun(t)], pageBreakBefore: true });
const H1keep = (t) => new Paragraph({ heading: HeadingLevel.HEADING_1, children: [new TextRun(t)] });
const H2 = (t) => new Paragraph({ heading: HeadingLevel.HEADING_2, children: [new TextRun(t)] });
const H3 = (t) => new Paragraph({ heading: HeadingLevel.HEADING_3, children: [new TextRun(t)] });
/** Párrafo con un salto de página. */
const pageBreak = () => new Paragraph({ children: [new PageBreak()] });

/**
 * Lista con viñetas.
 * @param {string[]} items Elementos (con marcado mínimo).
 * @param {number} [level=0] Nivel de sangría (0 o 1).
 * @returns {import("docx").Paragraph[]}
 */
function bullets(items, level = 0) {
  return items.map((t) => new Paragraph({ numbering: { reference: "bullets", level }, children: runs(t), spacing: { after: 60 } }));
}

/**
 * Lista numerada que reinicia en 1. En docx la numeración continúa mientras se use la misma
 * referencia, así que cada lista registra su propia configuración en `numberingConfigs`.
 * @param {string[]} items Pasos (con marcado mínimo).
 * @returns {import("docx").Paragraph[]}
 */
function steps(items) {
  const ref = `steps-${++listCounter}`;
  numberingConfigs.push({
    reference: ref,
    levels: [{ level: 0, format: LevelFormat.DECIMAL, text: "%1.", alignment: AlignmentType.LEFT, style: { paragraph: { indent: { left: 540, hanging: 360 } } } }],
  });
  return items.map((t) => new Paragraph({ numbering: { reference: ref, level: 0 }, children: runs(t), spacing: { after: 60 } }));
}

/**
 * Bloque de código o seudocódigo: fondo gris, fuente monoespaciada, una línea por párrafo.
 * Solo la primera y la última línea llevan borde (arriba y abajo), para que el bloque se vea como
 * un recuadro; las líneas vacías se escriben como un espacio para que conserven su altura.
 * @param {string} text Código; se ignora un salto de línea final.
 * @returns {import("docx").Paragraph[]}
 */
function code(text) {
  const lines = text.replace(/\n$/, "").split("\n");
  return lines.map((line, i) => new Paragraph({
    children: [new TextRun({ text: line.length ? line : " ", font: MONO, size: 17 })],
    shading: { type: ShadingType.CLEAR, color: "auto", fill: "F2F5F3" },
    spacing: { before: i === 0 ? 80 : 0, after: i === lines.length - 1 ? 160 : 0, line: 240 },
    indent: { left: 120, right: 120 },
    border: i === 0 ? { top: { style: BorderStyle.SINGLE, size: 4, color: "D3DADE", space: 4 } }
      : i === lines.length - 1 ? { bottom: { style: BorderStyle.SINGLE, size: 4, color: "D3DADE", space: 4 } } : undefined,
  }));
}

/**
 * Pie de figura o de tabla con numeración automática ("Figura 3. …", "Tabla 2. …").
 * @param {"Figura"|"Tabla"} prefix Tipo de pie; cada uno lleva su propio contador.
 * @param {string} text Texto del pie (con marcado mínimo).
 * @returns {import("docx").Paragraph}
 */
function caption(prefix, text) {
  const n = prefix === "Figura" ? ++figure : ++table;
  return new Paragraph({
    children: [new TextRun({ text: `${prefix} ${n}. `, bold: true, size: 18, color: "555555" }), ...runs(text, { size: 18, color: "555555" })],
    alignment: AlignmentType.CENTER, spacing: { before: 60, after: 200 },
  });
}

/**
 * Tabla de ancho completo con encabezado sombreado que se repite en cada página.
 * @param {string[]} headers Encabezados de columna.
 * @param {Array<Array<string|number>>} rows Filas; un "\n" dentro de una celda la parte en párrafos.
 * @param {number[]} [widths] Ancho de cada columna como fracción (suman 1); por defecto, iguales.
 * @param {string} [title] Si se da, la tabla lleva antes un pie "Tabla n.".
 * @returns {Array<import("docx").Paragraph|import("docx").Table>} Pie (opcional), tabla y un párrafo
 *   vacío de separación.
 */
function tbl(headers, rows, widths, title) {
  const w = (widths ?? headers.map(() => 1 / headers.length)).map((f) => Math.round(f * CONTENT_WIDTH));
  // El redondeo puede dejar unos DXA sueltos: se suman a la última columna para cuadrar el ancho.
  w[w.length - 1] += CONTENT_WIDTH - w.reduce((a, b) => a + b, 0);
  const cell = (t, i, head) => new TableCell({
    width: { size: w[i], type: WidthType.DXA },
    shading: head ? { type: ShadingType.CLEAR, color: "auto", fill: "DDEFD6" } : undefined,
    margins: { top: 50, bottom: 50, left: 90, right: 90 },
    children: String(t).split("\n").map((line) => new Paragraph({ children: runs(line, { size: 18, bold: head || undefined }), spacing: { after: 0 } })),
  });
  const out = [];
  if (title) out.push(caption("Tabla", title));
  out.push(new Table({
    width: { size: CONTENT_WIDTH, type: WidthType.DXA },
    columnWidths: w,
    layout: TableLayoutType.FIXED,
    rows: [
      new TableRow({ tableHeader: true, children: headers.map((h, i) => cell(h, i, true)) }),
      ...rows.map((r) => new TableRow({ cantSplit: true, children: r.map((c, i) => cell(c, i, false)) })),
    ],
  }));
  out.push(new Paragraph({ spacing: { after: 120 }, children: [] }));
  return out;
}

/**
 * Lee las dimensiones de una imagen PNG o JPEG sin dependencias externas.
 * - PNG (empieza con 0x89): ancho y alto están en el bloque IHDR, en los bytes 16 y 20 (big-endian).
 * - JPEG: se recorren los segmentos (marcador 0xFF xx + longitud) hasta un SOF0–SOF3, que guarda
 *   el alto y el ancho en los bytes 5 y 7 del segmento.
 * @param {string} file Ruta de la imagen.
 * @returns {{w: number, h: number, type: "png"|"jpg", data: Buffer}} Dimensiones en píxeles, tipo y bytes.
 * @throws {Error} Si no es PNG ni un JPEG con segmento SOF reconocible.
 */
function imageSize(file) {
  const b = fs.readFileSync(file);
  if (b[0] === 0x89) return { w: b.readUInt32BE(16), h: b.readUInt32BE(20), type: "png", data: b };
  let i = 2; // JPEG: buscar SOF0/SOF2
  while (i < b.length) {
    if (b[i] !== 0xff) { i++; continue; }
    const marker = b[i + 1], len = b.readUInt16BE(i + 2);
    if (marker >= 0xc0 && marker <= 0xc3) return { h: b.readUInt16BE(i + 5), w: b.readUInt16BE(i + 7), type: "jpg", data: b };
    i += 2 + len;
  }
  throw new Error("Tamaño de imagen desconocido: " + file);
}

/**
 * Imagen centrada con pie de figura. Se escala para caber en maxW × maxH pulgadas (a 96 px por
 * pulgada) sin ampliarla más de 1.6 veces. Si el archivo no existe, en su lugar se escribe un aviso
 * en cursiva, para que el documento se genere aunque falte alguna captura.
 * @param {string} file Ruta de la imagen PNG o JPEG.
 * @param {string} text Texto del pie de figura.
 * @param {{maxW?: number, maxH?: number}} [opts] Tamaño máximo en pulgadas.
 * @returns {import("docx").Paragraph[]}
 */
function img(file, text, { maxW = 6.3, maxH = 7.6 } = {}) {
  if (!fs.existsSync(file)) return [P(`*[Falta la imagen ${path.basename(file)}]*`)];
  const { w, h, type, data } = imageSize(file);
  const scale = Math.min((maxW * 96) / w, (maxH * 96) / h, 1.6);
  return [
    new Paragraph({
      alignment: AlignmentType.CENTER, spacing: { before: 120, after: 0 }, keepNext: true,
      children: [new ImageRun({ type, data, transformation: { width: Math.round(w * scale), height: Math.round(h * scale) } })],
    }),
    caption("Figura", text),
  ];
}

/**
 * Convierte el Markdown sencillo de docs/metricas.md en párrafos y tablas: "# " (título, que se omite
 * por defecto), "## " (sección, como H3 y sin su numeración), tablas con "|" y párrafos.
 * @param {string} md Texto Markdown.
 * @param {{skipTitle?: boolean}} [opts] skipTitle = false conserva el título "# " como H2.
 * @returns {Array<import("docx").Paragraph|import("docx").Table>}
 */
function markdown(md, { skipTitle = true } = {}) {
  const out = [];
  const lines = md.split("\n");
  for (let i = 0; i < lines.length; i++) {
    const l = lines[i];
    if (!l.trim()) continue;
    if (l.startsWith("# ")) { if (!skipTitle) out.push(H2(l.slice(2))); continue; }
    if (l.startsWith("## ")) { out.push(H3(l.slice(3).replace(/^\d+b?\.\s*/, ""))); continue; }
    if (l.startsWith("|")) {
      // Las líneas seguidas que empiezan con "|" forman una tabla: la primera es el encabezado y la
      // segunda, el separador "|---|", que se descarta. Un "\|" dentro de una celda es una barra literal.
      const rows = [];
      while (i < lines.length && lines[i].startsWith("|")) { rows.push(lines[i]); i++; }
      i--;
      const split = (r) => r.replace(/\\\|/g, "\u0000").split("|").slice(1, -1).map((c) => c.trim().replace(/\u0000/g, "|"));
      const head = split(rows[0]);
      const body = rows.slice(2).map(split);
      out.push(...tbl(head, body));
      continue;
    }
    out.push(P(l));
  }
  return out;
}

// D es el módulo docx completo, para que generar_documento.js use la misma instancia de la librería.
module.exports = {
  D, FONT, MONO, ACCENT, CONTENT_WIDTH, numberingConfigs,
  runs, P, H1, H1keep, H2, H3, pageBreak, bullets, steps, code, caption, tbl, img, markdown,
};
