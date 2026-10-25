"use strict";
// Cliente web de ABCO: SPA sin framework que consume la API REST (/api/...).

const $ = (sel, root = document) => root.querySelector(sel);
const app = $("#app");

// ---------- Sesión ----------
const session = {
  get() { try { return JSON.parse(sessionStorage.getItem("prisma.session") || "null"); } catch { return null; } },
  set(s) { try { sessionStorage.setItem("prisma.session", JSON.stringify(s)); } catch { /* sin almacenamiento */ } memory = s; },
  clear() { try { sessionStorage.removeItem("prisma.session"); } catch { } memory = null; },
};
let memory = session.get();
const current = () => memory;

// ---------- Utilidades ----------
const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
const fmt = (n, d = 2) => (typeof n === "number" ? n.toLocaleString("es-MX", { maximumFractionDigits: d, minimumFractionDigits: d }) : n);
const bytes = (s) => new TextEncoder().encode(s).length;

/** Campo de contraseña con botón Mostrar/Ocultar (oculto por defecto). */
const pwInput = (id, attrs = "") => `<div class="pw-field"><input id="${id}" type="password" ${attrs}><button type="button" class="pw-toggle" data-pw="${id}" aria-label="Mostrar">Mostrar</button></div>`;
document.addEventListener("click", (e) => {
  const b = e.target.closest(".pw-toggle");
  if (!b) return;
  const input = document.getElementById(b.dataset.pw);
  const show = input.type === "password";
  input.type = show ? "text" : "password";
  b.textContent = show ? "Ocultar" : "Mostrar";
  b.setAttribute("aria-label", b.textContent);
});

/** Diálogo modal simple. buttons: [{ label, value, primary }]. Devuelve el value elegido. */
function showDialog(html, buttons = [{ label: "Entendido", value: true, primary: true }]) {
  const d = $("#dialog");
  d.innerHTML = `${html}<div class="actions">${buttons.map((b, i) => `<button type="button" data-i="${i}" class="${b.primary ? "" : "secondary"}">${esc(b.label)}</button>`).join("")}</div>`;
  return new Promise((resolve) => {
    d.onclick = (e) => { const b = e.target.closest("button[data-i]"); if (b) { d.close(); resolve(buttons[+b.dataset.i].value); } };
    d.oncancel = () => resolve(undefined);
    d.showModal();
  });
}

// ---------- Tema: automático (sigue al sistema) → claro → oscuro ----------
const THEMES = ["auto", "light", "dark"], THEME_LABEL = { auto: "automático", light: "claro", dark: "oscuro" };
function getTheme() { try { return localStorage.getItem("abco.theme") || "auto"; } catch { return "auto"; } }
function applyTheme(t) {
  if (t === "auto") delete document.documentElement.dataset.theme; else document.documentElement.dataset.theme = t;
  try { localStorage.setItem("abco.theme", t); } catch { }
  $("#themeBtn").textContent = `Tema: ${THEME_LABEL[t]}`;
}
$("#themeBtn").addEventListener("click", () => applyTheme(THEMES[(THEMES.indexOf(getTheme()) + 1) % THEMES.length]));
applyTheme(getTheme());

function toast(msg, error = false) {
  const t = $("#toast");
  t.textContent = msg;
  t.className = "toast show" + (error ? " err" : "");
  clearTimeout(toast.timer);
  toast.timer = setTimeout(() => (t.className = "toast"), 3800);
}

async function busy(button, fn) {
  const label = button.innerHTML;
  button.disabled = true;
  button.innerHTML = `<span class="spinner" aria-hidden="true"></span> Procesando…`;
  try { return await fn(); }
  finally {
    button.disabled = false;
    // Una vista puede pedir otra etiqueta para el siguiente paso (p. ej., el login con 2FA).
    button.innerHTML = button.dataset.nextLabel ?? label;
    delete button.dataset.nextLabel;
  }
}

class ApiError extends Error {
  constructor(status, body) { super(body?.error || `Error ${status}`); this.status = status; this.body = body; }
}

/** Llama a la API; agrega el token; devuelve JSON, Blob o la Response cruda según `as`. */
async function api(path, { method = "GET", json, form, as = "json" } = {}) {
  const headers = {};
  const s = current();
  if (s?.token) headers.Authorization = `Bearer ${s.token}`;
  let body;
  if (json !== undefined) { headers["Content-Type"] = "application/json"; body = JSON.stringify(json); }
  if (form) body = form;
  let res;
  try { res = await fetch(path, { method, headers, body }); }
  catch { throw new ApiError(0, { error: "No hay conexión con el servidor." }); }
  if (res.status === 401 && s?.token && !path.startsWith("/api/auth")) {
    session.clear(); updateNav(); location.hash = "#/login";
    throw new ApiError(401, { error: "Tu sesión expiró. Inicia sesión de nuevo." });
  }
  if (res.status === 429) throw new ApiError(429, { error: "Demasiados intentos. Espera un minuto." });
  if (res.status === 403 && s?.mustChangePassword) { location.hash = "#/cambiar"; throw new ApiError(403, { error: "Primero cambia tu contraseña temporal." }); }
  if (!res.ok) {
    let data = null;
    try { data = await res.json(); } catch { }
    throw new ApiError(res.status, data || { error: `Error ${res.status}` });
  }
  if (as === "raw") return res;
  if (as === "blob") return res.blob();
  if (res.status === 204) return null;
  return res.json();
}

function download(blob, name) {
  const url = URL.createObjectURL(blob);
  const a = Object.assign(document.createElement("a"), { href: url, download: name });
  document.body.append(a); a.click(); a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 60_000);
}

function filenameFrom(res, fallback) {
  const cd = res.headers.get("Content-Disposition") || "";
  const m = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(cd);
  return m ? decodeURIComponent(m[1]) : fallback;
}

// ---------- Catálogo (compartido por Ocultar y Cuenta) ----------
let catalogCache = null;
async function catalogPicker(root, onChange) {
  catalogCache ??= await api("/api/catalog");
  root.innerHTML = catalogCache.map((c) => `
    <button type="button" data-id="${esc(c.id)}" aria-pressed="false">
      <img src="/api/catalog/${encodeURIComponent(c.id)}" alt="${esc(c.name)}" loading="lazy"><span>${esc(c.name)}</span>
    </button>`).join("");
  root.addEventListener("click", (e) => {
    const b = e.target.closest("button[data-id]");
    if (!b) return;
    root.querySelectorAll("button").forEach((x) => x.setAttribute("aria-pressed", String(x === b)));
    onChange(b.dataset.id);
  });
}

/** Bloque "elige imagen": pestañas Catálogo / Archivo. Devuelve una función que agrega la imagen al FormData. */
function imageSource(root, { onChange = () => {} } = {}) {
  root.innerHTML = `
    <div class="seg" role="radiogroup" aria-label="Origen de la imagen">
      <label><input type="radio" name="src" value="catalogo" checked><span>Catálogo</span></label>
      <label><input type="radio" name="src" value="archivo"><span>Mi imagen</span></label>
    </div>
    <div data-pane="catalogo" style="margin-top:12px"><div class="catalog"></div></div>
    <div data-pane="archivo" style="margin-top:12px" hidden>
      <input type="file" accept="image/png,image/jpeg,image/webp,image/bmp">
      <small>PNG, JPEG, WebP o BMP de hasta 15 MB. Las fotos de más de 1280 px por lado se reducen.</small>
    </div>`;
  let catalogId = null;
  const file = $("input[type=file]", root);
  catalogPicker($(".catalog", root), (id) => { catalogId = id; onChange(); }).catch((e) => toast(e.message, true));
  root.addEventListener("change", (e) => {
    if (e.target.name === "src") {
      root.querySelectorAll("[data-pane]").forEach((p) => (p.hidden = p.dataset.pane !== e.target.value));
      onChange();
    }
    if (e.target === file) onChange();
  });
  return {
    mode: () => $("input[name=src]:checked", root).value,
    append(form) {
      if (this.mode() === "archivo") {
        if (!file.files[0]) throw new Error("Selecciona un archivo de imagen.");
        form.append("image", file.files[0]);
      } else {
        if (!catalogId) throw new Error("Elige una imagen del catálogo.");
        form.append("catalogId", catalogId);
      }
      return form;
    },
    ready() { return this.mode() === "archivo" ? !!file.files[0] : !!catalogId; },
  };
}

// ---------- Vistas ----------
const views = {};

views.login = () => {
  app.innerHTML = `
    <div class="auth-wrap">
      <h1>Iniciar sesión</h1>
      <p class="lead">Si activaste el segundo factor, después de tu contraseña te pediremos tu <b>imagen-llave</b>.</p>
      <form class="card" id="f1" autocomplete="on">
        <label for="u">Usuario</label><input id="u" type="text" autocomplete="username" required>
        <label for="p">Contraseña</label>${pwInput("p", 'autocomplete="current-password" required')}
        <div id="step2" hidden>
          <label for="k">Imagen-llave (llave.png)</label>
          <input id="k" type="file" accept="image/png">
          <small>La imagen que descargaste al activar el 2FA. Sin ella no es posible entrar.</small>
        </div>
        <div class="actions"><button id="go">Entrar</button><a class="btn secondary" href="#/registro">Crear cuenta</a></div>
        <div class="forgot"><button type="button" class="link" id="forgot">¿Olvidaste tu contraseña?</button></div>
      </form>
    </div>`;
  $("#forgot").addEventListener("click", () =>
    showDialog(`<h2>¿Olvidaste tu contraseña?</h2><p>Contacta al administrador para que restablezca tu contraseña. Te dará una contraseña temporal que deberás cambiar al entrar.</p>`));
  let needs2fa = false;
  $("#f1").addEventListener("submit", (e) => {
    e.preventDefault();
    busy($("#go"), async () => {
      const username = $("#u").value.trim(), password = $("#p").value;
      try {
        let res;
        if (!needs2fa) {
          res = await api("/api/auth/login", { method: "POST", json: { username, password } });
          if (res.requires2fa) {
            needs2fa = true;
            $("#step2").hidden = false;
            $("#u").readOnly = true;
            $("#go").dataset.nextLabel = "Verificar imagen-llave";
            $("#k").focus();
            toast("Contraseña correcta. Ahora carga tu imagen-llave.");
            return;
          }
        } else {
          const key = $("#k").files[0];
          if (!key) return toast("Selecciona tu imagen-llave.", true);
          const form = new FormData();
          form.append("username", username); form.append("password", password); form.append("keyImage", key);
          res = await api("/api/auth/login/2fa", { method: "POST", form });
        }
        session.set(res);
        updateNav();
        toast(`Bienvenido, ${res.username}.`);
        location.hash = res.mustChangePassword ? "#/cambiar" : "#/ocultar";
      } catch (err) { toast(err.message, true); }
    });
  });
};

views.registro = () => {
  app.innerHTML = `
    <div class="auth-wrap">
      <h1>Crear cuenta</h1>
      <p class="lead">Después podrás activar el segundo factor con una imagen-llave en <b>Cuenta</b>.</p>
      <form class="card" id="f">
        <label for="u">Usuario</label><input id="u" type="text" autocomplete="username" pattern="[A-Za-z0-9_.\\-]{3,32}" required>
        <small>3 a 32 caracteres: letras, números, punto, guion o guion bajo.</small>
        <label for="p">Contraseña</label>${pwInput("p", 'autocomplete="new-password" minlength="8" required')}
        <label for="p2">Repite la contraseña</label>${pwInput("p2", 'autocomplete="new-password" minlength="8" required')}
        <div class="actions"><button id="go">Registrarme</button><a class="btn secondary" href="#/login">Ya tengo cuenta</a></div>
      </form>
    </div>`;
  $("#f").addEventListener("submit", (e) => {
    e.preventDefault();
    if ($("#p").value !== $("#p2").value) return toast("Las contraseñas no coinciden.", true);
    busy($("#go"), async () => {
      try {
        await api("/api/auth/register", { method: "POST", json: { username: $("#u").value.trim(), password: $("#p").value } });
        toast("Cuenta creada. Ya puedes iniciar sesión.");
        location.hash = "#/login";
      } catch (err) { toast(err.message, true); }
    });
  });
};

views.ocultar = () => {
  app.innerHTML = `
    <h1>Ocultar un mensaje</h1>
    <p class="lead">ABCO cifra el mensaje con su "César matricial" y lo esconde en los bits menos significativos siguiendo un recorrido barajado con tu frase. La esteganografía clásica escribe los bits en orden y sin clave.</p>
    <div class="grid two">
      <form class="card" id="f">
        <h2>1. Imagen portadora</h2>
        <div id="src"></div>
        <p id="cap" class="muted" style="margin:10px 0 0"></p>
        <h2 style="margin-top:18px">2. Mensaje</h2>
        <label for="msg">Mensaje secreto</label>
        <textarea id="msg" required placeholder="Escribe aquí lo que quieres ocultar…"></textarea>
        <small id="count">0 bytes</small>
        <label>Algoritmo</label>
        <div class="seg" role="radiogroup">
          <label><input type="radio" name="mode" value="prisma" checked><span>ABCO</span></label>
          <label><input type="radio" name="mode" value="clasico"><span>Esteganografía clásica</span></label>
        </div>
        <div id="passWrap">
          <label for="pass">Frase secreta</label>
          ${pwInput("pass", 'minlength="4" autocomplete="off" placeholder="Compártela con el destinatario por otro medio"')}
        </div>
        <div class="actions"><button id="go">Ocultar en la imagen</button></div>
      </form>
      <section class="card" id="out" aria-live="polite"><h2>Resultado</h2><div class="empty">Aquí aparecerá la imagen con el mensaje oculto.</div></section>
    </div>`;
  const src = imageSource($("#src"), { onChange: refreshCapacity });
  const msg = $("#msg");
  msg.addEventListener("input", () => ($("#count").textContent = `${bytes(msg.value)} bytes`));
  $("#f").addEventListener("change", (e) => { if (e.target.name === "mode") $("#passWrap").hidden = e.target.value !== "prisma"; });

  async function refreshCapacity() {
    $("#cap").textContent = "";
    if (!src.ready()) return;
    try {
      const c = await api("/api/stego/capacity", { method: "POST", form: src.append(new FormData()) });
      $("#cap").innerHTML = `Imagen ${c.width}×${c.height}${c.resized ? ` (reducida desde ${c.originalWidth}×${c.originalHeight})` : ""} · capacidad ABCO <b>${c.prismaCapacity.toLocaleString("es-MX")} B</b> · clásico ${c.classicCapacity.toLocaleString("es-MX")} B`;
    } catch (err) { $("#cap").textContent = err.message; }
  }

  $("#f").addEventListener("submit", (e) => {
    e.preventDefault();
    busy($("#go"), async () => {
      try {
        const mode = $("input[name=mode]:checked").value;
        const form = src.append(new FormData());
        form.append("message", msg.value); form.append("mode", mode); form.append("passphrase", $("#pass").value);
        const res = await api("/api/stego/embed", { method: "POST", form, as: "raw" });
        const blob = await res.blob();
        const name = filenameFrom(res, "prisma.png");
        const url = URL.createObjectURL(blob);
        const psnr = res.headers.get("X-Prisma-Psnr"), used = +res.headers.get("X-Prisma-Payload-Bytes"), cap = +res.headers.get("X-Prisma-Capacity");
        $("#out").innerHTML = `
          <h2>Resultado <span class="badge info">${mode === "prisma" ? "ABCO" : "Esteganografía clásica"}</span></h2>
          <img class="preview" src="${url}" alt="Imagen con el mensaje oculto">
          <div class="stats">
            <div class="stat"><b>${psnr === "inf" ? "∞" : fmt(+psnr)} dB</b><small>PSNR (calidad; > 40 dB es imperceptible)</small></div>
            <div class="stat"><b>${used.toLocaleString("es-MX")} B</b><small>mensaje</small></div>
            <div class="stat"><b>${fmt((100 * used) / cap, 3)} %</b><small>de la capacidad</small></div>
          </div>
          <div class="actions"><button class="secondary" id="dl">Descargar PNG</button></div>
          <p class="muted"><small>Envíala <b>como archivo</b>. Si una app de mensajería la recomprime "como foto", el mensaje se destruye.</small></p>`;
        $("#dl").addEventListener("click", () => download(blob, name));
        toast("Mensaje oculto.");
      } catch (err) { toast(err.message, true); }
    });
  });
};

views.revelar = () => {
  app.innerHTML = `
    <h1>Revelar un mensaje</h1>
    <p class="lead">Carga la imagen PNG que recibiste. Con ABCO necesitas la frase; si es incorrecta o la imagen fue alterada, el sistema responde con el mismo error genérico.</p>
    <div class="grid two">
      <form class="card" id="f">
        <label for="img">Imagen PNG</label>
        <input id="img" type="file" accept="image/png" required>
        <label>Algoritmo</label>
        <div class="seg">
          <label><input type="radio" name="mode" value="prisma" checked><span>ABCO</span></label>
          <label><input type="radio" name="mode" value="clasico"><span>Esteganografía clásica</span></label>
        </div>
        <div id="passWrap"><label for="pass">Frase secreta</label>${pwInput("pass", 'autocomplete="off"')}</div>
        <div class="actions"><button id="go">Revelar</button></div>
      </form>
      <section class="card" id="out" aria-live="polite"><h2>Mensaje</h2><div class="empty">Aquí aparecerá el mensaje oculto.</div></section>
    </div>`;
  $("#f").addEventListener("change", (e) => { if (e.target.name === "mode") $("#passWrap").hidden = e.target.value !== "prisma"; });
  $("#f").addEventListener("submit", (e) => {
    e.preventDefault();
    busy($("#go"), async () => {
      const form = new FormData();
      form.append("image", $("#img").files[0]);
      form.append("mode", $("input[name=mode]:checked").value);
      form.append("passphrase", $("#pass").value);
      try {
        const r = await api("/api/stego/extract", { method: "POST", form });
        $("#out").innerHTML = `<h2>Mensaje <span class="badge ok">${r.bytes} B</span></h2><div class="message-box">${esc(r.message)}</div>`;
      } catch (err) {
        const raw = err.body?.rawSequentialHex;
        $("#out").innerHTML = `<h2>Mensaje</h2><div class="error-box">${esc(err.message)}</div>
          ${raw ? `<h3>Lo que vería un atacante leyendo los bits menos significativos en orden</h3><p class="hex">${esc(raw)}</p><small>Sin la frase, los bits parecen ruido aleatorio.</small>` : ""}`;
      }
    });
  });
};

views.cesar = () => {
  app.innerHTML = `
    <h1>Laboratorio César</h1>
    <p class="lead">C = (P + k) mod n. Con solo n claves posibles, se rompe probando todas y eligiendo la que mejor se parece al español con la prueba χ².</p>
    <div class="grid two">
      <form class="card" id="f">
        <label for="t">Texto</label>
        <textarea id="t">La criptografía estudia cómo proteger la información de los mensajes que se envían entre dos personas</textarea>
        <label for="k">Clave k = <output id="kv">3</output></label>
        <input id="k" type="range" min="0" max="25" value="3">
        <label class="check"><input id="enye" type="checkbox"> Incluir Ñ (alfabeto de 27 letras)</label>
        <div class="actions">
          <button type="button" data-op="encrypt">Cifrar</button>
          <button type="button" class="secondary" data-op="decrypt">Descifrar</button>
          <button type="button" class="secondary" data-op="crack">Romper (χ²)</button>
        </div>
      </form>
      <section class="card" id="out" aria-live="polite"><h2>Resultado</h2><div class="empty">Cifra un texto y luego intenta romperlo.</div></section>
    </div>`;
  const k = $("#k");
  k.addEventListener("input", () => ($("#kv").textContent = k.value));
  $("#enye").addEventListener("change", (e) => { k.max = e.target.checked ? 26 : 25; $("#kv").textContent = k.value; });
  $("#f").addEventListener("click", (e) => {
    const b = e.target.closest("button[data-op]");
    if (!b) return;
    busy(b, async () => {
      const op = b.dataset.op, text = $("#t").value, enye = $("#enye").checked;
      try {
        if (op === "crack") {
          const r = await api("/api/caesar/crack", { method: "POST", json: { text, enye } });
          // Barra = χ²(mejor) / χ²(k): la clave correcta llena la barra y las demás quedan casi vacías.
          const best = Math.max(r.best.chiSquare, 0.01);
          $("#out").innerHTML = `
            <h2>Clave encontrada: k = ${r.best.key} <span class="badge ok">${fmt(r.ms)} ms</span></h2>
            <div class="message-box">${esc(r.best.preview)}</div>
            <h3>Las ${r.candidates.length} claves ordenadas por χ² (menor = más parecido al español)</h3>
            <div class="table-wrap"><table><thead><tr><th>k</th><th>χ²</th><th></th><th>Vista previa</th></tr></thead><tbody>
            ${r.candidates.map((c, i) => `<tr class="${i === 0 ? "best" : ""}"><td class="num">${c.key}</td><td class="num">${fmt(c.chiSquare)}</td>
              <td><div class="bar"><i style="width:${Math.max(2, Math.min(100, (100 * best) / Math.max(c.chiSquare, 0.01)))}%"></i></div></td><td>${esc(c.preview.slice(0, 48))}</td></tr>`).join("")}
            </tbody></table></div>`;
        } else {
          const r = await api(`/api/caesar/${op}`, { method: "POST", json: { text, key: +k.value, enye } });
          $("#out").innerHTML = `<h2>${op === "encrypt" ? "Texto cifrado" : "Texto descifrado"} <span class="badge info">k = ${k.value}, n = ${r.alphabet.length}</span></h2>
            <div class="message-box">${esc(r.result)}</div>
            <div class="actions"><button type="button" class="secondary" id="use">Usar como entrada</button></div>`;
          $("#use").addEventListener("click", () => ($("#t").value = r.result));
        }
      } catch (err) { toast(err.message, true); }
    });
  });
};

views.comparador = () => {
  app.innerHTML = `
    <h1>Comparador de algoritmos</h1>
    <p class="lead">El mismo mensaje con los tres algoritmos del sistema: César, esteganografía clásica y ABCO. La entropía (bits por byte, máximo 8) mide qué tanto se borran los patrones del idioma; la última columna dice si una alteración de un solo bit se detecta.</p>
    <form class="card" id="f">
      <div class="grid two">
        <div><label for="m">Mensaje</label><textarea id="m">la criptografia estudia como proteger la informacion de los mensajes</textarea></div>
        <div>
          <label for="p">Frase (ABCO)</label><input id="p" type="text" value="frase-secreta">
          <label for="k">Clave César</label><input id="k" type="number" min="0" max="25" value="3">
          <div class="actions"><button id="go">Comparar</button></div>
        </div>
      </div>
    </form>
    <div id="out" style="margin-top:16px" aria-live="polite"></div>`;
  $("#f").addEventListener("submit", (e) => {
    e.preventDefault();
    busy($("#go"), async () => {
      try {
        const r = await api("/api/compare", { method: "POST", json: { message: $("#m").value, passphrase: $("#p").value, caesarKey: +$("#k").value } });
        $("#out").innerHTML = `
          <p class="muted">Texto claro: ${r.plainBytes} B · entropía ${fmt(r.plainEntropy, 3)} bits/byte</p>
          <div class="compare">${r.results.map((x) => `
            <article class="card">
              <h2>${esc(x.algorithm)} <span class="badge info">${esc(x.kind)}</span></h2>
              <div class="stats">
                <div class="stat"><b>${x.outputBytes} B</b><small>salida</small></div>
                <div class="stat"><b>${fmt(x.ms)} ms</b><small>tiempo</small></div>
                <div class="stat"><b>${fmt(x.entropy, 3)}</b><small>entropía (bits/byte)</small><div class="bar"><i style="width:${(100 * x.entropy) / 8}%"></i></div></div>
                <div class="stat"><b><span class="badge ${x.detectsTampering ? "ok" : "no"}">${x.detectsTampering ? "Sí" : "No"}</span></b><small>detecta alteración</small></div>
              </div>
              <p class="muted"><small>${esc(x.notes)}</small></p>
              <p class="hex">${esc(x.outputHex)}</p>
            </article>`).join("")}</div>`;
      } catch (err) { toast(err.message, true); }
    });
  });
};

views.cuenta = async () => {
  app.innerHTML = `<h1>Cuenta y seguridad</h1>
    <div class="grid two"><section class="card" id="me">Cargando…</section><section class="card" id="tfa"></section></div>
    <div class="grid two" style="margin-top:16px"><section class="card" id="pwc"></section></div>`;
  passwordForm($("#pwc"), false);
  let me;
  try { me = await api("/api/account/me"); } catch (err) { $("#me").textContent = err.message; return; }
  $("#me").innerHTML = `
    <h2>${esc(me.username)} <span class="badge info">${esc(me.role)}</span></h2>
    <p>Segundo factor (imagen-llave): <span class="badge ${me.twoFactorEnabled ? "ok" : "no"}">${me.twoFactorEnabled ? "activado" : "desactivado"}</span></p>
    <p class="muted"><small>Cuenta creada el ${new Date(me.createdUtc + (me.createdUtc.endsWith("Z") ? "" : "Z")).toLocaleString("es-MX")}.</small></p>
    <h3>¿Cómo funciona la imagen-llave?</h3>
    <p class="muted"><small>El servidor genera un secreto aleatorio de 32 bytes, lo oculta con ABCO en la imagen que elijas usando tu contraseña como frase, y solo guarda su huella SHA-256. Al iniciar sesión, revela el secreto con tu contraseña y compara la huella. Sin la imagen no se entra, y la imagen sola no sirve sin la contraseña. Si cambias de contraseña, tendrás que generar una nueva.</small></p>`;
  if (me.twoFactorEnabled) {
    $("#tfa").innerHTML = `
      <h2>Desactivar 2FA</h2>
      <form id="off"><label for="pw">Contraseña</label>${pwInput("pw", 'autocomplete="current-password" required')}
      <div class="actions"><button class="danger" id="go">Desactivar</button></div></form>
      <h3>¿Perdiste tu imagen-llave?</h3><p class="muted"><small>Un administrador puede restablecer tu 2FA desde la consola de administración.</small></p>`;
    $("#off").addEventListener("submit", (e) => {
      e.preventDefault();
      busy($("#go"), async () => {
        try { await api("/api/account/2fa/disable", { method: "POST", json: { password: $("#pw").value } }); toast("2FA desactivado."); views.cuenta(); updateSession({ twoFactorEnabled: false }); }
        catch (err) { toast(err.message, true); }
      });
    });
  } else {
    $("#tfa").innerHTML = `
      <h2>Activar 2FA con imagen-llave</h2>
      <form id="on">
        <label>Imagen para tu llave</label><div id="src"></div>
        <label for="pw">Confirma tu contraseña</label>${pwInput("pw", 'autocomplete="current-password" required')}
        <div class="actions"><button id="go">Generar mi imagen-llave</button></div>
      </form>
      <div id="key" aria-live="polite"></div>`;
    const src = imageSource($("#src"));
    $("#on").addEventListener("submit", (e) => {
      e.preventDefault();
      busy($("#go"), async () => {
        try {
          const form = src.append(new FormData());
          form.append("password", $("#pw").value);
          const res = await api("/api/account/2fa/enable", { method: "POST", form, as: "raw" });
          const blob = await res.blob(), name = filenameFrom(res, "llave.png");
          download(blob, name);
          updateSession({ twoFactorEnabled: true });
          const badge = $("#me .badge.no");
          if (badge) { badge.className = "badge ok"; badge.textContent = "activado"; }
          $("#on").remove();
          $("#key").innerHTML = `
            <div class="message-box">2FA activado. Se descargó <b>${esc(name)}</b>. Guárdala en un lugar seguro: la necesitarás cada vez que inicies sesión.</div>
            <img class="preview" style="margin-top:12px" src="${URL.createObjectURL(blob)}" alt="Tu imagen-llave">
            <div class="actions"><button class="secondary" id="again">Descargar de nuevo</button></div>`;
          $("#again").addEventListener("click", () => download(blob, name));
        } catch (err) { toast(err.message, true); }
      });
    });
  }
};

/**
 * Formulario de cambio de contraseña. forced = true en la vista obligatoria tras un restablecimiento.
 * El servidor entrega una sesión nueva; si había 2FA, se desactiva (la llave dependía de la contraseña anterior).
 */
function passwordForm(root, forced) {
  root.innerHTML = `
    <h2>${forced ? "Cambia tu contraseña" : "Cambiar contraseña"}</h2>
    ${forced ? `<p class="lead">Entraste con una contraseña temporal. Elige una nueva para continuar.</p>` : ""}
    <form id="pwf">
      <label for="cur">${forced ? "Contraseña temporal" : "Contraseña actual"}</label>${pwInput("cur", 'autocomplete="current-password" required')}
      <label for="np">Nueva contraseña</label>${pwInput("np", 'autocomplete="new-password" minlength="8" required')}
      <label for="np2">Repite la nueva contraseña</label>${pwInput("np2", 'autocomplete="new-password" minlength="8" required')}
      <div class="actions"><button id="pwgo">Guardar contraseña</button></div>
    </form>`;
  $("#pwf", root).addEventListener("submit", (e) => {
    e.preventDefault();
    if ($("#np", root).value !== $("#np2", root).value) return toast("Las contraseñas nuevas no coinciden.", true);
    busy($("#pwgo", root), async () => {
      try {
        const r = await api("/api/account/password", { method: "POST", json: { currentPassword: $("#cur", root).value, newPassword: $("#np", root).value } });
        session.set(r.session);
        updateNav();
        toast(r.twoFactorDisabled ? "Contraseña cambiada. Tu 2FA se desactivó: genera una nueva imagen-llave en Cuenta." : "Contraseña cambiada.");
        if (forced) location.hash = "#/ocultar"; else views.cuenta();
      } catch (err) { toast(err.message, true); }
    });
  });
}

views.cambiar = () => {
  app.innerHTML = `<div class="auth-wrap"><section class="card" id="pwc"></section>
    <p><button type="button" class="link" id="out">Cerrar sesión</button></p></div>`;
  passwordForm($("#pwc"), true);
  $("#out").addEventListener("click", () => $("#logoutBtn").click());
};

views.admin = async () => {
  if (current()?.role !== "admin") { app.innerHTML = `<div class="card error-box">Solo el rol administrador puede ver esta consola.</div>`; return; }
  app.innerHTML = `
    <h1>Consola de administración</h1>
    <p class="lead">Los nombres de usuario se muestran <b>ofuscados con César</b> (ASCII imprimible, k = 13). Es ofuscación, no cifrado: el botón "Revelar" la deshace.</p>
    <div class="grid two">
      <section class="card"><h2>Traza de la última operación ABCO</h2><div id="trace" class="muted">Cargando…</div>
        <div class="actions"><button class="secondary" id="reload">Actualizar</button></div></section>
      <section class="card"><h2>Mapa de diferencias |original − estego|</h2>
        <p class="muted"><small>Cada punto blanco es un píxel cuyo bit menos significativo cambió. Con ABCO quedan dispersos por el recorrido; con la esteganografía clásica se ven en franja al inicio.</small></p>
        <div id="diff" class="empty">Sin datos todavía.</div></section>
    </div>
    <section class="card" style="margin-top:16px"><h2>Usuarios</h2><div id="users">Cargando…</div></section>
    <section class="card" style="margin-top:16px"><h2>Auditoría</h2><div id="audit">Cargando…</div></section>`;

  const loadTrace = async () => {
    try {
      const t = await api("/api/admin/trace/last");
      $("#trace").innerHTML = `<p><small>Usuario: <code>${esc(t.user)}</code> · ${new Date(t.trace.utc).toLocaleString("es-MX")}</small></p><pre>${esc(t.text)}</pre>`;
    } catch (err) { $("#trace").textContent = err.message; }
    try {
      const blob = await api("/api/admin/diffmap/last", { as: "blob" });
      $("#diff").outerHTML = `<img id="diff" class="preview" src="${URL.createObjectURL(blob)}" alt="Mapa de diferencias">`;
    } catch { }
  };
  const loadUsers = async () => {
    const users = await api("/api/admin/users");
    $("#users").innerHTML = `<div class="table-wrap"><table><thead><tr><th>Id</th><th>Usuario (ofuscado)</th><th>Rol</th><th>2FA</th><th>Acciones</th></tr></thead><tbody>
      ${users.map((u) => `<tr>
        <td class="num">${u.id}</td>
        <td><div class="cell-actions"><code data-obf="${esc(u.username)}">${esc(u.username)}</code><button class="link" data-reveal>Revelar</button></div></td>
        <td><span class="badge ${u.role === "admin" ? "info" : "neutral"}">${u.role === "admin" ? "administrador" : "usuario"}</span>
          ${u.mustChangePassword ? `<br><small class="muted">contraseña temporal pendiente</small>` : ""}</td>
        <td><div class="cell-actions">${u.twoFactorEnabled ? `<span class="badge ok">sí</span><button class="link" data-reset2fa="${u.id}">Restablecer 2FA</button>` : `<span class="badge no">no</span>`}</div></td>
        <td><div class="cell-actions">
          <button class="link" data-resetpw="${u.id}">Restablecer contraseña</button>
          <button class="link" data-role="${u.id}" data-to="${u.role === "admin" ? "user" : "admin"}">${u.role === "admin" ? "Quitar administrador" : "Hacer administrador"}</button>
        </div></td></tr>`).join("")}
      </tbody></table></div>`;
  };
  const loadAudit = async () => {
    const rows = await api("/api/admin/audit?take=60");
    $("#audit").innerHTML = `<div class="table-wrap"><table><thead><tr><th>Fecha</th><th>Usuario</th><th>Acción</th><th>Modo</th><th>Bytes</th><th>ms</th><th>Resultado</th></tr></thead><tbody>
      ${rows.map((a) => `<tr><td>${new Date(a.utc + (a.utc.endsWith("Z") ? "" : "Z")).toLocaleString("es-MX")}</td><td><code>${esc(a.user)}</code></td><td>${esc(a.action)}</td>
        <td>${esc(a.mode ?? "")}</td><td class="num">${a.bytes}</td><td class="num">${a.ms}</td><td><span class="badge ${a.ok ? "ok" : "no"}">${a.ok ? "ok" : "falló"}</span></td></tr>`).join("")}
      </tbody></table></div>`;
  };

  $("#reload").addEventListener("click", loadTrace);
  $("#users").addEventListener("click", async (e) => {
    const rev = e.target.closest("[data-reveal]"), reset = e.target.closest("[data-reset2fa]");
    const resetPw = e.target.closest("[data-resetpw]"), role = e.target.closest("[data-role]");
    try {
      if (resetPw) {
        const ok = await showDialog(`<h2>Restablecer contraseña</h2><p>Se generará una contraseña temporal y el usuario deberá cambiarla al entrar. Si tenía 2FA, se desactivará.</p>`,
          [{ label: "Restablecer", value: true, primary: true }, { label: "Cancelar", value: false }]);
        if (!ok) return;
        const r = await api(`/api/admin/users/${resetPw.dataset.resetpw}/reset-password`, { method: "POST" });
        const copy = await showDialog(`<h2>Contraseña temporal</h2><p class="temp-pass">${esc(r.temporaryPassword)}</p>
          <p class="muted"><small>Se muestra una sola vez. Entrégala al usuario por un medio seguro.</small></p>`,
          [{ label: "Copiar y cerrar", value: true, primary: true }, { label: "Cerrar", value: false }]);
        if (copy) { try { await navigator.clipboard.writeText(r.temporaryPassword); toast("Copiada al portapapeles."); } catch { toast("No se pudo copiar; anótala antes de cerrar.", true); } }
        loadUsers();
      }
      if (role) {
        await api(`/api/admin/users/${role.dataset.role}/role`, { method: "POST", json: { role: role.dataset.to } });
        toast(role.dataset.to === "admin" ? "Ahora es administrador." : "Ya no es administrador.");
        loadUsers();
      }
      if (rev) {
        const code = rev.previousElementSibling;
        const r = await api("/api/admin/deobfuscate", { method: "POST", json: { text: code.dataset.obf } });
        code.textContent = code.textContent === r.text ? code.dataset.obf : r.text;
      }
      if (reset && await showDialog(`<h2>Restablecer 2FA</h2><p>El usuario podrá entrar solo con su contraseña y deberá generar otra imagen-llave.</p>`,
          [{ label: "Restablecer", value: true, primary: true }, { label: "Cancelar", value: false }])) {
        await api(`/api/admin/users/${reset.dataset.reset2fa}/reset-2fa`, { method: "POST" });
        toast("2FA restablecido."); loadUsers();
      }
    } catch (err) { toast(err.message, true); }
  });
  await Promise.allSettled([loadTrace(), loadUsers().catch((e) => ($("#users").textContent = e.message)), loadAudit().catch((e) => ($("#audit").textContent = e.message))]);
};

// ---------- Navegación ----------
function updateSession(patch) { const s = current(); if (s) session.set({ ...s, ...patch }); }

function updateNav() {
  const s = current();
  const nav = $("#nav");
  nav.hidden = !s || !!s.mustChangePassword;
  $("#menuBtn").hidden = !s;
  nav.querySelectorAll("[data-admin]").forEach((a) => (a.hidden = s?.role !== "admin"));
  nav.querySelectorAll("a").forEach((a) => a.classList.toggle("active", a.getAttribute("href") === location.hash));
}

const publicViews = new Set(["login", "registro"]);
function route() {
  const name = (location.hash.replace(/^#\//, "") || "").split("?")[0];
  const s = current();
  if (!s && !publicViews.has(name)) { location.hash = "#/login"; return; }
  if (s?.mustChangePassword && name !== "cambiar") { location.hash = "#/cambiar"; return; }
  if (s && (publicViews.has(name) || !views[name] || (name === "cambiar" && !s.mustChangePassword))) { location.hash = "#/ocultar"; return; }
  $("#nav").classList.remove("open");
  $("#menuBtn").setAttribute("aria-expanded", "false");
  updateNav();
  views[name]();
  app.focus({ preventScroll: true });
  window.scrollTo(0, 0);
}

$("#menuBtn").addEventListener("click", () => {
  const open = $("#nav").classList.toggle("open");
  $("#menuBtn").setAttribute("aria-expanded", String(open));
});
$("#logoutBtn").addEventListener("click", () => { session.clear(); toast("Sesión cerrada."); location.hash = "#/login"; updateNav(); });
window.addEventListener("hashchange", route);
route();
