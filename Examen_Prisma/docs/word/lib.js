// Utilidades para construir el documento Word con la librería docx.
const fs = require("fs");
const path = require("path");
const D = require("docx");
const {
  Paragraph, TextRun, HeadingLevel, ImageRun, Table, TableRow, TableCell, WidthType, ShadingType,
  AlignmentType, BorderStyle, LevelFormat, PageBreak, TableLayoutType,
} = D;

const FONT = "Arial";
const MONO = "Courier New";
const ACCENT = "4B3FD6";
const CONTENT_WIDTH = 9360; // DXA: carta con márgenes de 1 in

let figure = 0, table = 0, listCounter = 0;
const numberingConfigs = [{
  reference: "bullets",
  levels: [
    { level: 0, format: LevelFormat.BULLET, text: "•", alignment: AlignmentType.LEFT, style: { paragraph: { indent: { left: 540, hanging: 270 } } } },
    { level: 1, format: LevelFormat.BULLET, text: "◦", alignment: AlignmentType.LEFT, style: { paragraph: { indent: { left: 1080, hanging: 270 } } } },
  ],
}];

/** Texto con marcado mínimo: **negrita**, *cursiva*, `código`. */
function runs(text, base = {}) {
  const out = [];
  const re = /(\*\*[^*]+\*\*|`[^`]+`|\*[^*]+\*)/g;
  let last = 0, m;
  while ((m = re.exec(text))) {
    if (m.index > last) out.push(new TextRun({ text: text.slice(last, m.index), ...base }));
    const t = m[0];
    if (t.startsWith("**")) out.push(new TextRun({ text: t.slice(2, -2), bold: true, ...base }));
    else if (t.startsWith("`")) out.push(new TextRun({ text: t.slice(1, -1), font: MONO, size: 19, color: "3B2FB0", ...base }));
    else out.push(new TextRun({ text: t.slice(1, -1), italics: true, ...base }));
    last = m.index + t.length;
  }
  if (last < text.length) out.push(new TextRun({ text: text.slice(last), ...base }));
  return out;
}

const P = (text, opts = {}) => new Paragraph({ children: runs(text, opts.run), spacing: { after: 120, line: 276 }, alignment: opts.align ?? AlignmentType.JUSTIFIED, ...opts.para });
const H1 = (t) => new Paragraph({ heading: HeadingLevel.HEADING_1, children: [new TextRun(t)], pageBreakBefore: true });
const H1keep = (t) => new Paragraph({ heading: HeadingLevel.HEADING_1, children: [new TextRun(t)] });
const H2 = (t) => new Paragraph({ heading: HeadingLevel.HEADING_2, children: [new TextRun(t)] });
const H3 = (t) => new Paragraph({ heading: HeadingLevel.HEADING_3, children: [new TextRun(t)] });
const pageBreak = () => new Paragraph({ children: [new PageBreak()] });

function bullets(items, level = 0) {
  return items.map((t) => new Paragraph({ numbering: { reference: "bullets", level }, children: runs(t), spacing: { after: 60 } }));
}

/** Lista numerada que reinicia en 1. */
function steps(items) {
  const ref = `steps-${++listCounter}`;
  numberingConfigs.push({
    reference: ref,
    levels: [{ level: 0, format: LevelFormat.DECIMAL, text: "%1.", alignment: AlignmentType.LEFT, style: { paragraph: { indent: { left: 540, hanging: 360 } } } }],
  });
  return items.map((t) => new Paragraph({ numbering: { reference: ref, level: 0 }, children: runs(t), spacing: { after: 60 } }));
}

/** Bloque de código o seudocódigo: fondo gris, fuente monoespaciada, una línea por párrafo. */
function code(text) {
  const lines = text.replace(/\n$/, "").split("\n");
  return lines.map((line, i) => new Paragraph({
    children: [new TextRun({ text: line.length ? line : " ", font: MONO, size: 17 })],
    shading: { type: ShadingType.CLEAR, color: "auto", fill: "F3F2FB" },
    spacing: { before: i === 0 ? 80 : 0, after: i === lines.length - 1 ? 160 : 0, line: 240 },
    indent: { left: 120, right: 120 },
    border: i === 0 ? { top: { style: BorderStyle.SINGLE, size: 4, color: "DDDBEE", space: 4 } }
      : i === lines.length - 1 ? { bottom: { style: BorderStyle.SINGLE, size: 4, color: "DDDBEE", space: 4 } } : undefined,
  }));
}

function caption(prefix, text) {
  const n = prefix === "Figura" ? ++figure : ++table;
  return new Paragraph({
    children: [new TextRun({ text: `${prefix} ${n}. `, bold: true, size: 18, color: "555555" }), ...runs(text, { size: 18, color: "555555" })],
    alignment: AlignmentType.CENTER, spacing: { before: 60, after: 200 },
  });
}

/** Tabla con encabezado sombreado. widths en fracciones (suman 1). */
function tbl(headers, rows, widths, title) {
  const w = (widths ?? headers.map(() => 1 / headers.length)).map((f) => Math.round(f * CONTENT_WIDTH));
  w[w.length - 1] += CONTENT_WIDTH - w.reduce((a, b) => a + b, 0);
  const cell = (t, i, head) => new TableCell({
    width: { size: w[i], type: WidthType.DXA },
    shading: head ? { type: ShadingType.CLEAR, color: "auto", fill: "E7E5FA" } : undefined,
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

/** Imagen centrada con pie de figura. maxW/maxH en pulgadas. */
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

/** Convierte el Markdown sencillo de docs/metricas.md (##, párrafos, tablas) en párrafos y tablas. */
function markdown(md, { skipTitle = true } = {}) {
  const out = [];
  const lines = md.split("\n");
  for (let i = 0; i < lines.length; i++) {
    const l = lines[i];
    if (!l.trim()) continue;
    if (l.startsWith("# ")) { if (!skipTitle) out.push(H2(l.slice(2))); continue; }
    if (l.startsWith("## ")) { out.push(H3(l.slice(3).replace(/^\d+b?\.\s*/, ""))); continue; }
    if (l.startsWith("|")) {
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

module.exports = {
  D, FONT, MONO, ACCENT, CONTENT_WIDTH, numberingConfigs,
  runs, P, H1, H1keep, H2, H3, pageBreak, bullets, steps, code, caption, tbl, img, markdown,
};
