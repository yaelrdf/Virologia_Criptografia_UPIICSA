// Ayudas de Chart.js (la variable global Chart viene de lib/chart/chart.umd.min.js). El eje x es lineal en ms Unix y
// se formatea como hora, así no hace falta un adaptador de fechas. Los colores siguen el tema de la página y se
// vuelven a aplicar con el evento "st-theme".
/** Todas las gráficas creadas, para recolorearlas y destruirlas juntas. */
const charts = new Set();

/** Paleta de colores de las series. */
export const SERIES = { blue: "#2f7fd8", orange: "#e8833a", green: "#2ea043", purple: "#8957e5", red: "#e5534b", teal: "#1b998b" };

/** Lee una variable CSS del documento. */
function css(name) { return getComputedStyle(document.documentElement).getPropertyValue(name).trim(); }

/** Colores del texto y de la cuadrícula según el tema actual. */
function themeOptions() {
  const text = css("--muted"), grid = css("--border");
  return { text, grid };
}

/** Formatea un instante (ms) como hora de 24 h para las marcas del eje x. */
const timeTick = (v) => new Date(v).toLocaleTimeString("es-MX", { hour12: false });
/** Si la pantalla mide menos de 480 px de ancho. */
const narrow = () => matchMedia("(max-width: 479px)").matches;

/** Gráficas a tamaño de teléfono: menos marcas, leyenda más pequeña y sin títulos de eje. Se vuelve a aplicar cuando cambia el ancho. */
function fit(chart) {
  const n = narrow();
  const x = chart.options.scales.x, y = chart.options.scales.y;
  x.ticks.maxTicksLimit = n ? 3 : chart.$xTicks;
  x.ticks.font = y.ticks.font = { size: n ? 10 : 12 };
  if (y.title) y.title.display = !n && !!y.title.text;
  if (x.title) x.title.display = !n && !!x.title.text;
  const legend = chart.options.plugins.legend;
  if (legend.labels) Object.assign(legend.labels, { boxWidth: n ? 8 : 12, font: { size: n ? 10 : 12 } });
}

/**
 * Gráfica de líneas en el tiempo. datasets: [{ label, color, dashed?, fill?, marks? }]; marks dibuja solo puntos
 * (sin línea) y se usa para las anomalías por puntuación z. max: puntos que conserva push() por serie.
 * @param {HTMLCanvasElement} canvas Lienzo donde se dibuja.
 * @param {object[]} datasets Series.
 * @param {{yLabel?: string, yMin?: number, yMax?: number, legend?: boolean, max?: number}} [opts]
 * @returns {Chart} La gráfica creada.
 */
export function timeChart(canvas, datasets, { yLabel = "", yMin, yMax, legend = true, max = 300 } = {}) {
  const { text, grid } = themeOptions();
  const chart = new Chart(canvas, {
    type: "line",
    data: {
      datasets: datasets.map((d) => ({
        label: d.label, data: [], borderColor: d.color, backgroundColor: d.color + "33", borderWidth: d.width ?? 1.6,
        borderDash: d.dashed ? [5, 4] : undefined, pointRadius: d.marks ? 4.5 : d.points ?? 0, tension: 0.25, fill: d.fill ?? false, spanGaps: false,
        showLine: !d.marks, pointStyle: d.marks ? "crossRot" : "circle", pointBorderWidth: d.marks ? 2 : 1,
        parsing: false, normalized: true,
      })),
    },
    options: {
      // Sin animaciones: los datos en vivo llegan cada segundo.
      animation: false, responsive: true, maintainAspectRatio: false, interaction: { mode: "nearest", intersect: false },
      scales: {
        x: { type: "linear", ticks: { color: text, maxTicksLimit: 6, callback: timeTick }, grid: { color: grid } },
        y: { min: yMin, max: yMax, ticks: { color: text }, grid: { color: grid }, title: { display: !!yLabel, text: yLabel, color: text } },
      },
      plugins: {
        legend: { display: legend, labels: { color: text, boxWidth: 12 } },
        tooltip: { callbacks: { title: (items) => timeTick(items[0].parsed.x) } },
      },
    },
  });
  chart.$max = max;
  chart.$xTicks = 6;
  fit(chart);
  chart.update("none");
  charts.add(chart);
  return chart;
}

/**
 * Gráfica de barras sobre un eje x numérico (espectro en Hz).
 * @param {HTMLCanvasElement} canvas Lienzo.
 * @param {string} label Nombre de la serie.
 * @param {string} color Color de las barras.
 * @param {{xLabel?: string, yLabel?: string}} [opts] Títulos de los ejes.
 */
export function xyChart(canvas, label, color, { xLabel = "", yLabel = "" } = {}) {
  const { text, grid } = themeOptions();
  const chart = new Chart(canvas, {
    type: "bar",
    data: { labels: [], datasets: [{ label, data: [], backgroundColor: color, borderRadius: 2 }] },
    options: {
      animation: false, responsive: true, maintainAspectRatio: false,
      scales: {
        x: { ticks: { color: text, maxTicksLimit: 9 }, grid: { display: false }, title: { display: !!xLabel, text: xLabel, color: text } },
        y: { beginAtZero: true, ticks: { color: text }, grid: { color: grid }, title: { display: !!yLabel, text: yLabel, color: text } },
      },
      plugins: { legend: { display: false } },
    },
  });
  chart.$xTicks = 9;
  fit(chart);
  chart.update("none");
  charts.add(chart);
  return chart;
}

/**
 * Agrega un punto a la serie i y conserva como máximo max puntos (por defecto, el límite propio de la gráfica).
 * Los valores nulos o NaN se ignoran. No redibuja: quien llama decide cuándo hacer update().
 */
export function push(chart, i, x, y, max = chart.$max ?? 300) {
  if (y == null || Number.isNaN(y)) return;
  const d = chart.data.datasets[i].data;
  d.push({ x, y });
  if (d.length > max) d.splice(0, d.length - max);
}

/** Vacía todas las series de una gráfica. */
export function clear(chart) { chart.data.datasets.forEach((d) => (d.data = [])); chart.update(); }

/** Destruye todas las gráficas (al cambiar de dispositivo en el tablero). */
export function destroyAll() { charts.forEach((c) => c.destroy()); charts.clear(); }

// Al cruzar el ancho de 480 px, se ajustan las opciones de todas las gráficas.
matchMedia("(max-width: 479px)").addEventListener("change", () => { for (const c of charts) { fit(c); c.update("none"); } });

// Al cambiar de tema, se recolorean ejes, cuadrícula y leyenda.
addEventListener("st-theme", () => {
  const { text, grid } = themeOptions();
  for (const c of charts) {
    for (const axis of Object.values(c.options.scales)) {
      axis.ticks.color = text;
      if (axis.grid?.color !== undefined) axis.grid.color = grid;
      if (axis.title) axis.title.color = text;
    }
    if (c.options.plugins.legend.labels) c.options.plugins.legend.labels.color = text;
    c.update();
  }
});
