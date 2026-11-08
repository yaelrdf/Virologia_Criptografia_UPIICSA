// Cola sin conexión de lecturas EN CLARO en IndexedDB (máximo 500; se descartan las más antiguas). Las lecturas se
// cifran solo al enviarlas, así cada intento recibe un seq nuevo y estrictamente creciente. Si IndexedDB no está
// disponible, usa memoria.
/** Máximo de lecturas guardadas. */
const MAX = 500;
/** Promesa de la base de datos abierta (o null si no se pudo abrir). */
let dbp = null;
/** Cola de respaldo en memoria. */
const mem = [];

/** Abre (una sola vez) la base «signaltrace» con el almacén «queue» de llave autoincremental. */
function db() {
  if (dbp) return dbp;
  dbp = new Promise((resolve) => {
    try {
      const r = indexedDB.open("signaltrace", 1);
      r.onupgradeneeded = () => r.result.createObjectStore("queue", { keyPath: "id", autoIncrement: true });
      r.onsuccess = () => resolve(r.result);
      r.onerror = () => resolve(null);
    } catch { resolve(null); }
  });
  return dbp;
}

/**
 * Ejecuta una operación dentro de una transacción y resuelve al completarse.
 * @param {IDBDatabase} d Base abierta.
 * @param {IDBTransactionMode} mode "readonly" o "readwrite".
 * @param {(s: IDBObjectStore) => IDBRequest|void} fn Operación sobre el almacén.
 * @returns {Promise<any>} El resultado de la solicitud que devolvió fn.
 */
function tx(d, mode, fn) {
  return new Promise((resolve, reject) => {
    const t = d.transaction("queue", mode);
    const out = fn(t.objectStore("queue"));
    t.oncomplete = () => resolve(out?.result);
    t.onerror = () => reject(t.error);
  });
}

/** Agrega una lectura al final de la cola y recorta las más antiguas si se pasa del máximo. */
export async function push(reading) {
  const d = await db();
  if (!d) { mem.push({ id: Date.now() + Math.random(), reading }); if (mem.length > MAX) mem.shift(); return; }
  await tx(d, "readwrite", (s) => s.add({ reading }));
  const n = await size();
  if (n > MAX) {
    const oldest = await peek(n - MAX);
    await tx(d, "readwrite", (s) => oldest.forEach((o) => s.delete(o.id)));
  }
}

/** Devuelve sin quitar las primeras `limit` lecturas, en orden de llegada: [{ id, reading }]. */
export async function peek(limit = 1) {
  const d = await db();
  if (!d) return mem.slice(0, limit);
  return new Promise((resolve) => {
    const out = [];
    // El cursor recorre por llave, que crece con cada inserción.
    const req = d.transaction("queue").objectStore("queue").openCursor();
    req.onsuccess = () => {
      const c = req.result;
      if (c && out.length < limit) { out.push(c.value); c.continue(); } else resolve(out);
    };
    req.onerror = () => resolve(out);
  });
}

/** Quita una lectura por su id (después de que el servidor la aceptó o la rechazó en definitiva). */
export async function remove(id) {
  const d = await db();
  if (!d) { const i = mem.findIndex((m) => m.id === id); if (i >= 0) mem.splice(i, 1); return; }
  await tx(d, "readwrite", (s) => s.delete(id));
}

/** Número de lecturas pendientes. */
export async function size() {
  const d = await db();
  return d ? tx(d, "readonly", (s) => s.count()) : mem.length;
}

/** Vacía la cola. */
export async function clear() {
  const d = await db();
  mem.length = 0;
  if (d) await tx(d, "readwrite", (s) => s.clear());
}
