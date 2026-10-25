// Screenshots for the documentation, taken from the running system with the system Firefox (WebDriver BiDi).
//   ST_PASSWORD=... node docs/tools/capturas.mjs [http://localhost:5090]
// Start the server and, for live data, the simulator (--anomalies --gaps) before running it.
import puppeteer from "puppeteer-core";
import { mkdirSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";

const BASE = process.argv[2] ?? "http://localhost:5090";
const PASSWORD = process.env.ST_PASSWORD;
const OUT = path.join(path.dirname(fileURLToPath(import.meta.url)), "..", "capturas");
if (!PASSWORD) { console.error("Define ST_PASSWORD"); process.exit(1); }
mkdirSync(OUT, { recursive: true });

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const browser = await puppeteer.launch({
  browser: "firefox",
  executablePath: process.env.FIREFOX ?? "/usr/bin/firefox",
  headless: true,
  extraPrefsFirefox: { "geo.enabled": false, "dom.webnotifications.enabled": false },
});
const page = await browser.newPage();
page.on("pageerror", (e) => console.warn("  [pageerror]", e.message));

async function shot(name, opts = {}) {
  const file = path.join(OUT, name + ".png");
  await page.screenshot({ path: file, ...opts });
  console.log("✔", name);
}

/** Screenshot of the union of the bounding boxes of the given selectors (page coordinates). */
async function shotOf(name, selectors, pad = 12) {
  const box = await page.evaluate((sels, pad) => {
    const rs = sels.flatMap((s) => [...document.querySelectorAll(s)]).map((e) => e.getBoundingClientRect());
    if (!rs.length) return null;
    const x = Math.min(...rs.map((r) => r.left)) - pad + scrollX, y = Math.min(...rs.map((r) => r.top)) - pad + scrollY;
    const x2 = Math.max(...rs.map((r) => r.right)) + pad + scrollX, y2 = Math.max(...rs.map((r) => r.bottom)) + pad + scrollY;
    return { x: Math.max(0, x), y: Math.max(0, y), width: x2 - Math.max(0, x), height: y2 - Math.max(0, y) };
  }, selectors, pad);
  if (!box) { console.warn("  (sin elementos)", name, selectors); return; }
  await shot(name, { clip: box, captureBeyondViewport: true });
}

async function go(p, wait = 2500) {
  await page.goto(BASE + "/" + p, { waitUntil: "load" });
  // Sticky bars would cover the top of element screenshots taken after scrolling.
  await page.addStyleTag({ content: ".topbar, .grid.sidebar > aside { position: static !important; }" }).catch(() => {});
  await sleep(wait);
}

async function theme(t) {
  await page.evaluate((t) => localStorage.setItem("st.theme", JSON.stringify(t)), t);
}

// ---------- desktop, light theme
await page.setViewport({ width: 1366, height: 900, deviceScaleFactor: 1 });
await go("index.html", 800);
await page.evaluate(() => localStorage.clear());
await theme("light");
await go("index.html", 800);
await shot("web_login");
await page.type("#u", "admin");
await page.type("#p", PASSWORD);
await Promise.all([page.waitForNavigation({ waitUntil: "load" }), page.click("button[type=submit]")]);
await theme("light");

// Agent: register this browser, pin the key, start in simulator mode.
await go("agent.html", 2000);
if (await page.$("#register")) { await page.click("#register"); await sleep(1500); }
if (await page.$("#pinok")) { await page.click("#pinok"); await page.click("#dopin"); await sleep(800); }
await page.evaluate(() => { const s = document.getElementById("src-sim"); if (!s.checked) s.click(); });
await page.click("#start");
await sleep(9000);
await shotOf("web_agente", ["#payload", "#wire"].map((s) => `section:has(> ${s})`));
await shot("web_agente_completo", { fullPage: true });
await page.click("#start"); // stop

// Dashboard
await go("app.html", 7000);
await shot("web_dashboard");
await page.evaluate(() => document.getElementById("map").scrollIntoView({ block: "center" }));
await sleep(4000); // OSM tiles
await shotOf("web_mapa", ["section:has(> #map)"]);
await page.evaluate(() => { const c = document.getElementById("layer-heat"); c.click(); });
await sleep(2500);
await shotOf("web_mapa_calor", ["section:has(> #map)"]);
await shotOf("web_dashboard_graficas", ["#device-view .grid.auto > section:nth-child(-n+5)"]);
await shotOf("web_dashboard_malla", ["section.chart-card:has(#c-grid)"]);
await shotOf("web_alertas", ["aside section:has(#alerts)"]);
await page.click("#qr-btn");
await sleep(1200);
await shotOf("web_qr", ["#qr-dialog"], 4);
await page.click("#qr-close");

// Lab: run every scenario (also fills the console with accepted and rejected packets).
await go("lab.html", 2000);
await page.click("#run-all");
await page.waitForFunction(() => document.querySelector("#summary table") && !document.getElementById("run-all").disabled, { timeout: 120000 });
await sleep(800);
await shotOf("web_lab", [".page-head", "#summary", "#cfg"]);
await shotOf("web_lab_escenarios", ["#sc-bitflip", "#sc-replay", "#sc-mitm", "#sc-sigmix"]);

// Console
await go("console.html", 3000);
await page.evaluate(() => {
  const b = [...document.querySelectorAll("#pkts button")].find((x) => /\bok\b/.test(x.textContent) && x.textContent.includes("aes-256-gcm"));
  b?.click();
});
await sleep(800);
await shot("web_consola");
await page.evaluate(() => { document.querySelector('#s-rounds button[data-i="2"]')?.click(); document.querySelector(".stage[data-stage=sigmix]").scrollIntoView(); });
await sleep(600);
await shotOf("web_consola_sigmix", ['.stage[data-stage="sigmix"]']);
await shotOf("web_consola_aead", ['.stage[data-stage="wire"]', '.stage[data-stage="verify"]', '.stage[data-stage="aead"]']);
await shotOf("web_consola_entropia", ["section.card:has(> .chart-box > #c-ent)", "section.card:has(> #c-hex)"]);
await page.evaluate(() => {
  const b = [...document.querySelectorAll("#pkts button")].find((x) => x.textContent.includes("auth_failed"));
  b?.click();
});
await sleep(600);
await shotOf("web_consola_rechazo", ['.stage[data-stage="wire"]', '.stage[data-stage="verify"]']);

// Users and API
await go("admin.html", 2500);
await shot("web_usuarios");
await page.goto(BASE + "/scalar/", { waitUntil: "load" });
await sleep(5000);
await shot("web_api");

// Dark theme example
await theme("dark");
await go("app.html", 7000);
await shot("web_dashboard_oscuro");
await theme("light");

// ---------- phone viewport
await page.setViewport({ width: 390, height: 844, deviceScaleFactor: 2 });
await go("agent.html", 2500);
await page.click("#start");
await sleep(7000);
await page.evaluate(() => scrollTo(0, 0));
await sleep(300);
await shot("web_movil_agente");
await page.click("#start");
await go("app.html", 6000);
await shot("web_movil_dashboard");
await page.evaluate(() => document.getElementById("c-rssi").scrollIntoView());
await sleep(1000);
await shot("web_movil_graficas");

await browser.close();
console.log("Listo:", OUT);
