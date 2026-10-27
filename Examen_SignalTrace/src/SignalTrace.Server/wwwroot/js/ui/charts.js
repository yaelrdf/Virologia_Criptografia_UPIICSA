// Chart.js helpers (global Chart from lib/chart/chart.umd.min.js). Linear x axis in epoch ms formatted as time,
// so no date adapter is needed. Colors follow the page theme and are re-applied on "st-theme".
const charts = new Set();

export const SERIES = { blue: "#2f7fd8", orange: "#e8833a", green: "#2ea043", purple: "#8957e5", red: "#e5534b", teal: "#1b998b" };

function css(name) { return getComputedStyle(document.documentElement).getPropertyValue(name).trim(); }

function themeOptions() {
  const text = css("--muted"), grid = css("--border");
  return { text, grid };
}

const timeTick = (v) => new Date(v).toLocaleTimeString("es-MX", { hour12: false });
const narrow = () => matchMedia("(max-width: 479px)").matches;

/** Phone-sized charts: fewer ticks, smaller legend, no axis titles. Re-applied when the width changes. */
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
 * Line chart over time. datasets: [{ label, color, dashed?, fill?, marks? }]; marks draws only points (no line),
 * used for the z-score anomalies. max: points kept per dataset by push().
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

/** Bar/line chart over a numeric x axis (spectrum in Hz). */
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

/** Appends a point to dataset i, keeping at most max points (the chart's own limit by default). */
export function push(chart, i, x, y, max = chart.$max ?? 300) {
  if (y == null || Number.isNaN(y)) return;
  const d = chart.data.datasets[i].data;
  d.push({ x, y });
  if (d.length > max) d.splice(0, d.length - max);
}

export function clear(chart) { chart.data.datasets.forEach((d) => (d.data = [])); chart.update(); }

export function destroyAll() { charts.forEach((c) => c.destroy()); charts.clear(); }

matchMedia("(max-width: 479px)").addEventListener("change", () => { for (const c of charts) { fit(c); c.update("none"); } });

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
