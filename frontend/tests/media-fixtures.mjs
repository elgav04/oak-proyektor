// «Backend» SINTÉTICO de la biblioteca de medios para los tests del panel: habla el protocolo REAL (la versión sale de protocol-version.mjs) con el cliente IPC real. Todos los nombres y rutas
// son inventados («Video de prueba», «E:\USB\…»): nada real ni de terceros. Responde media.list/media.get, REGISTRA los comandos y actualiza su estado como lo haría el backend real
// (reportProbe → ok/unsupported, setThumbnail → miniatura, importaciones que avanzan y terminan).
import { V } from "./protocol-version.mjs";
import { loadTs } from "./helper.mjs";

const { IpcClient } = await loadTs("/src/ipc/client.ts");

export const item = (id, over = {}) => ({
  id, kind: "video", name: `Medio de prueba ${id}`, folderId: null, originalFileName: `prueba-${id}.mp4`, ext: ".mp4", bytes: 1_000_000 + id, durationMs: 12_000, width: 1920, height: 1080,
  container: "MP4", videoCodec: "H.264", audioCodec: "AAC", hasAudio: true, status: "pending", statusCode: null, statusMessage: null,
  fileUrl: `file:///C:/datos/media/files/ab/${String(id).padStart(64, "a")}.mp4`, thumbnailUrl: null, revision: 1, createdAt: "2026-11-26T10:00:00.000Z", updatedAt: "2026-11-26T10:00:00.000Z", ...over,
});

export function createMediaBackend(options = {}) {
  const items = new Map();
  const folders = new Map();
  const commands = [];
  const queries = [];
  let nextId = 1;
  let nextFolder = 1;
  let deliver = () => {};
  const state = { freeBytes: options.freeBytes ?? 500 * 1024 ** 3 };

  const envelope = (kind, type, payload, id = crypto.randomUUID()) => JSON.stringify({ v: V, id, kind, type, payload });
  const reply = (req, result) => deliver(envelope("reply", req.type, { ok: true, result, error: null }, req.id));
  const fail = (req, code, message) => deliver(envelope("reply", req.type, { ok: false, result: null, error: { code, message } }, req.id));
  const event = (type, payload) => deliver(envelope("event", type, payload));
  const failed = (cmd, code, message) => event("command.failed", { commandId: cmd.id, commandType: cmd.type, error: { code, message } });

  const addItem = (over = {}) => { const it = item(nextId++, over); items.set(it.id, it); return it; };
  const addFolder = (name) => { const f = { id: nextFolder++, name }; folders.set(f.id, f); return f; };

  const matching = (r) => [...items.values()].filter((i) =>
    (r.folderId === null || i.folderId === r.folderId) && (!r.onlyUnfiled || i.folderId === null) && (r.kind === null || i.kind === r.kind)
    && (r.query === null || i.name.toLowerCase().includes(r.query.toLowerCase()))).sort((a, b) => a.name.localeCompare(b.name));

  const handleQuery = (req) => {
    queries.push(req);
    if (req.type === "media.list") {
      const all = matching(req.payload); const limit = req.payload.limit ?? 500;
      return reply(req, {
        folders: [...folders.values()].map((f) => ({ ...f, itemCount: [...items.values()].filter((i) => i.folderId === f.id).length })),
        items: all.slice(0, limit), truncated: all.length > limit, totalItems: items.size, totalBytes: [...items.values()].reduce((s, i) => s + i.bytes, 0), freeBytes: state.freeBytes,
      });
    }
    if (req.type === "media.get") { const i = items.get(req.payload.id); return i ? reply(req, i) : fail(req, "media.notFound", "El elemento no existe (puede que se haya borrado)."); }
    return fail(req, "unknown_message", req.type);
  };

  const touch = (it, changes) => { Object.assign(it, changes, { revision: it.revision + 1 }); };
  const changed = (cmd, kind, mediaId = null, count = 0) => event("media.changed", { commandId: cmd.id, kind, mediaId, count });
  let importHook = null;

  const handleCommand = (cmd) => {
    commands.push(cmd);
    const p = cmd.payload;
    switch (cmd.type) {
      case "media.import":
        if (importHook) return importHook(cmd);
        return;   // por defecto el test dirige el avance y el final con `progress()` / `finish()`
      case "media.rename": { const it = items.get(p.id); if (!it) return failed(cmd, "media.notFound", "El elemento no existe."); touch(it, { name: p.name.trim() }); return changed(cmd, "updated", it.id); }
      case "media.move": { const it = items.get(p.id); if (!it) return failed(cmd, "media.notFound", "El elemento no existe."); touch(it, { folderId: p.folderId }); return changed(cmd, "updated", it.id); }
      case "media.delete": items.delete(p.id); return changed(cmd, "deleted", p.id);
      case "media.createFolder": { if ([...folders.values()].some((f) => f.name.toLowerCase() === p.name.trim().toLowerCase())) return failed(cmd, "media.folderDuplicate", "Ya existe una carpeta con ese nombre."); addFolder(p.name.trim()); return changed(cmd, "folders"); }
      case "media.renameFolder": { const f = folders.get(p.id); if (f) f.name = p.name.trim(); return changed(cmd, "folders"); }
      case "media.deleteFolder": folders.delete(p.id); for (const i of items.values()) if (i.folderId === p.id) i.folderId = null; return changed(cmd, "folders");
      case "media.reportProbe": {
        const it = items.get(p.mediaId); if (!it) return failed(cmd, "media.notFound", "El elemento no existe.");
        if (p.ok) touch(it, { status: "ok", durationMs: p.durationMs ?? it.durationMs, width: p.width ?? it.width, height: p.height ?? it.height });
        else touch(it, { status: "unsupported", statusCode: "media.decodeFailed", statusMessage: "No se pudo reproducir: el archivo está dañado o incompleto." });
        return changed(cmd, "updated", it.id);
      }
      case "media.setThumbnail": { const it = items.get(p.mediaId); if (!it) return failed(cmd, "media.notFound", "El elemento no existe."); touch(it, { thumbnailUrl: `file:///C:/datos/media/thumbs/${it.id}.jpg?r=${it.revision + 1}` }); return changed(cmd, "updated", it.id); }
      case "media.verifyLibrary": return changed(cmd, "verified", null, 0);
      default: return;
    }
  };

  const transport = {
    send: (raw) => {
      const msg = JSON.parse(raw);
      if (msg.v !== V) return;   // como el backend real: otra versión no se interpreta
      queueMicrotask(() => { if (msg.kind === "query") handleQuery(msg); else handleCommand(msg); });
    },
    onMessage: (cb) => { deliver = cb; },
  };
  const client = new IpcClient(transport);

  return {
    client, items, folders, commands, queries, state, addItem, addFolder,
    sent: (type) => commands.filter((c) => c.type === type),
    onImport: (fn) => { importHook = fn; },
    progress: (importId, phase, doneBytes, totalBytes) => event("media.importProgress", { importId, phase, doneBytes, totalBytes }),
    finish: (importId, over = {}) => event("media.importFinished", { importId, ok: true, errorCode: null, errorMessage: null, mediaId: null, warningCode: null, warningMessage: null, duplicateOfId: null, duplicateOfName: null, ...over }),
    changedEvent: (kind = "updated", mediaId = null) => event("media.changed", { commandId: "x", kind, mediaId, count: 0 }),
  };
}

/** Un `ProbeRunner` simulado: responde lo que el test diga, de uno en uno, y registra el orden y la concurrencia. */
export function createFakeRunner(answer = () => ({ ok: true, durationMs: 9000, width: 1280, height: 720, errorCode: null, thumbnailBase64: "AAAA" })) {
  const calls = [];
  let active = 0;
  let maxActive = 0;
  const run = async (it, wantThumbnail) => {
    active++; maxActive = Math.max(maxActive, active);
    calls.push({ id: it.id, wantThumbnail });
    await new Promise((r) => setTimeout(r, 5));
    active--;
    const a = answer(it, wantThumbnail);
    if (a instanceof Error) throw a;
    return a;
  };
  return { run, calls, maxActive: () => maxActive };
}
