// «Backend» SINTÉTICO de canciones para los tests del panel: habla el protocolo REAL (sobre v12) con el cliente IPC real, así que los tests también comprueban los
// mensajes tal como salen hacia C#. TODA la letra está inventada aquí (palabras sueltas): ninguna canción real.
import { loadTs } from "./helper.mjs";

const { IpcClient } = await loadTs("/src/ipc/client.ts");

const WORDS = ["lumari", "tenoko", "sabiko", "zurimako", "vitola", "pemar", "ñandú", "quilo", "dubé", "malorí"];
export const line = (n, k = 0) => `${WORDS[(n * 3 + k) % WORDS.length]} ${WORDS[(n * 5 + k + 1) % WORDS.length]} ${WORDS[(n * 7 + k + 2) % WORDS.length]} ${n}`;
/** Cuerpo sintético de `lines` líneas. */
export const body = (n, lines = 2) => Array.from({ length: lines }, (_, i) => line(n, i)).join("\n");

const keyOf = (label) => label.normalize("NFD").replace(/\p{M}/gu, "").toLowerCase().replace(/[^\p{L}\p{N}]/gu, "");
const ALIASES = { verso: "Verso", coro: "Coro", puente: "Puente", final: "Final", intro: "Intro", estribillo: "Estribillo" };
const canonical = (raw) => {
  const label = raw.trim().replace(/\s+/g, " ");
  const m = /^([^\d]+?)\s*(\d+)?$/.exec(label);
  const alias = m && ALIASES[keyOf(m[1])];
  return alias ? (m[2] ? `${alias} ${Number(m[2])}` : alias) : label;
};
const kindOf = (label) => ({ verso: "verse", coro: "chorus", estribillo: "chorus", puente: "bridge", final: "ending", intro: "intro" })[keyOf(label.replace(/\d+$/, ""))] ?? "other";

/** Una versión mínima (pero fiel a lo que el panel necesita) del parser del backend: etiquetas entre corchetes, repeticiones, bloques sin etiquetas, `Orden:` ignorado. */
export function fakeParse(text) {
  const lines = text.replace(/\r\n?/g, "\n").split("\n").map((l) => l.trim());
  const sections = [];
  const arrangement = [];
  const byKey = new Map();
  let current = null;
  let format = "empty";
  const finish = () => {
    if (!current) return;
    const bodyText = current.lines.join("\n").replace(/^\n+|\n+$/g, "").replace(/\n{3,}/g, "\n\n");
    if (bodyText === "") { if (byKey.has(keyOf(current.label))) arrangement.push(byKey.get(keyOf(current.label))); return; }
    const k = keyOf(current.label);
    if (byKey.has(k) && sections[byKey.get(k)].text === bodyText) { arrangement.push(byKey.get(k)); return; }
    sections.push({ id: null, kind: kindOf(current.label), label: current.label, text: bodyText });
    byKey.set(k, sections.length - 1);
    arrangement.push(sections.length - 1);
  };
  for (const raw of lines) {
    const label = /^\[([^\[\]]{1,60})\]\s*:?$/.exec(raw);
    if (label && label[1].trim()) { finish(); current = { label: canonical(label[1]), lines: [] }; format = "brackets"; continue; }
    if (/^orden\s*:/i.test(raw)) continue;
    if (!current) { if (raw === "") continue; current = { label: "Sección 1", lines: [] }; if (format === "empty") format = "blocks"; }
    current.lines.push(raw);
  }
  finish();
  const errors = sections.length > 60 ? ["Hay demasiadas secciones (el máximo es 60)."] : [];
  return { sections, arrangement, warnings: [], errors, detected: sections.length === 0 ? "empty" : format };
}

/**
 * Backend falso. Opciones: `songs` (canciones iniciales), `usedIn` (id → nº de servicios), `settings`.
 * Inyección de fallos: `backend.failNext(type, code, message)`; `backend.dropNextChanged()` (el comando se «pierde»: sin respuesta); `backend.externalChange(id)`
 * (otra ventana guarda la canción: sube su revisión sin que el panel lo sepa).
 */
export function createSongBackend(options = {}) {
  const songs = new Map();
  const histories = new Map();
  let nextSongId = 1;
  let nextSectionId = 1;
  let nextRevisionId = 1;
  const usedIn = options.usedIn ?? {};
  const state = {
    settings: options.settings ?? {
      theme: { fontFamily: "Arial, sans-serif", fontSize: 60, color: "#FFFFFF", outlineWidth: 2, outlineColor: "#000000", shadowBlur: 8, shadowOffset: 3, shadowColor: "#000000CC", align: "center", verticalAlign: "middle", lineHeight: 1.25, marginTop: 70, marginRight: 120, marginBottom: 70, marginLeft: 120, autoFit: true, minFontSize: 36 },
      transitionMs: 400,
      atEnd: "stay",
    },
  };
  const commands = [];
  const queries = [];
  let deliver = () => {};
  let failure = null;
  let dropChanged = false;
  let holdNext = false;
  const held = [];

  const envelope = (kind, type, payload, id = crypto.randomUUID()) => JSON.stringify({ v: 14, id, kind, type, payload });
  const reply = (req, result) => deliver(envelope("reply", req.type, { ok: true, result, error: null }, req.id));
  const fail = (req, code, message) => deliver(envelope("reply", req.type, { ok: false, result: null, error: { code, message } }, req.id));
  const emitFailed = (cmd, code, message) => deliver(envelope("event", "command.failed", { commandId: cmd.id, commandType: cmd.type, error: { code, message } }));
  const emitChanged = (cmd, songId, revision, kind, used = 0) => {
    if (dropChanged) { dropChanged = false; return; }
    deliver(envelope("event", "song.changed", { commandId: cmd.id, songId, revision, kind, usedInServices: used }));
  };

  const stamp = () => new Date("2026-11-25T10:00:00Z").toISOString();
  const summary = (s, fragment = null) => ({ id: s.id, title: s.title, author: s.author, updatedAt: s.updatedAt, fragment });
  const formatText = (s) => s.sections.map((x) => `[${x.label}]\n${x.text}`).join("\n\n");
  const detail = (s) => ({ ...s, text: formatText(s), usedInServices: usedIn[s.id] ?? 0 });
  const snapshot = (s) => JSON.parse(JSON.stringify({ title: s.title, author: s.author, sections: s.sections, arrangement: s.arrangement }));

  const normalizeSections = (input) => input.map((x) => ({ id: x.id ?? nextSectionId++, kind: kindOf(canonical(x.label)), label: canonical(x.label), text: x.text.trim() }));

  const addSong = ({ title, author = null, sections, arrangement, background = null, notes = null }) => {
    const s = {
      id: nextSongId++, title, author, copyright: null, ccliNumber: null, notes, revision: 1, createdAt: stamp(), updatedAt: stamp(), background,
      sections: sections.map((x) => ({ id: nextSectionId++, kind: x.kind ?? kindOf(x.label), label: x.label, text: x.text })),
      arrangement: arrangement ?? sections.map((_, i) => i),
    };
    songs.set(s.id, s);
    histories.set(s.id, []);
    return s;
  };
  for (const s of options.songs ?? []) addSong(s);

  const handleQuery = (req) => {
    queries.push(req);
    switch (req.type) {
      case "song.getSettings": return reply(req, state.settings);
      case "song.list": return reply(req, { truncated: false, songs: [...songs.values()].sort((a, b) => a.title.localeCompare(b.title)).map((s) => summary(s)) });
      case "song.search": {
        const q = req.payload.query.toLowerCase();
        const found = [...songs.values()].filter((s) => `${s.title} ${s.author ?? ""} ${s.sections.map((x) => x.text).join(" ")}`.toLowerCase().includes(q.split(" ")[0] ?? q));
        return reply(req, { query: req.payload.query, truncated: false, songs: found.slice(0, req.payload.limit).map((s) => summary(s, `«${q}»`)) });
      }
      case "song.get": {
        const s = songs.get(req.payload.id);
        return s ? reply(req, detail(s)) : fail(req, "song.notFound", "La canción no existe (puede que se haya borrado).");
      }
      case "song.history": {
        if (!songs.has(req.payload.songId)) return fail(req, "song.notFound", "La canción no existe.");
        return reply(req, { revisions: (histories.get(req.payload.songId) ?? []).slice().reverse().map((r) => ({ id: r.id, createdAt: r.createdAt, reason: r.reason, sectionCount: r.snap.sections.length })) });
      }
      case "song.parseText": return reply(req, fakeParse(req.payload.text));
      default: return fail(req, "unknown_message", req.type);
    }
  };

  const handleCommand = (cmd) => {
    commands.push(cmd);
    if (failure && failure.type === cmd.type) { const f = failure; failure = null; return emitFailed(cmd, f.code, f.message); }
    switch (cmd.type) {
      case "song.setSettings": state.settings = { ...cmd.payload }; return;
      // Comandos de la salida: se registran (los valida el backend real, aquí solo interesa lo que el panel manda).
      case "output.showContent": case "output.clearContent": case "output.setBackground": return;
      case "song.save": {
        const p = cmd.payload;
        if (p.title.trim() === "") return emitFailed(cmd, "song.invalidRequest", "Falta el título.");
        if (p.id === null) {
          const sections = normalizeSections(p.sections.map((x) => ({ ...x, id: null })));
          const s = { id: nextSongId++, title: p.title.trim(), author: p.author, copyright: p.copyright, ccliNumber: p.ccliNumber, notes: p.notes, revision: 1, createdAt: stamp(), updatedAt: stamp(), background: p.background, sections, arrangement: p.arrangement };
          songs.set(s.id, s);
          histories.set(s.id, []);
          return emitChanged(cmd, s.id, 1, "created");
        }
        const s = songs.get(p.id);
        if (!s) return emitFailed(cmd, "song.notFound", "La canción no existe (puede que se haya borrado).");
        if (s.revision !== p.expectedRevision) return emitFailed(cmd, "song.conflict", "La canción cambió mientras la editabas. Vuelve a abrirla para ver la versión actual.");
        histories.get(s.id).push({ id: nextRevisionId++, createdAt: stamp(), reason: p.reason ?? "save", snap: snapshot(s) });
        s.title = p.title.trim(); s.author = p.author; s.copyright = p.copyright; s.ccliNumber = p.ccliNumber; s.notes = p.notes; s.background = p.background;
        s.sections = normalizeSections(p.sections);
        s.arrangement = p.arrangement;
        s.revision += 1;
        return emitChanged(cmd, s.id, s.revision, "updated");
      }
      case "song.restoreRevision": {
        const { songId, revisionId, expectedRevision } = cmd.payload;
        const s = songs.get(songId);
        if (!s) return emitFailed(cmd, "song.notFound", "La canción no existe.");
        if (s.revision !== expectedRevision) return emitFailed(cmd, "song.conflict", "La canción cambió mientras la editabas.");
        const list = histories.get(songId);
        const rev = revisionId === null ? list.at(-1) : list.find((r) => r.id === revisionId);
        if (!rev) return emitFailed(cmd, "song.notFound", "No hay una copia previa que restaurar para esta canción.");
        list.push({ id: nextRevisionId++, createdAt: stamp(), reason: "restore", snap: snapshot(s) });
        s.title = rev.snap.title; s.author = rev.snap.author; s.sections = rev.snap.sections; s.arrangement = rev.snap.arrangement; s.revision += 1;
        return emitChanged(cmd, s.id, s.revision, "restored");
      }
      case "song.delete": {
        const s = songs.get(cmd.payload.id);
        if (!s) return emitFailed(cmd, "song.notFound", "La canción no existe.");
        songs.delete(s.id);
        return emitChanged(cmd, s.id, 0, "deleted", usedIn[s.id] ?? 0);
      }
      default: return emitFailed(cmd, "unknown_message", cmd.type);
    }
  };

  const transport = {
    send: (raw) => {
      const msg = JSON.parse(raw);
      // El backend responde de forma asíncrona (como la vida real), no dentro del propio send.
      queueMicrotask(() => {
        if (msg.kind === "query") return handleQuery(msg);
        if (holdNext) { holdNext = false; held.push(msg); return; }   // el comando «tarda»: no se procesa hasta release()
        handleCommand(msg);
      });
    },
    onMessage: (cb) => { deliver = cb; },
  };
  const client = new IpcClient(transport);
  return {
    client, state, songs, commands, queries, histories,
    sent: (type) => commands.filter((c) => c.type === type),
    failNext: (type, code, message) => { failure = { type, code, message }; },
    dropNextChanged: () => { dropChanged = true; },
    /** El PRÓXIMO comando queda retenido (el backend «tarda»); `release()` lo procesa. Sirve para editar mientras se guarda. */
    holdNext: () => { holdNext = true; },
    held,
    release: () => { while (held.length > 0) handleCommand(held.shift()); },
    addSong,
    /** Otra ventana guarda la canción: sube su revisión. */
    externalChange: (id) => { const s = songs.get(id); s.revision += 1; s.title += " (otro)"; },
  };
}

export const tick = (ms = 0) => new Promise((r) => setTimeout(r, ms));
export async function until(cond, ms = 2000) {
  const end = Date.now() + ms;
  while (Date.now() < end) {
    if (cond()) return;
    await tick(5);
  }
  throw new Error("la condición no se cumplió a tiempo");
}

/** Almacén de borrador en memoria (sustituye a localStorage). */
export function memoryDraftStorage() {
  let value = null;
  const writes = [];
  return { get: () => value, set: (v) => { value = v; writes.push(v); }, clear: () => { value = null; }, peek: () => value, writes };
}
