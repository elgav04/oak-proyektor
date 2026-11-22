// Datos y «backend» SINTÉTICOS para los tests de la pestaña de Biblia: libros y versículos INVENTADOS (ningún texto bíblico). El backend falso
// habla el protocolo real (sobre v9) con el cliente IPC real, así que los tests también comprueban los mensajes tal como salen a C#.
import { loadTs } from "./helper.mjs";

const { IpcClient } = await loadTs("/src/ipc/client.ts");

export const BOOKS = [
  { number: 1, name: "Alfa", short: "Alf", aliases: ["alfa", "alf"] },
  { number: 2, name: "Beta Mayor", short: "Bet", aliases: ["beta", "bet"] },
  { number: 3, name: "Gamma", short: "Gam", aliases: ["gamma", "gam"] },
];

const WORDS = ["lumen", "arto", "senda", "viru", "tolen", "mara", "pelo", "dune", "karo", "nilo", "sobe", "tira"];
// Gamma 2 (120 versículos) y Gamma 3 (250) existen para probar selecciones y rangos grandes.
export const CHAPTER_SIZES = { "1:1": 12, "1:2": 4, "2:1": 6, "3:1": 3, "3:2": 120, "3:3": 250 };

/** Texto inventado y determinista de un versículo; `long` = versículo largo (varias frases). */
export function verseText(book, chapter, verse, sentences = 1) {
  let out = "";
  for (let s = 0; s < sentences; s++) {
    const words = [];
    for (let i = 0; i < 7; i++) words.push(WORDS[(book * 7 + chapter * 5 + verse * 3 + s * 11 + i * 2) % WORDS.length]);
    out += (out ? " " : "") + words[0].toUpperCase().charAt(0) + words.join(" ").slice(1) + (s % 2 === 0 ? ", " + WORDS[(verse + s) % WORDS.length] + "." : ".");
  }
  return out;
}

const sentencesOf = (book, chapter, verse, longVerses) => (longVerses.has(`${book}:${chapter}:${verse}`) ? 14 : 1);

/**
 * Backend falso. `options.longVerses` = conjunto «libro:cap:ver» de versículos largos. Devuelve el transporte para `IpcClient`, los comandos recibidos,
 * las consultas recibidas y el estado (ajustes, historial) que el «backend» guarda.
 */
export function createFakeBackend(options = {}) {
  const longVerses = options.longVerses ?? new Set();
  const state = {
    bibles: [{ id: 1, abbreviation: "SIN", name: "Biblia sintética", language: "es", attribution: options.attribution ?? "", numberingProfile: null, books: 3, verses: 25 }],
    settings: {
      defaultBible: null, projectOnNavigate: false, superscriptNumbers: true, showAttribution: false, transitionMs: 280,
      theme: { fontFamily: "Arial, sans-serif", fontSize: 64, color: "#FFFFFF", outlineWidth: 2, outlineColor: "#000000", shadowBlur: 8, shadowOffset: 3, shadowColor: "#000000CC", align: "center", verticalAlign: "middle", lineHeight: 1.3, marginTop: 70, marginRight: 120, marginBottom: 70, marginLeft: 120, autoFit: true, minFontSize: 40 },
      shortNames: [],
      ...(options.settings ?? {}),
    },
    history: [],
    nextHistoryId: 1,
  };
  const commands = [];
  const queries = [];
  let deliver = () => {};

  const verseDto = (b, c, v) => ({ bookNumber: b.number, bookName: b.name, chapter: c, verseNumber: v, text: verseText(b.number, c, v, sentencesOf(b.number, c, v, longVerses)) });
  const chapterVerses = (b, c) => Array.from({ length: CHAPTER_SIZES[`${b.number}:${c}`] ?? 0 }, (_, i) => verseDto(b, c, i + 1));
  const findBook = (token) => BOOKS.find((b) => b.aliases.some((a) => a.startsWith(token.toLowerCase())) || b.name.toLowerCase().startsWith(token.toLowerCase()));
  const reply = (req, result) => deliver(JSON.stringify({ v:9, id: req.id, kind: "reply", type: req.type, payload: { ok: true, result, error: null } }));
  const fail = (req, code, message) => deliver(JSON.stringify({ v:9, id: req.id, kind: "reply", type: req.type, payload: { ok: false, result: null, error: { code, message } } }));

  const passage = (req, record) => {
    // Como el backend real (H4): un rango de capítulos enorme se RECHAZA con bible.rangeTooLarge (nunca se recorta).
    const chapterRange = /^\s*\S+\s+(\d+)\s*-\s*(\d+)\s*$/.exec(req.payload.reference);
    if (chapterRange && Number(chapterRange[2]) - Number(chapterRange[1]) >= 2) return fail(req, "bible.rangeTooLarge", "Rango demasiado grande: tiene más de 200 versículos. Acótalo (por ejemplo, un capítulo o unos pocos versículos).");
    const m = /^\s*([^\d\s]+(?:\s+[^\d\s]+)?)\s*(\d+)(?:\s*[:.]\s*(\d+)(?:\s*-\s*(\d+))?)?\s*$/.exec(req.payload.reference);
    const book = m && findBook(m[1].trim().split(/\s+/)[0]);
    if (!m || !book) return fail(req, "bible.invalidReference", "No se entiende la referencia.");
    const chapter = Number(m[2]);
    const all = chapterVerses(book, chapter);
    if (all.length === 0) return fail(req, "bible.passageNotFound", "No existe ese pasaje.");
    const from = m[3] ? Number(m[3]) : 1;
    const to = m[4] ? Number(m[4]) : m[3] ? from : all.length;
    const verses = all.filter((v) => v.verseNumber >= from && v.verseNumber <= to);
    if (record) {
      const first = verses[0]; const last = verses[verses.length - 1];
      const label = first.verseNumber === last.verseNumber ? `${book.name} ${chapter}:${first.verseNumber}` : `${book.name} ${chapter}:${first.verseNumber}-${last.verseNumber}`;
      state.history.unshift({ id: state.nextHistoryId++, bible: "SIN", bookNumber: book.number, bookName: book.name, chapter, verseStart: first.verseNumber, verseEnd: last.verseNumber, label, citedAt: "2026-01-01T00:00:00Z" });
    }
    return reply(req, { bible: "SIN", attribution: state.bibles[0].attribution, reference: req.payload.reference, verses });
  };

  const handleQuery = (req) => {
    queries.push(req);
    switch (req.type) {
      case "bible.list": return reply(req, { bibles: state.bibles });
      case "bible.getSettings": return reply(req, state.settings);
      case "bible.history": return reply(req, { entries: state.history.slice(0, 30) });
      case "bible.suggest": {
        const p = req.payload.prefix.toLowerCase();
        return reply(req, { suggestions: BOOKS.filter((b) => b.aliases.some((a) => a.startsWith(p))).map((b) => ({ bookNumber: b.number, name: b.name, shortName: b.short, matchedAlias: b.aliases[0] })) });
      }
      case "bible.lookup": return passage(req, req.payload.record);
      case "bible.chapter": {
        const book = BOOKS.find((b) => b.number === req.payload.bookNumber);
        const verses = book ? chapterVerses(book, req.payload.chapter) : [];
        return verses.length === 0 ? fail(req, "bible.passageNotFound", "No existe ese pasaje.") : reply(req, { bible: "SIN", attribution: state.bibles[0].attribution, reference: "", verses });
      }
      case "bible.search": {
        const q = req.payload.query.toLowerCase();
        const hits = [];
        for (const b of BOOKS) for (const key of Object.keys(CHAPTER_SIZES)) {
          const [bn, c] = key.split(":").map(Number);
          if (bn !== b.number) continue;
          for (const v of chapterVerses(b, c)) if (v.text.toLowerCase().includes(q)) hits.push({ bible: "SIN", bookNumber: b.number, bookName: b.name, chapter: c, verseNumber: v.verseNumber, fragment: v.text });
        }
        return reply(req, { query: req.payload.query, truncated: false, hits: hits.slice(0, req.payload.limit) });
      }
      default: return fail(req, "unknown_message", req.type);
    }
  };

  const transport = {
    send: (raw) => {
      const msg = JSON.parse(raw);
      // El backend responde de forma asíncrona (como la vida real), no dentro del propio send.
      queueMicrotask(() => {
        if (msg.kind === "query") return handleQuery(msg);
        commands.push(msg);
        if (msg.type === "bible.setSettings") state.settings = { ...msg.payload };
        if (msg.type === "bible.setAttribution") state.bibles[0].attribution = msg.payload.attribution;
      });
    },
    onMessage: (cb) => { deliver = cb; },
  };
  const client = new IpcClient(transport);
  return {
    client, state, commands, queries,
    sent: (type) => commands.filter((c) => c.type === type),
    lastContent: () => commands.filter((c) => c.type === "output.showContent").at(-1)?.payload,
  };
}

export const tick = (ms = 0) => new Promise((r) => setTimeout(r, ms));
/** Espera (con tope) a que se cumpla una condición. */
export async function until(cond, ms = 2000) {
  const end = Date.now() + ms;
  while (Date.now() < end) {
    if (cond()) return;
    await tick(5);
  }
  throw new Error("la condición no se cumplió a tiempo");
}
