// Builds "Documentacion_Sistema_SignalTrace.docx" from docs/*.md (single source of truth), the Graphviz diagrams
// (docs/diagramas/png) and the screenshots (docs/capturas).
//   node docs/word/generar_documento.js [salida.docx]
const fs = require("fs");
const path = require("path");
const L = require("./lib");
const {
  Document, Packer, Paragraph, TextRun, AlignmentType, Header, Footer, PageNumber, TableOfContents, ImageRun,
  HeadingLevel, BorderStyle,
} = L.D;

const DOCS = path.resolve(__dirname, "..");
const OUT = process.argv[2] ?? path.join(__dirname, "Documentacion_Sistema_SignalTrace.docx");
const CHAPTERS = [
  "00-introduccion.md", "01-arquitectura.md", "02-algoritmos.md", "03-escenarios.md", "04-android-y-api.md",
  "05-criptoanalisis-ycm1.md", "06-matematicas.md", "07-manual.md", "08-limitaciones.md", "09-pruebas.md", "10-conclusiones.md",
];

const center = (text, opts = {}) => new Paragraph({
  alignment: AlignmentType.CENTER, spacing: { after: opts.after ?? 120 },
  children: [new TextRun({ text, size: opts.size ?? 24, bold: opts.bold, color: opts.color, font: L.FONT })],
});

function cover() {
  const logo = path.join(DOCS, "..", "src/SignalTrace.Server/wwwroot/icons/icon-512.png");
  return [
    center("INSTITUTO POLITÉCNICO NACIONAL", { size: 32, bold: true, after: 80 }),
    center("Unidad Profesional Interdisciplinaria de Ingeniería y Ciencias Sociales y Administrativas (UPIICSA)", { size: 22, after: 400 }),
    center("Virología y Criptografía · Grupo 6NV68", { size: 26, after: 600 }),
    new Paragraph({
      alignment: AlignmentType.CENTER, spacing: { after: 300 },
      children: [new ImageRun({ type: "png", data: fs.readFileSync(logo), transformation: { width: 150, height: 150 } })],
    }),
    center("SignalTrace", { size: 64, bold: true, color: L.ACCENT, after: 120 }),
    center("Sistema de rastreo de señales con telemetría cifrada", { size: 30, after: 120 }),
    center("Proyecto del segundo parcial: «Inventar un algoritmo de encriptación»", { size: 24, after: 120 }),
    center("Yael Crypto March1 (algoritmo propio) + AES-256-GCM · ECDH P-256 + HKDF · ECDSA · PBKDF2 · HMAC-SHA256 · ChaCha20-Poly1305", { size: 20, color: "555555", after: 900 }),
    center("Integrantes: ____________________________________________", { size: 24, after: 200 }),
    center("____________________________________________", { size: 24, after: 200 }),
    center("Profesor(a): ____________________________________________", { size: 24, after: 600 }),
    center("Ciudad de México, octubre de 2026", { size: 24 }),
  ];
}

function toc() {
  return [
    new Paragraph({ heading: HeadingLevel.HEADING_1, pageBreakBefore: true, children: [new TextRun("Índice")] }),
    new TableOfContents("Índice", { hyperlink: true, headingStyleRange: "1-2" }),
    new Paragraph({
      spacing: { before: 200 },
      children: [new TextRun({ text: "Si el índice aparece vacío, en Word: clic derecho sobre él → «Actualizar campo».", italics: true, size: 18, color: "777777" })],
    }),
  ];
}

const body = [];
for (const file of CHAPTERS) {
  const p = path.join(DOCS, file);
  if (!fs.existsSync(p)) { console.warn("Falta", file); continue; }
  body.push(...L.markdown(fs.readFileSync(p, "utf8"), DOCS));
}

const doc = new Document({
  creator: "SignalTrace",
  title: "SignalTrace — Documentación del sistema",
  description: "Sistema de rastreo de señales con telemetría cifrada (Virología y Criptografía, 6NV68)",
  features: { updateFields: true },
  styles: {
    default: { document: { run: { font: L.FONT, size: 21 } } },
    paragraphStyles: [
      { id: "Heading1", name: "Heading 1", basedOn: "Normal", next: "Normal", quickFormat: true,
        run: { size: 34, bold: true, color: L.ACCENT, font: L.FONT }, paragraph: { spacing: { before: 120, after: 240 }, outlineLevel: 0,
          border: { bottom: { style: BorderStyle.SINGLE, size: 8, color: L.ACCENT, space: 4 } } } },
      { id: "Heading2", name: "Heading 2", basedOn: "Normal", next: "Normal", quickFormat: true,
        run: { size: 27, bold: true, color: "1D2B3A", font: L.FONT }, paragraph: { spacing: { before: 300, after: 140 }, outlineLevel: 1, keepNext: true } },
      { id: "Heading3", name: "Heading 3", basedOn: "Normal", next: "Normal", quickFormat: true,
        run: { size: 23, bold: true, color: "33475B", font: L.FONT }, paragraph: { spacing: { before: 200, after: 100 }, outlineLevel: 2, keepNext: true } },
    ],
  },
  numbering: { config: L.numberingConfigs },
  sections: [
    {
      properties: { page: { size: { width: 12240, height: 15840 }, margin: { top: 1440, right: 1440, bottom: 1440, left: 1440 } } },
      children: cover(),
    },
    {
      properties: { page: { size: { width: 12240, height: 15840 }, margin: { top: 1440, right: 1440, bottom: 1440, left: 1440 } } },
      headers: {
        default: new Header({ children: [new Paragraph({ alignment: AlignmentType.RIGHT,
          children: [new TextRun({ text: "SignalTrace · Virología y Criptografía · 6NV68", size: 16, color: "777777" })] })] }),
      },
      footers: {
        default: new Footer({ children: [new Paragraph({ alignment: AlignmentType.CENTER,
          children: [new TextRun({ children: ["Página ", PageNumber.CURRENT, " de ", PageNumber.TOTAL_PAGES], size: 16, color: "777777" })] })] }),
      },
      children: [...toc(), ...body],
    },
  ],
});

Packer.toBuffer(doc).then((buf) => {
  fs.writeFileSync(OUT, buf);
  console.log(`Documento generado: ${OUT} (${(buf.length / 1024).toFixed(0)} KB)`);
});
