// Offline queue of PLAINTEXT readings in IndexedDB (max 500, oldest dropped). Readings are encrypted only when
// sent, so every attempt gets a fresh, strictly increasing seq. Falls back to memory if IndexedDB is unavailable.
const MAX = 500;
let dbp = null;
const mem = [];

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

function tx(d, mode, fn) {
  return new Promise((resolve, reject) => {
    const t = d.transaction("queue", mode);
    const out = fn(t.objectStore("queue"));
    t.oncomplete = () => resolve(out?.result);
    t.onerror = () => reject(t.error);
  });
}

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

export async function peek(limit = 1) {
  const d = await db();
  if (!d) return mem.slice(0, limit);
  return new Promise((resolve) => {
    const out = [];
    const req = d.transaction("queue").objectStore("queue").openCursor();
    req.onsuccess = () => {
      const c = req.result;
      if (c && out.length < limit) { out.push(c.value); c.continue(); } else resolve(out);
    };
    req.onerror = () => resolve(out);
  });
}

export async function remove(id) {
  const d = await db();
  if (!d) { const i = mem.findIndex((m) => m.id === id); if (i >= 0) mem.splice(i, 1); return; }
  await tx(d, "readwrite", (s) => s.delete(id));
}

export async function size() {
  const d = await db();
  return d ? tx(d, "readonly", (s) => s.count()) : mem.length;
}

export async function clear() {
  const d = await db();
  mem.length = 0;
  if (d) await tx(d, "readwrite", (s) => s.clear());
}
