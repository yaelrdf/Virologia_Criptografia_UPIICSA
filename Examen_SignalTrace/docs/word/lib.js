// Helpers to build the Word document with the docx library (adapted from the previous exam's generator).
const fs = require("fs");
const path = require("path");
const D = require("docx");
const {
  Paragraph, TextRun, HeadingLevel, ImageRun, Table, TableRow, TableCell, WidthType, ShadingType,
  AlignmentType, BorderStyle, LevelFormat, PageBreak, TableLayoutType,
} = D;

const FONT = "Arial";
const MONO = "Courier New";
const ACCENT = "0F6CBD";
const HEAD_FILL = "DCEAF7";
const CODE_FILL = "F2F5F9";
const CONTENT_WIDTH = 9360; // DXA: Letter with 1 in margins

let figure = 0, table = 0, listCounter = 0;
const numberingConfigs = [{
  reference: "bullets",
  levels: [
    { level: 0, format: LevelFormat.BULLET, text: "•", alignment: AlignmentType.LEFT, style: { paragraph: { indent: { left: 540, hanging: 270 } } } },
    { level: 1, format: LevelFormat.BULLET, text: "◦", alignment: AlignmentType.LEFT, style: { paragraph: { indent: { left: 1080, hanging: 270 } } } },
    { level: 2, format: LevelFormat.BULLET, text: "▪", alignment: AlignmentType.LEFT, style: { paragraph: { indent: { left: 1620, hanging: 270 } } } },
  ],
}];

/** Inline markup: **bold**, *italic*, `code`. */
function runs(text, base = {}) {
  const out = [];
  const re = /(\*\*[^*]+\*\*|`[^`]+`|\*[^*\s][^*]*\*)/g;
  let last = 0, m;
  while ((m = re.exec(text))) {
    if (m.index > last) out.push(new TextRun({ text: text.slice(last, m.index), ...base }));
    const t = m[0];
    if (t.startsWith("**")) out.push(...runs(t.slice(2, -2), { ...base, bold: true }));
    else if (t.startsWith("`")) out.push(new TextRun({ text: t.slice(1, -1), font: MONO, size: 18, color: "0B4F8A", ...base }));
    else out.push(new TextRun({ text: t.slice(1, -1), italics: true, ...base }));
    last = m.index + t.length;
  }
  if (last < text.length) out.push(new TextRun({ text: text.slice(last), ...base }));
  return out;
}

const P = (text, opts = {}) => new Paragraph({ children: runs(text, opts.run), spacing: { after: 120, line: 276 }, alignment: opts.align ?? AlignmentType.JUSTIFIED, ...opts.para });
const H1 = (t) => new Paragraph({ heading: HeadingLevel.HEADING_1, children: [new TextRun(t)], pageBreakBefore: true });
const H2 = (t) => new Paragraph({ heading: HeadingLevel.HEADING_2, children: [new TextRun(t)] });
const H3 = (t) => new Paragraph({ heading: HeadingLevel.HEADING_3, children: [new TextRun(t)] });
const pageBreak = () => new Paragraph({ children: [new PageBreak()] });

function bullets(items, level = 0) {
  return items.map((t) => new Paragraph({ numbering: { reference: "bullets", level }, children: runs(t), spacing: { after: 60 } }));
}

function newNumbering() {
  const ref = `steps-${++listCounter}`;
  numberingConfigs.push({
    reference: ref,
    levels: [0, 1, 2].map((level) => ({
      level, format: level === 1 ? LevelFormat.LOWER_LETTER : LevelFormat.DECIMAL, text: `%${level + 1}.`, alignment: AlignmentType.LEFT,
      style: { paragraph: { indent: { left: 540 + level * 540, hanging: 360 } } },
    })),
  });
  return ref;
}

function steps(items) {
  const ref = newNumbering();
  return items.map((t) => new Paragraph({ numbering: { reference: ref, level: 0 }, children: runs(t), spacing: { after: 60 } }));
}

/** Code or pseudocode block: shaded, monospace, one paragraph per line. */
function code(text) {
  const lines = text.replace(/\n$/, "").split("\n");
  return lines.map((line, i) => new Paragraph({
    children: [new TextRun({ text: line.length ? line : " ", font: MONO, size: 16 })],
    shading: { type: ShadingType.CLEAR, color: "auto", fill: CODE_FILL },
    spacing: { before: i === 0 ? 80 : 0, after: i === lines.length - 1 ? 160 : 0, line: 240 },
    indent: { left: 120, right: 120 },
    keepLines: true,
    keepNext: i < lines.length - 1 && lines.length <= 30,
    border: i === 0 ? { top: { style: BorderStyle.SINGLE, size: 4, color: "C9D3DF", space: 4 } }
      : i === lines.length - 1 ? { bottom: { style: BorderStyle.SINGLE, size: 4, color: "C9D3DF", space: 4 } } : undefined,
  }));
}

/** Highlighted note (Markdown "> ..."). */
function note(text) {
  return new Paragraph({
    children: runs(text), spacing: { before: 80, after: 160, line: 264 }, alignment: AlignmentType.LEFT,
    shading: { type: ShadingType.CLEAR, color: "auto", fill: "FFF6E5" },
    border: { left: { style: BorderStyle.SINGLE, size: 18, color: "D29922", space: 8 } },
    indent: { left: 160, right: 120 },
  });
}

function caption(prefix, text) {
  const n = prefix === "Figura" ? ++figure : ++table;
  return new Paragraph({
    children: [new TextRun({ text: `${prefix} ${n}. `, bold: true, size: 18, color: "555555" }), ...runs(text, { size: 18, color: "555555" })],
    alignment: AlignmentType.CENTER, spacing: { before: 60, after: 200 }, keepLines: true,
  });
}

/** Table with a shaded header. widths in fractions (sum 1). */
function tbl(headers, rows, widths, title) {
  const w = (widths ?? autoWidths(headers, rows)).map((f) => Math.round(f * CONTENT_WIDTH));
  w[w.length - 1] += CONTENT_WIDTH - w.reduce((a, b) => a + b, 0);
  const cell = (t, i, head) => new TableCell({
    width: { size: w[i], type: WidthType.DXA },
    shading: head ? { type: ShadingType.CLEAR, color: "auto", fill: HEAD_FILL } : undefined,
    margins: { top: 50, bottom: 50, left: 90, right: 90 },
    children: String(t ?? "").split("<br>").map((line) => new Paragraph({ children: runs(line, { size: 17, bold: head || undefined }), spacing: { after: 0 }, alignment: AlignmentType.LEFT })),
  });
  const out = [];
  if (title) out.push(caption("Tabla", title));
  out.push(new Table({
    width: { size: CONTENT_WIDTH, type: WidthType.DXA },
    columnWidths: w,
    layout: TableLayoutType.FIXED,
    rows: [
      new TableRow({ tableHeader: true, children: headers.map((h, i) => cell(h, i, true)) }),
      ...rows.map((r) => new TableRow({ cantSplit: true, children: headers.map((_, i) => cell(r[i] ?? "", i, false)) })),
    ],
  }));
  out.push(new Paragraph({ spacing: { after: 120 }, children: [] }));
  return out;
}

/** Column widths proportional to the longest text in each column (clamped). */
function autoWidths(headers, rows) {
  const len = headers.map((h, i) => Math.max(h.length, ...rows.map((r) => String(r[i] ?? "").length)));
  const clamped = len.map((l) => Math.min(Math.max(l, 9), 70));
  const sum = clamped.reduce((a, b) => a + b, 0);
  return clamped.map((l) => l / sum);
}

function imageSize(file) {
  const b = fs.readFileSync(file);
  if (b[0] === 0x89) return { w: b.readUInt32BE(16), h: b.readUInt32BE(20), type: "png", data: b };
  let i = 2; // JPEG: find SOF0/SOF2
  while (i < b.length) {
    if (b[i] !== 0xff) { i++; continue; }
    const marker = b[i + 1], len = b.readUInt16BE(i + 2);
    if (marker >= 0xc0 && marker <= 0xc3) return { h: b.readUInt16BE(i + 5), w: b.readUInt16BE(i + 7), type: "jpg", data: b };
    i += 2 + len;
  }
  throw new Error("Unknown image size: " + file);
}

/** Centered image with a figure caption. maxW/maxH in inches. Pixel density hint `dpi` for diagrams. */
function img(file, text, { maxW = 6.3, maxH = 7.4, dpi = 96 } = {}) {
  if (!fs.existsSync(file)) return [P(`*[Falta la imagen ${path.basename(file)}]*`)];
  const { w, h, type, data } = imageSize(file);
  const scale = Math.min((maxW * 96) / w, (maxH * 96) / h, 96 / dpi * 1.0);
  return [
    new Paragraph({
      alignment: AlignmentType.CENTER, spacing: { before: 120, after: 0 }, keepNext: true,
      children: [new ImageRun({ type, data, transformation: { width: Math.round(w * scale), height: Math.round(h * scale) } })],
    }),
    caption("Figura", text),
  ];
}

/**
 * Markdown subset used in docs/*.md → docx elements:
 * # (chapter, new page) ## ### headings, paragraphs, "- " bullets (nested by 2 spaces), "1. " numbered lists
 * (nested by 3 spaces), tables, ``` code ```, ![caption](relative/path.png), "> " notes.
 */
function markdown(md, baseDir, { chapter = true } = {}) {
  const out = [];
  const lines = md.replace(/\r/g, "").split("\n");
  let numRef = null;
  for (let i = 0; i < lines.length; i++) {
    const l = lines[i];
    if (!l.trim()) { if (!/^\s*\d+\.\s/.test(lines[i + 1] ?? "") && !/^\s+[-*]\s/.test(lines[i + 1] ?? "")) numRef = null; continue; }
    if (l.startsWith("```")) {
      const buf = [];
      for (i++; i < lines.length && !lines[i].startsWith("```"); i++) buf.push(lines[i]);
      out.push(...code(buf.join("\n")));
      continue;
    }
    if (l.startsWith("# ")) { out.push(chapter ? H1(l.slice(2)) : H2(l.slice(2))); continue; }
    if (l.startsWith("## ")) { out.push(H2(l.slice(3))); continue; }
    if (l.startsWith("### ")) { out.push(H3(l.slice(4))); continue; }
    const im = l.match(/^!\[(.*)\]\((.*)\)\s*$/);
    if (im) {
      const file = path.resolve(baseDir, im[2]);
      const isDiagram = im[2].includes("diagramas/");
      const isPhone = im[2].includes("movil");
      out.push(...img(file, im[1], isDiagram ? { dpi: 150, maxH: 8.2 } : isPhone ? { maxW: 2.8, maxH: 6 } : { dpi: 96 }));
      continue;
    }
    if (l.startsWith("> ")) { out.push(note(l.slice(2))); continue; }
    if (l.startsWith("|")) {
      const rows = [];
      while (i < lines.length && lines[i].startsWith("|")) { rows.push(lines[i]); i++; }
      i--;
      const split = (r) => r.replace(/\\\|/g, "\u0000").split("|").slice(1, -1).map((c) => c.trim().replace(/\u0000/g, "|"));
      out.push(...tbl(split(rows[0]), rows.slice(2).map(split)));
      continue;
    }
    const bl = l.match(/^(\s*)[-*]\s+(.*)$/);
    if (bl) {
      out.push(new Paragraph({ numbering: { reference: "bullets", level: Math.min(2, Math.floor(bl[1].length / 2)) }, children: runs(bl[2]), spacing: { after: 60 } }));
      continue;
    }
    const nl = l.match(/^(\s*)\d+\.\s+(.*)$/);
    if (nl) {
      const level = Math.min(2, Math.floor(nl[1].length / 3));
      if (!numRef || (level === 0 && /^1\.\s/.test(l.trim()) && !nl[1])) numRef = level === 0 ? newNumbering() : numRef ?? newNumbering();
      out.push(new Paragraph({ numbering: { reference: numRef, level }, children: runs(nl[2]), spacing: { after: 60 } }));
      continue;
    }
    // A lead-in paragraph ("Pseudocódigo:", "Pasos:") stays on the same page as what follows.
    out.push(l.trim().replace(/\*+$/, "").endsWith(":") ? P(l, { para: { keepNext: true } }) : P(l));
  }
  return out;
}

module.exports = {
  D, FONT, MONO, ACCENT, CONTENT_WIDTH, numberingConfigs,
  runs, P, H1, H2, H3, pageBreak, bullets, steps, code, note, caption, tbl, img, markdown,
};
