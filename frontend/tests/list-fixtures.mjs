// «Backend» SINTÉTICO de las listas de culto para los tests del panel: habla el protocolo REAL (sobres v13) con el cliente IPC real. TODA la letra y el texto bíblico
// están inventados aquí (palabras sueltas): nada real. Responde las lecturas que usa la pestaña «Lista» (playlist.*, song.get/getSettings, bible.chapter/getSettings) y
// REGISTRA los comandos; cada `output.showContent` se «confirma» como lo haría el backend real (evento `output.stateChanged` con el `origin` en el slide).
import { loadTs } from "./helper.mjs";
import { body } from "./song-fixtures.mjs";

const { IpcClient } = await loadTs("/src/ipc/client.ts");

const THEME = { fontFamily: "Arial, sans-serif", fontSize: 60, color: "#FFFFFF", outlineWidth: 2, outlineColor: "#000000", shadowBlur: 8, shadowOffset: 3, shadowColor: "#000000CC", align: "center", verticalAlign: "middle", lineHeight: 1.25, marginTop: 70, marginRight: 120, marginBottom: 70, marginLeft: 120, autoFit: true, minFontSize: 36 };
const BIBLE_THEME = { ...THEME, fontSize: 64, lineHeight: 1.3, minFontSize: 40 };

/** Una canción de n secciones de 1 línea cada una (cada sección cabe en una diapositiva y el orden natural da n diapositivas). */
export const songOf = (title, n = 3, extra = {}) => ({
  title,
  sections: Array.from({ length: n }, (_, i) => ({ label: i === 0 ? "Verso 1" : `Verso ${i + 1}`, text: body(i + 1, 1) })),
  ...extra,
});

export function createListBackend(options = {}) {
  const songs = new Map();
  const lists = new Map();
  const chapters = new Map();
  let nextSongId = 1;
  let nextListId = 1;
  let nextItemId = 1;
  const commands = [];
  const queries = [];
  let deliver = () => {};
  let seq = 1;
  let failure = null;
  const state = {
    playlistSettings: { onItemEnd: options.onItemEnd ?? "stay" },
    songSettings: { theme: THEME, transitionMs: 400, atEnd: "stay" },
    bibleSettings: { defaultBible: "TST", projectOnNavigate: false, superscriptNumbers: true, showAttribution: true, transitionMs: 280, theme: BIBLE_THEME, shortNames: [] },
    output: null,
    master: "live",
    window: { isOpen: true, isPaused: false },
  };

  const envelope = (kind, type, payload, id = crypto.randomUUID()) => JSON.stringify({ v: 13, id, kind, type, payload });
  const reply = (req, result) => deliver(envelope("reply", req.type, { ok: true, result, error: null }, req.id));
  const fail = (req, code, message) => deliver(envelope("reply", req.type, { ok: false, result: null, error: { code, message } }, req.id));
  const event = (type, payload) => deliver(envelope("event", type, payload));

  const addSong = ({ title, sections, arrangement, background = null }) => {
    const s = {
      id: nextSongId++, title, author: null, copyright: null, ccliNumber: null, notes: null, revision: 1, createdAt: "2026-11-25T10:00:00.000Z", updatedAt: "2026-11-25T10:00:00.000Z", background,
      sections: sections.map((x, i) => ({ id: i + 1, kind: "verse", label: x.label, text: x.text })),
      arrangement: arrangement ?? sections.map((_, i) => i), text: "", usedInServices: 0, usedInLists: [],
    };
    songs.set(s.id, s);
    return s;
  };
  const addChapter = (bible, bookNumber, chapter, verseTexts, attribution = "") => {
    chapters.set(`${bible}:${bookNumber}:${chapter}`, {
      bible, attribution, reference: `Libro ${bookNumber} ${chapter}`,
      verses: verseTexts.map((text, i) => ({ bookNumber, bookName: "Libro sintético", chapter, verseNumber: i + 1, text })),
    });
  };

  /** Una lista con elementos: `{ song: id }`, `{ passage: [book, chapter, start, end?, bible] }` o `{ note: "título", text?, clear? }`. */
  const addList = ({ name, date = null, items = [] }) => {
    const list = { id: nextListId++, name, serviceDate: date, notes: "", revision: 1, createdAt: "2026-11-25T10:00:00.000Z", updatedAt: "2026-11-25T10:00:00.000Z", items: [] };
    lists.set(list.id, list);
    for (const spec of items) addItem(list.id, spec);
    return list;
  };
  const addItem = (listId, spec) => {
    const list = lists.get(listId);
    const base = { id: nextItemId++, position: list.items.length, songId: null, bookNumber: null, chapter: null, verseStart: null, verseEnd: null, bibleAbbreviation: null, note: null, clearOnActivate: false, resolution: null, offeredBible: null, isMissing: false };
    let item;
    if (spec.song !== undefined) item = { ...base, itemType: "song", songId: spec.song, title: spec.title ?? songs.get(spec.song)?.title ?? "Canción" };
    else if (spec.passage) { const [b, c, s, e, bible] = spec.passage; item = { ...base, itemType: "passage", title: spec.title ?? `Libro ${b} ${c}:${s}${e ? `-${e}` : ""}`, bookNumber: b, chapter: c, verseStart: s, verseEnd: e ?? null, bibleAbbreviation: bible, resolution: spec.resolution ?? "resolved" }; }
    else item = { ...base, itemType: "note", title: spec.note, note: spec.text ?? null, clearOnActivate: spec.clear ?? false };
    list.items.push(item);
    return item;
  };
  const view = (list) => {
    const items = list.items.map((it, i) => {
      const missing = it.itemType === "song" && !songs.has(it.songId);
      const projectable = it.itemType === "song" ? !missing : it.itemType === "passage" ? it.resolution === "resolved" : false;
      return { ...it, position: i, title: it.itemType === "song" && !missing ? songs.get(it.songId).title : it.title, isMissing: missing, projectable };
    });
    return { id: list.id, name: list.name, serviceDate: list.serviceDate, notes: list.notes, revision: list.revision, createdAt: list.createdAt, updatedAt: list.updatedAt, missingCount: items.filter((i) => i.isMissing).length, items };
  };

  const outputPayload = () => ({
    seq: seq++,
    state: {
      schemaVersion: 2, revision: 0, master: state.master,
      background: { kind: "color", color: "#102030", mediaUrl: null, fileName: null, muted: true, transitionMs: 0, warning: null, retry: 0 },
      content: state.slide ? { slide: state.slide, transitionMs: state.transitionMs ?? 0 } : { slide: null, transitionMs: 0 },
      overlays: { items: [] },
    },
    window: state.window,
  });
  /** Publica el estado de la salida (lo que el backend confirmaría). */
  const confirm = () => { state.output = outputPayload(); event("output.stateChanged", state.output); return state.output; };

  const handleQuery = (req) => {
    queries.push(req);
    switch (req.type) {
      case "playlist.getSettings": return reply(req, state.playlistSettings);
      case "song.getSettings": return reply(req, state.songSettings);
      case "bible.getSettings": return reply(req, state.bibleSettings);
      case "playlist.list": return reply(req, { truncated: false, playlists: [...lists.values()].map((l) => { const v = view(l); return { id: l.id, name: l.name, serviceDate: l.serviceDate, itemCount: v.items.length, missingCount: v.missingCount, revision: l.revision }; }) });
      case "playlist.get": { const l = lists.get(req.payload.id); return l ? reply(req, view(l)) : fail(req, "playlist.notFound", "La lista no existe (puede que se haya borrado)."); }
      case "song.get": { const s = songs.get(req.payload.id); return s ? reply(req, s) : fail(req, "song.notFound", "La canción no existe (puede que se haya borrado)."); }
      case "bible.chapter": {
        const c = chapters.get(`${req.payload.bible}:${req.payload.bookNumber}:${req.payload.chapter}`);
        return c ? reply(req, c) : fail(req, "bible.notFound", "Esa Biblia no está instalada.");
      }
      default: return fail(req, "unknown_message", req.type);
    }
  };

  let slideCounter = 0;
  const handleCommand = (cmd) => {
    commands.push(cmd);
    if (failure && failure.type === cmd.type) {
      const f = failure; failure = null;
      return event("command.failed", { commandId: cmd.id, commandType: cmd.type, error: { code: f.code, message: f.message } });
    }
    switch (cmd.type) {
      case "output.showContent":
        state.slide = { id: `s${++slideCounter}`, blocks: cmd.payload.blocks.map((text) => ({ text })), theme: cmd.payload.theme, origin: cmd.payload.origin ?? null };
        state.transitionMs = cmd.payload.transitionMs;
        if (options.autoConfirm !== false) confirm();
        return;
      case "output.clearContent": state.slide = null; if (options.autoConfirm !== false) confirm(); return;
      case "playlist.setSettings": state.playlistSettings = { ...cmd.payload }; return;
      default: return;
    }
  };

  const transport = {
    send: (raw) => {
      const msg = JSON.parse(raw);
      queueMicrotask(() => { if (msg.kind === "query") handleQuery(msg); else handleCommand(msg); });
    },
    onMessage: (cb) => { deliver = cb; },
  };
  const client = new IpcClient(transport);

  return {
    client, state, songs, lists, commands, queries,
    addSong, addChapter, addList, addItem,
    sent: (type) => commands.filter((c) => c.type === type),
    shows: () => commands.filter((c) => c.type === "output.showContent"),
    confirm,
    /** El estado de salida que vería el panel (el último confirmado). */
    output: () => state.output,
    setMaster: (master) => { state.master = master; return confirm(); },
    setWindow: (w) => { state.window = { ...state.window, ...w }; return confirm(); },
    /** Otro panel o la Biblia proyectan algo que NO viene de una lista. */
    showForeign: (text) => { state.slide = { id: `f${++slideCounter}`, blocks: [{ text }], theme: THEME, origin: null }; return confirm(); },
    /** El estado trae un origen a mano (de otra lista, de un elemento que no existe…). */
    showOrigin: (origin, text = "texto sintético") => { state.slide = { id: `o${++slideCounter}`, blocks: [{ text }], theme: THEME, origin }; return confirm(); },
    event,
    removeSong: (id) => { songs.delete(id); event("song.changed", { commandId: "x", songId: id, revision: 0, kind: "deleted", usedInServices: 1, usedInLists: [] }); },
    failNext: (type, code, message) => { failure = { type, code, message }; },
  };
}

export const fakeFit = () => ({ fits: (blocks) => blocks.reduce((n, b) => n + b.split("\n").reduce((m, l) => m + Math.max(1, Math.ceil(l.length / 40)), 0), 0) <= 6, dispose: () => undefined });
