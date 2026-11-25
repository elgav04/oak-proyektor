import { createStore, type StoreApi } from "zustand/vanilla";
import type {
  CommandPayloads,
  CommandType,
  EventPayloads,
  EventType,
  QueryPayloads,
  QueryType,
  SongBackgroundDto,
  SongChangedPayload,
  SongParseTextPayload,
  SongPayload,
  SongRevisionDto,
  SongSettingsPayload,
  SongSummaryDto,
  TextTheme,
} from "@/ipc/protocol";
import { createLatestSender, realClock, type Clock } from "../bible/latest";
import { createBlocksMeasurer } from "../bible/measure";
import { projectionTheme } from "../bible/slide";
import { SONG_DEFAULT_SETTINGS } from "./defaults";
import { decodeSongFile, MAX_FILE_BYTES, type ForcedEncoding } from "./fileDecoder";
import { deriveSlides, paginateChunk, uniqueChunks, warmChunks, type SongSlide } from "./slides";
import {
  buildSavePayload,
  fingerprint,
  formatSectionsText,
  isNaturalOrder,
  labelKey,
  naturalOrder,
  orderIndexes,
  reconcileOrder,
  reconcileSections,
  repeatedEdits,
  saveButtonLabel,
  unresolvedOrder,
  type EditableSong,
  type EditorSection,
  type OrderEntry,
} from "./textModel";

/** Lo mínimo del cliente IPC que usa la pestaña (así se prueba con uno falso). `send` devuelve el id del comando: `song.changed` lo trae como `commandId`. */
export interface SongApi {
  request<T extends QueryType>(type: T, payload: QueryPayloads[T]["request"], timeoutMs?: number): Promise<QueryPayloads[T]["reply"]>;
  send<T extends CommandType>(type: T, payload: CommandPayloads[T]): string;
  subscribe<T extends EventType>(type: T, handler: (payload: EventPayloads[T]) => void): () => void;
}

/** Dónde se guarda el borrador sin guardar (por si la página se recarga). Opcional y de mejor esfuerzo. */
export interface DraftStorage {
  get(): string | null;
  set(value: string): void;
  clear(): void;
}

export type BlocksFit = (blocks: readonly string[]) => boolean;

export interface SongControllerDeps {
  api: SongApi;
  createFit?: (theme: TextTheme) => { fits: BlocksFit; dispose: () => void };
  clock?: Clock;
  storage?: DraftStorage | null;
  searchDebounceMs?: number;
  parseDebounceMs?: number;
  slidesDelayMs?: number;
  draftDelayMs?: number;
  saveTimeoutMs?: number;
  /** Trabajo máximo seguido al calcular diapositivas antes de ceder el hilo (ms). */
  sliceBudgetMs?: number;
  yieldNow?: () => Promise<void>;
  /** Mínimo entre dos envíos de proyección al navegar con las teclas (la última siempre llega). */
  sendIntervalMs?: number;
  /** Cuánto tarda en cargarse la tira al moverse por la lista con las flechas. */
  selectDelayMs?: number;
  now?: () => number;
  newUid?: () => string;
}

export interface ParseInfo {
  detected: string;
  warnings: string[];
  errors: string[];
  /** El texto trae repeticiones u «Orden:»: en el editor el orden se edita aparte, así que se ignoran (y se avisa). */
  orderIgnored: boolean;
  /** Se está interpretando (el resultado aún no es el del texto actual). */
  stale: boolean;
}

export interface EditorState {
  songId: number | null;
  revision: number | null;
  /** El contenido viene de una importación (el motivo de la copia previa será «import»). */
  fromImport: boolean;
  title: string;
  author: string;
  copyright: string;
  ccli: string;
  notes: string;
  background: SongBackgroundDto | null;
  text: string;
  sections: EditorSection[];
  order: OrderEntry[];
  /** Lo que había al abrir o al guardar por última vez (para «sin guardar» y para el «×N» del botón). */
  baselineSections: EditorSection[];
  baselinePrint: string;
  parse: ParseInfo;
  slides: SongSlide[] | null;
  slidesBusy: boolean;
  selectedOrder: number;
  selectedSlide: number;
  usedInServices: number;
  history: SongRevisionDto[] | null;
  historyOpen: boolean;
}

export interface ImportDraft {
  text: string;
  fileName: string | null;
  bytes: Uint8Array | null;
  encoding: string | null;
  forced: ForcedEncoding;
  decodeWarnings: string[];
  fileError: string | null;
  merge: boolean;
  title: string;
  parsed: SongParseTextPayload | null;
  parsing: boolean;
  parseError: string | null;
}

export interface RecoveredDraft {
  songId: number | null;
  title: string;
}

/**
 * La canción SELECCIONADA para proyectar (distinta de la que se edita). `cursor` = diapositiva seleccionada (estado de VISTA: la que se proyectará con Enter);
 * `requested` = la última que se PIDIÓ proyectar (null = todavía no se pidió nada de esta canción). Qué está EN PANTALLA no se guarda aquí: sale del estado que
 * confirma el backend (`liveStatus.ts`).
 */
export interface ProjectionState {
  songId: number;
  title: string;
  revision: number;
  background: SongBackgroundDto | null;
  slides: SongSlide[] | null;
  busy: boolean;
  cursor: number;
  requested: number | null;
  /** Se pulsó «siguiente» en la última diapositiva (la canción se queda en ella: «FIN»). */
  atEnd: boolean;
  error: string | null;
}

export interface SongViewState {
  loaded: boolean;
  settings: SongSettingsPayload | null;
  mode: "list" | "editor";
  screen: "main" | "theme";
  list: SongSummaryDto[];
  listTruncated: boolean;
  query: string;
  results: SongSummaryDto[] | null;
  resultsTruncated: boolean;
  listIndex: number;
  editor: EditorState | null;
  projection: ProjectionState | null;
  saving: boolean;
  conflict: boolean;
  confirmLeave: boolean;
  deleteAsk: { id: number; title: string; used: number } | null;
  importDraft: ImportDraft | null;
  recovered: RecoveredDraft | null;
  message: string | null;
}

const initialState: SongViewState = {
  loaded: false,
  settings: null,
  mode: "list",
  screen: "main",
  list: [],
  listTruncated: false,
  query: "",
  results: null,
  resultsTruncated: false,
  listIndex: 0,
  editor: null,
  projection: null,
  saving: false,
  conflict: false,
  confirmLeave: false,
  deleteAsk: null,
  importDraft: null,
  recovered: null,
  message: null,
};

const errorMessage = (error: unknown): string => (error instanceof Error ? error.message : typeof error === "object" && error !== null && "message" in error ? String(error.message) : String(error));
const errorCode = (error: unknown): string => (typeof error === "object" && error !== null && "code" in error ? String(error.code) : "");

const emptyParse: ParseInfo = { detected: "empty", warnings: [], errors: [], orderIgnored: false, stale: false };

/** Lo editable de la canción abierta. */
export function editableOf(e: EditorState): EditableSong {
  return { title: e.title, author: e.author, copyright: e.copyright, ccli: e.ccli, notes: e.notes, background: e.background, sections: e.sections, order: e.order };
}

export function isDirty(e: EditorState | null): boolean {
  return e !== null && fingerprint(editableOf(e)) !== e.baselinePrint;
}

/** Texto del botón principal de guardar para el editor abierto («Guardar en la canción (Coro ×3)»). */
export function saveLabel(e: EditorState): string {
  return saveButtonLabel(e.songId === null, repeatedEdits(e.sections, e.order, e.baselineSections));
}

/** Por qué no se puede guardar ahora (null = se puede). */
export function saveBlocker(e: EditorState): string | null {
  if (e.title.trim().length === 0) return "Falta el título.";
  if (e.parse.errors.length > 0) return e.parse.errors[0] ?? "El texto tiene errores.";
  if (unresolvedOrder(e.order, e.sections).length > 0) return "El orden hace referencia a una sección que ya no existe: quita ese elemento o vuelve a escribir su etiqueta.";
  if (e.sections.length > 0 && e.order.length === 0) return "El orden está vacío: añade al menos una sección.";
  return null;
}

export interface SongController {
  store: StoreApi<SongViewState>;
  init: () => Promise<void>;
  refreshList: () => Promise<void>;
  setQuery: (query: string) => void;
  moveList: (delta: number) => void;
  /** Clic en una canción de la lista: la selecciona y muestra su tira de diapositivas (NO proyecta nada). */
  selectIndex: (index: number) => void;
  /** Enter: proyecta la diapositiva seleccionada. */
  projectCursor: () => void;
  /** Clic en una diapositiva de la tira: la selecciona y la proyecta. */
  projectSlide: (index: number) => void;
  nextSlide: () => void;
  prevSlide: () => void;
  /** Esc: quita el texto de la pantalla (`output.clearContent`) y descarta lo pendiente. */
  clearProjection: () => void;
  openSelected: () => void;
  openSong: (id: number) => void;
  newSong: () => void;
  duplicate: (id: number) => void;
  askDelete: (id?: number) => void;
  cancelDelete: () => void;
  confirmDelete: () => Promise<void>;

  setField: (field: "title" | "author" | "copyright" | "ccli" | "notes", value: string) => void;
  setBackground: (background: SongBackgroundDto | null) => void;
  setText: (text: string) => void;
  selectOrder: (index: number) => void;
  moveOrder: (from: number, to: number) => void;
  removeOrder: (index: number) => void;
  repeatOrder: (index: number) => void;
  insertSection: (uid: string) => void;
  selectSlide: (index: number) => void;

  save: () => Promise<boolean>;
  saveAndClose: () => Promise<void>;
  closeEditor: () => void;
  resolveLeave: (choice: "save" | "discard" | "stay") => Promise<void>;
  openHistory: () => Promise<void>;
  closeHistory: () => void;
  undoLastSave: () => Promise<void>;
  restoreVersion: (revisionId: number) => Promise<void>;
  resolveConflict: (choice: "reload" | "saveCopy") => Promise<void>;

  openImport: (initialText?: string) => void;
  closeImport: () => void;
  importSetText: (text: string) => void;
  importSetTitle: (title: string) => void;
  importSetMerge: (merge: boolean) => void;
  importFile: (file: { name: string; size: number; arrayBuffer: () => Promise<ArrayBuffer> }) => Promise<void>;
  importForceEncoding: (encoding: ForcedEncoding) => void;
  applyImport: () => void;

  openTheme: () => void;
  closeTheme: () => void;
  saveSettings: (next: SongSettingsPayload) => Promise<void>;

  openRecovered: () => Promise<void>;
  discardRecovered: () => void;
  dismissMessage: () => void;
  dispose: () => void;
}

/** Fallo de un comando de escritura (`command.failed`, o sin respuesta): lleva el código del backend (`song.conflict`, `song.busy`…). */
class CommandError extends Error {
  constructor(readonly code: string, message: string) {
    super(message);
    this.name = "CommandError";
  }
}

interface Waiter {
  resolve: (changed: SongChangedPayload) => void;
  reject: (error: Error) => void;
  timer: unknown;
}

const DRAFT_VERSION = 1;

export function createSongController(deps: SongControllerDeps): SongController {
  const { api } = deps;
  const clock = deps.clock ?? realClock;
  const now = deps.now ?? (() => (typeof performance !== "undefined" ? performance.now() : Date.now()));
  const yieldNow = deps.yieldNow ?? (() => new Promise<void>((resolve) => { setTimeout(resolve, 0); }));
  let uidCounter = 0;
  const newUid = deps.newUid ?? (() => `u${String(++uidCounter)}`);
  const store = createStore<SongViewState>(() => ({ ...initialState }));
  const get = store.getState;
  const set = store.setState;
  const life = { disposed: false };
  const gone = (): boolean => life.disposed;
  const theme = (): TextTheme => (get().settings ?? SONG_DEFAULT_SETTINGS).theme;

  const patch = (changes: Partial<EditorState>): void => {
    set((s) => (s.editor ? { editor: { ...s.editor, ...changes } } : s));
  };

  // ───────── medición y diapositivas derivadas ─────────

  const themeKey = (t: TextTheme): string => JSON.stringify([t.fontFamily, t.fontSize, t.lineHeight, t.outlineWidth, t.shadowBlur, t.shadowOffset, t.marginTop, t.marginBottom, t.marginLeft, t.marginRight, t.align]);
  let fitCache: { key: string; fits: BlocksFit; dispose: () => void } | null = null;
  const pagesCache = new Map<string, string[]>();
  const fitFor = (t: TextTheme): BlocksFit => {
    const key = themeKey(t);
    if (fitCache?.key === key) return fitCache.fits;
    fitCache?.dispose();
    const made = (deps.createFit ?? ((th) => {
      const m = createBlocksMeasurer(th);
      return { fits: (blocks: readonly string[]) => m.fits(blocks), dispose: () => { m.dispose(); } };
    }))(t);
    fitCache = { key, ...made };
    pagesCache.clear();
    return made.fits;
  };

  let slidesToken = 0;
  let slidesTimer: unknown = null;

  /** Diapositivas de un trozo, memorizadas (el editor y la proyección comparten la memoria: un coro se mide una vez). */
  const makePagesOf = (fits: BlocksFit) => (chunk: string): string[] => {
    let pages = pagesCache.get(chunk);
    if (!pages) {
      pages = paginateChunk(chunk, (text) => fits([text]));
      if (pagesCache.size > 600) pagesCache.clear();
      pagesCache.set(chunk, pages);
    }
    return pages;
  };

  const computeSlides = async (token: number): Promise<void> => {
    const e = get().editor;
    if (!e || gone()) return;
    const fits = fitFor(theme());
    const indexes = e.order.map((entry) => e.sections.findIndex((s) => s.uid === entry.uid)).filter((i) => i >= 0);
    const sources = e.sections.map((s) => ({ uid: s.uid, label: s.label, text: s.text }));
    const pagesOf = makePagesOf(fits);
    const done = await warmChunks(uniqueChunks(sources, indexes), pagesOf, {
      budgetMs: deps.sliceBudgetMs ?? 12,
      now,
      yieldNow,
      cancelled: () => token !== slidesToken || gone(),
    });
    if (!done || token !== slidesToken || gone()) return;
    const slides = deriveSlides(sources, indexes, pagesOf);
    patch({ slides, slidesBusy: false, selectedSlide: Math.min(get().editor?.selectedSlide ?? 0, Math.max(0, slides.length - 1)) });
  };

  /** Recalcula las diapositivas cuando el editor deja de cambiar (el cálculo corre en tandas: nunca atasca el panel). */
  const scheduleSlides = (): void => {
    const e = get().editor;
    if (!e || gone()) return;
    const token = ++slidesToken;
    if (slidesTimer !== null) clock.clearTimeout(slidesTimer);
    patch({ slidesBusy: true });
    slidesTimer = clock.setTimeout(() => {
      slidesTimer = null;
      void computeSlides(token).catch(() => { if (token === slidesToken) patch({ slidesBusy: false }); });
    }, deps.slidesDelayMs ?? 150);
  };

  // ───────── borrador sin guardar ─────────

  let draftTimer: unknown = null;
  const writeDraft = (): void => {
    const storage = deps.storage;
    const e = get().editor;
    if (!storage || gone()) return;
    if (!e || !isDirty(e)) { storage.clear(); return; }
    // El texto escrito aún no se ha interpretado: las secciones guardadas serían las de antes. Se espera a que termine (el borrador nunca es más viejo que el texto).
    if (e.parse.stale || parseDirty) { scheduleDraft(); return; }
    try {
      storage.set(JSON.stringify({
        v: DRAFT_VERSION, songId: e.songId, revision: e.revision, fromImport: e.fromImport,
        title: e.title, author: e.author, copyright: e.copyright, ccli: e.ccli, notes: e.notes, background: e.background, text: e.text,
        sections: e.sections.map((s) => ({ serverId: s.serverId, kind: s.kind, label: s.label, text: s.text })),
        order: orderIndexes(e.order, e.sections) ?? [],
      }));
    } catch { /* el borrador es una comodidad: si no se puede guardar, no pasa nada */ }
  };
  function scheduleDraft(): void {
    if (!deps.storage || gone()) return;
    if (draftTimer !== null) clock.clearTimeout(draftTimer);
    draftTimer = clock.setTimeout(() => { draftTimer = null; writeDraft(); }, deps.draftDelayMs ?? 400);
  }

  // ───────── interpretar el texto (backend) ─────────

  let parseTimer: unknown = null;
  let parseToken = 0;
  let parseDirty = false;
  let parseInFlight: Promise<void> | null = null;

  const isNaturalArrangement = (arrangement: readonly number[], count: number): boolean => arrangement.length === count && arrangement.every((v, i) => v === i);

  const parseNow = (): Promise<void> => {
    const e = get().editor;
    if (!e) return Promise.resolve();
    const token = ++parseToken;
    const text = e.text;
    parseDirty = false;
    const run = (async (): Promise<void> => {
      try {
        const reply = await api.request("song.parseText", { text, mergeIdenticalBlocks: true });
        if (gone() || token !== parseToken) return;
        const cur = get().editor;
        if (!cur) return;
        const sections = reconcileSections(reply.sections, cur.sections, newUid);
        const order = reconcileOrder(cur.order, cur.sections, sections);
        patch({
          sections,
          order,
          parse: { detected: reply.detected, warnings: reply.warnings, errors: reply.errors, orderIgnored: !isNaturalArrangement(reply.arrangement, sections.length), stale: false },
        });
        scheduleSlides();
        scheduleDraft();
      } catch (error) {
        if (gone() || token !== parseToken) return;
        patch({ parse: { ...(get().editor?.parse ?? emptyParse), errors: [`No se pudo interpretar el texto: ${errorMessage(error)}`], stale: false } });
      }
    })();
    parseInFlight = run.finally(() => { if (parseInFlight === run) parseInFlight = null; });
    return parseInFlight;
  };

  const scheduleParse = (): void => {
    if (gone()) return;
    parseDirty = true;
    if (parseTimer !== null) clock.clearTimeout(parseTimer);
    patch({ parse: { ...(get().editor?.parse ?? emptyParse), stale: true } });
    parseTimer = clock.setTimeout(() => { parseTimer = null; void parseNow(); }, deps.parseDebounceMs ?? 150);
  };

  /** Antes de guardar: el texto escrito hasta ahora tiene que estar interpretado. */
  const flushParse = async (): Promise<void> => {
    if (parseTimer !== null) { clock.clearTimeout(parseTimer); parseTimer = null; }
    if (parseDirty) await parseNow();
    else if (parseInFlight) await parseInFlight;
  };

  // ───────── resultados de los comandos de escritura ─────────

  const waiters = new Map<string, Waiter>();
  const recentChanged = new Map<string, SongChangedPayload>();
  const recentFailed = new Map<string, CommandError>();
  const remember = <T,>(map: Map<string, T>, id: string, value: T): void => {
    map.set(id, value);
    if (map.size > 50) { const first = map.keys().next().value; if (first !== undefined) map.delete(first); }
  };
  let listRefreshTimer: unknown = null;
  const unsubscribers: (() => void)[] = [];

  const sendAndWait = <T extends CommandType>(type: T, payload: CommandPayloads[T]): Promise<SongChangedPayload> =>
    new Promise((resolve, reject) => {
      const id = api.send(type, payload);
      const earlyOk = recentChanged.get(id);
      if (earlyOk) { resolve(earlyOk); return; }
      const earlyFail = recentFailed.get(id);
      if (earlyFail) { reject(earlyFail); return; }
      const timer = clock.setTimeout(() => {
        waiters.delete(id);
        reject(new CommandError("timeout", "El programa no confirmó que se guardara. Revisa el estado de la salida y vuelve a intentarlo: tus cambios siguen en el editor."));
      }, deps.saveTimeoutMs ?? 20000);
      waiters.set(id, { resolve, reject, timer });
    });

  const onChanged = (p: SongChangedPayload): void => {
    remember(recentChanged, p.commandId, p);
    notifyProjection(p);
    const w = waiters.get(p.commandId);
    if (w) {
      clock.clearTimeout(w.timer);
      waiters.delete(p.commandId);
      w.resolve(p);
      return;
    }
    // Un cambio que no pedimos nosotros (otra ventana, la consola): solo se refresca la lista.
    if (listRefreshTimer !== null) clock.clearTimeout(listRefreshTimer);
    listRefreshTimer = clock.setTimeout(() => { listRefreshTimer = null; void controller.refreshList(); }, 200);
  };

  const onFailed = (p: EventPayloads["command.failed"]): void => {
    if (p.commandId === backgroundCommandId) appliedBackground = null;   // el fondo no se pudo aplicar: la próxima proyección de la canción lo reintenta
    const error = new CommandError(p.error.code, p.error.message);
    remember(recentFailed, p.commandId, error);
    const w = waiters.get(p.commandId);
    if (!w) return;
    clock.clearTimeout(w.timer);
    waiters.delete(p.commandId);
    w.reject(error);
  };

  // ───────── proyección (la canción SELECCIONADA en la lista) ─────────

  /** Las diapositivas se mandan por el carril en vivo con «el último gana»: una ráfaga de teclas muestra siempre la última. NO se escribe nada en la BD. */
  const sender = createLatestSender<{ text: string }>(
    ({ text }) => {
      const s = get().settings ?? SONG_DEFAULT_SETTINGS;   // sin los ajustes cargados se proyecta con los de fábrica: nunca un «Enter» que no hace nada
      api.send("output.showContent", { blocks: [text], theme: projectionTheme(s.theme), transitionMs: s.transitionMs });
    },
    deps.sendIntervalMs ?? 60,
    clock,
  );
  let appliedBackground: string | null = null;
  let backgroundCommandId: string | null = null;
  let selectToken = 0;
  let selectTimer: unknown = null;
  const BACKGROUND_FADE_MS = 600;

  const patchProjection = (changes: Partial<ProjectionState>): void => {
    set((s) => (s.projection ? { projection: { ...s.projection, ...changes } } : s));
  };

  const deriveAsync = async (sources: { uid: string; label: string; text: string }[], indexes: number[], cancelled: () => boolean): Promise<SongSlide[] | null> => {
    const pagesOf = makePagesOf(fitFor(theme()));
    const done = await warmChunks(uniqueChunks(sources, indexes), pagesOf, { budgetMs: deps.sliceBudgetMs ?? 12, now, yieldNow, cancelled });
    return done ? deriveSlides(sources, indexes, pagesOf) : null;
  };

  /** Carga la canción seleccionada y deriva su tira (en tandas, como el editor). `keep` conserva la diapositiva seleccionada si es la misma canción. */
  const loadProjection = async (id: number, keep: boolean): Promise<void> => {
    const token = ++selectToken;
    const previous = get().projection;
    if (keep && previous?.songId === id) patchProjection({ busy: true });
    else set({ projection: { songId: id, title: "", revision: 0, background: null, slides: null, busy: true, cursor: 0, requested: null, atEnd: false, error: null } });
    try {
      const song = await api.request("song.get", { id });
      if (gone() || token !== selectToken) return;
      const sources = song.sections.map((s, i) => ({ uid: String(s.id ?? i), label: s.label, text: s.text }));
      const slides = await deriveAsync(sources, song.arrangement, () => token !== selectToken || gone());
      if (!slides || gone() || token !== selectToken) return;
      const cursor = keep && previous?.songId === id ? Math.min(previous.cursor, Math.max(0, slides.length - 1)) : 0;
      patchProjection({ title: song.title, revision: song.revision, background: song.background, slides, busy: false, cursor, error: null });
    } catch (error) {
      if (gone() || token !== selectToken) return;
      if (errorCode(error) === "song.notFound") set({ projection: null });
      else patchProjection({ busy: false, error: errorMessage(error) });
    }
  };

  /** Si cambió (o se borró) la canción seleccionada —se guardó en el editor, otra ventana— se vuelve a leer su tira. */
  function notifyProjection(p: SongChangedPayload): void {
    const proj = get().projection;
    if (!proj || proj.songId !== p.songId) return;
    if (p.kind === "deleted") { selectToken++; set({ projection: null }); }
    else void loadProjection(p.songId, true);
  }

  /** Fondo propio de la canción: si lo tiene y no es el que ya se aplicó, UN `output.setBackground` (carril lento, validado por el backend). Sin fondo propio NO toca el actual. */
  const applyBackground = (p: ProjectionState): void => {
    const bg = p.background;
    if (!bg) return;
    const key = `${String(p.songId)}:${JSON.stringify(bg)}`;
    if (key === appliedBackground) return;
    appliedBackground = key;
    backgroundCommandId = api.send("output.setBackground", {
      kind: bg.kind === "color" ? "color" : bg.kind === "image" ? "image" : "video",
      path: bg.kind === "color" ? null : bg.path,
      color: bg.color,
      muted: bg.muted,
      transitionMs: BACKGROUND_FADE_MS,
    });
  };

  const projectAt = (index: number, immediate: boolean): void => {
    const p = get().projection;
    const slide = p?.slides?.[index];
    if (!p || !slide) return;
    applyBackground(p);   // antes que el texto (otro carril, sin orden garantizado: el fondo es una capa independiente)
    patchProjection({ cursor: index, requested: index, atEnd: false });
    if (immediate) sender.now({ text: slide.text });
    else sender.push({ text: slide.text });
  };

  // ───────── cargar una canción en el editor ─────────

  const blankParse = (text: string): ParseInfo => ({ ...emptyParse, detected: text.trim() === "" ? "empty" : "brackets" });

  const editorFromSong = (song: SongPayload, history: SongRevisionDto[] | null): EditorState => {
    const sections: EditorSection[] = song.sections.map((s) => ({ uid: newUid(), serverId: s.id, kind: s.kind, label: s.label, text: s.text }));
    const order: OrderEntry[] = song.arrangement.map((i) => ({ uid: sections[i]?.uid ?? "", label: sections[i]?.label ?? "" })).filter((o) => o.uid !== "");
    const base: EditorState = {
      songId: song.id, revision: song.revision, fromImport: false,
      title: song.title, author: song.author ?? "", copyright: song.copyright ?? "", ccli: song.ccliNumber ?? "", notes: song.notes ?? "",
      background: song.background, text: formatSectionsText(sections), sections, order,
      baselineSections: sections.map((s) => ({ ...s })), baselinePrint: "",
      parse: blankParse(song.sections.length === 0 ? "" : "x"), slides: null, slidesBusy: false, selectedOrder: 0, selectedSlide: 0,
      usedInServices: song.usedInServices, history, historyOpen: false,
    };
    return { ...base, baselinePrint: fingerprint(editableOf(base)) };
  };

  const emptyEditor = (): EditorState => {
    const base: EditorState = {
      songId: null, revision: null, fromImport: false, title: "", author: "", copyright: "", ccli: "", notes: "", background: null, text: "", sections: [], order: [],
      baselineSections: [], baselinePrint: "", parse: blankParse(""), slides: null, slidesBusy: false, selectedOrder: 0, selectedSlide: 0, usedInServices: 0, history: null, historyOpen: false,
    };
    return { ...base, baselinePrint: fingerprint(editableOf(base)) };
  };

  const showEditor = (editor: EditorState): void => {
    set({ editor, mode: "editor", screen: "main", saving: false, conflict: false, confirmLeave: false, importDraft: null, message: null });
    scheduleSlides();
  };

  const loadHistory = async (songId: number): Promise<SongRevisionDto[] | null> => {
    try { return (await api.request("song.history", { songId })).revisions; } catch { return null; }
  };

  const openFromServer = async (id: number): Promise<boolean> => {
    try {
      const [song, history] = await Promise.all([api.request("song.get", { id }), loadHistory(id)]);
      if (gone()) return false;
      showEditor(editorFromSong(song, history));
      return true;
    } catch (error) {
      if (!gone()) set({ message: errorMessage(error) });
      return false;
    }
  };

  /** Si hay cambios sin guardar pregunta (en la propia pantalla, nunca con un cuadro del sistema) antes de seguir. */
  let pendingProceed: (() => void) | null = null;
  const guard = (proceed: () => void): void => {
    const s = get();
    if (s.mode === "editor" && isDirty(s.editor)) {
      pendingProceed = proceed;
      set({ confirmLeave: true });
      return;
    }
    proceed();
  };

  const backToList = (): void => {
    pendingProceed = null;
    deps.storage?.clear();
    set({ mode: "list", editor: null, confirmLeave: false, conflict: false, saving: false, importDraft: null });
    slidesToken++;
  };

  // ───────── lista y búsqueda ─────────

  let queryTimer: unknown = null;
  let queryToken = 0;
  const visible = (): SongSummaryDto[] => get().results ?? get().list;

  const runSearch = async (): Promise<void> => {
    const query = get().query.trim();
    const token = ++queryToken;
    if (query.length === 0) { set({ results: null, resultsTruncated: false, listIndex: 0 }); return; }
    try {
      const reply = await api.request("song.search", { query, limit: 50 });
      if (gone() || token !== queryToken) return;
      set({ results: reply.songs, resultsTruncated: reply.truncated, listIndex: 0, message: null });
    } catch (error) {
      if (gone() || token !== queryToken) return;
      // «Escribe al menos una palabra» (solo símbolos): se queda la lista completa, sin alarmar.
      if (errorCode(error) === "song.invalidRequest") set({ results: null, resultsTruncated: false });
      else set({ message: errorMessage(error) });
    }
  };

  // ───────── guardar ─────────

  const afterSave = async (changed: SongChangedPayload, printAtSave: string): Promise<void> => {
    const song = await api.request("song.get", { id: changed.songId });
    if (gone()) return;
    const e = get().editor;
    if (!e) return;
    const unchanged = fingerprint(editableOf(e)) === printAtSave;
    // Los ids del servidor, por etiqueta (las secciones se guardaron en el mismo orden pero el texto pudo cambiar mientras tanto).
    const idByKey = new Map(song.sections.map((s) => [labelKey(s.label), s.id]));
    const sections = e.sections.map((s) => (s.serverId !== null ? s : { ...s, serverId: idByKey.get(labelKey(s.label)) ?? null }));
    const serverSections: EditorSection[] = song.sections.map((s) => {
      const match = sections.find((x) => x.serverId === s.id);
      return { uid: match?.uid ?? newUid(), serverId: s.id, kind: s.kind, label: s.label, text: s.text };
    });
    const serverOrder: OrderEntry[] = song.arrangement.map((i) => ({ uid: serverSections[i]?.uid ?? "", label: serverSections[i]?.label ?? "" })).filter((o) => o.uid !== "");
    const serverBase: EditableSong = {
      title: song.title, author: song.author ?? "", copyright: song.copyright ?? "", ccli: song.ccliNumber ?? "", notes: song.notes ?? "",
      background: song.background, sections: serverSections, order: serverOrder,
    };
    const history = await loadHistory(song.id);
    if (gone()) return;
    patch({
      songId: song.id, revision: song.revision, fromImport: false, usedInServices: song.usedInServices, history,
      baselineSections: serverSections, baselinePrint: fingerprint(serverBase),
      ...(unchanged ? { title: serverBase.title, author: serverBase.author, copyright: serverBase.copyright, ccli: serverBase.ccli, notes: serverBase.notes, sections: serverSections, order: serverOrder } : { sections }),
    });
    set({ saving: false, conflict: false, message: null });
    deps.storage?.clear();
    void controller.refreshList();
  };

  const doSave = async (asCopy = false): Promise<boolean> => {
    if (get().saving) return false;
    await flushParse();
    const e = get().editor;
    if (!e) return false;
    const blocker = saveBlocker(e);
    if (blocker) { set({ message: blocker }); return false; }
    const printAtSave = fingerprint(editableOf(e));
    const payload = buildSavePayload(asCopy ? null : e.songId, asCopy ? null : e.revision, editableOf(asCopy ? { ...e, sections: e.sections.map((s) => ({ ...s, serverId: null })) } : e), e.fromImport ? "import" : "save");
    if (!payload) { set({ message: "El orden hace referencia a una sección que ya no existe." }); return false; }
    set({ saving: true, message: null, conflict: false });
    try {
      const changed = await sendAndWait("song.save", payload);
      if (asCopy) {
        // La copia es otra canción: se abre ella (la original queda como estaba en el servidor).
        set({ saving: false });
        await openFromServer(changed.songId);
        void controller.refreshList();
        return true;
      }
      await afterSave(changed, printAtSave);
      return true;
    } catch (error) {
      if (gone()) return false;
      const code = errorCode(error);
      set({ saving: false, conflict: code === "song.conflict", message: code === "song.conflict" ? "La canción cambió mientras la editabas (otra ventana u otra orden la guardó). Tus cambios siguen aquí, sin guardar." : errorMessage(error) });
      return false;
    }
  };

  // ───────── importar ─────────

  let importToken = 0;
  let importTimer: unknown = null;
  const patchImport = (changes: Partial<ImportDraft>): void => {
    set((s) => (s.importDraft ? { importDraft: { ...s.importDraft, ...changes } } : s));
  };
  const parseImport = async (): Promise<void> => {
    const d = get().importDraft;
    if (!d) return;
    const token = ++importToken;
    if (d.text.trim() === "") { patchImport({ parsed: null, parsing: false, parseError: null }); return; }
    patchImport({ parsing: true });
    try {
      const reply = await api.request("song.parseText", { text: d.text, mergeIdenticalBlocks: d.merge });
      if (gone() || token !== importToken) return;
      patchImport({ parsed: reply, parsing: false, parseError: null });
    } catch (error) {
      if (gone() || token !== importToken) return;
      patchImport({ parsing: false, parseError: errorMessage(error) });
    }
  };
  const scheduleImportParse = (): void => {
    if (gone()) return;
    if (importTimer !== null) clock.clearTimeout(importTimer);
    patchImport({ parsing: true });
    importTimer = clock.setTimeout(() => { importTimer = null; void parseImport(); }, deps.parseDebounceMs ?? 150);
  };
  const emptyImport = (text = ""): ImportDraft => ({
    text, fileName: null, bytes: null, encoding: null, forced: "auto", decodeWarnings: [], fileError: null, merge: true, title: "", parsed: null, parsing: false, parseError: null,
  });

  // ───────── el controlador ─────────

  const controller: SongController = {
    store,

    async init() {
      unsubscribers.push(api.subscribe("song.changed", onChanged), api.subscribe("command.failed", onFailed));
      try {
        const [settings, list] = await Promise.all([api.request("song.getSettings", {}), api.request("song.list", {})]);
        if (gone()) return;
        let recovered: RecoveredDraft | null = null;
        const raw = deps.storage?.get() ?? null;
        if (raw) {
          try {
            const d = JSON.parse(raw) as { v?: number; songId?: number | null; title?: string };
            if (d.v === DRAFT_VERSION) recovered = { songId: d.songId ?? null, title: d.title ?? "" };
          } catch { deps.storage?.clear(); }
        }
        set({ loaded: true, settings, list: list.songs, listTruncated: list.truncated, recovered });
      } catch (error) {
        if (!gone()) set({ loaded: true, message: errorMessage(error) });
      }
    },

    async refreshList() {
      try {
        const list = await api.request("song.list", {});
        if (gone()) return;
        set({ list: list.songs, listTruncated: list.truncated, listIndex: Math.min(get().listIndex, Math.max(0, list.songs.length - 1)) });
        if (get().query.trim() !== "") void runSearch();
      } catch (error) {
        if (!gone()) set({ message: errorMessage(error) });
      }
    },

    setQuery(query) {
      set({ query });
      if (queryTimer !== null) clock.clearTimeout(queryTimer);
      queryTimer = clock.setTimeout(() => { queryTimer = null; void runSearch(); }, deps.searchDebounceMs ?? 180);
    },

    moveList(delta) {
      const size = visible().length;
      if (size === 0) return;
      set({ listIndex: Math.max(0, Math.min(size - 1, get().listIndex + delta)) });
      // La tira de la canción seleccionada se carga cuando dejas de moverte (recorrer la lista deprisa no pide cada canción).
      if (selectTimer !== null) clock.clearTimeout(selectTimer);
      selectTimer = clock.setTimeout(() => {
        selectTimer = null;
        const song = visible()[get().listIndex];
        if (song && !gone() && get().projection?.songId !== song.id) void loadProjection(song.id, false);
      }, deps.selectDelayMs ?? 120);
    },

    selectIndex(index) {
      const song = visible()[index];
      if (!song) return;
      set({ listIndex: index });
      if (selectTimer !== null) { clock.clearTimeout(selectTimer); selectTimer = null; }
      const current = get().projection;
      if (current?.songId === song.id && current.slides) return;
      void loadProjection(song.id, false);
    },

    projectCursor() {
      const p = get().projection;
      if (p?.slides?.length) projectAt(Math.min(p.cursor, p.slides.length - 1), true);
    },

    projectSlide(index) {
      projectAt(index, true);
    },

    nextSlide() {
      const p = get().projection;
      const count = p?.slides?.length ?? 0;
      if (!p || count === 0) return;
      if (p.requested === null) { projectAt(Math.min(p.cursor, count - 1), false); return; }   // todavía no se proyectó nada de esta canción: «siguiente» empieza por la seleccionada
      if (p.cursor >= count - 1) {
        // Al final: se queda en la última diapositiva (una pulsación de más no vacía la pantalla en un culto). Con «quitar el texto» en el tema, la limpia.
        if (get().settings?.atEnd === "clear") controller.clearProjection();
        else patchProjection({ atEnd: true });
        return;
      }
      projectAt(p.cursor + 1, false);
    },

    prevSlide() {
      const p = get().projection;
      const count = p?.slides?.length ?? 0;
      if (!p || count === 0) return;
      if (p.requested === null) { projectAt(Math.min(p.cursor, count - 1), false); return; }
      if (p.cursor <= 0) return;   // en el límite no pasa nada (ni se vuelve a proyectar)
      projectAt(p.cursor - 1, false);
    },

    clearProjection() {
      sender.cancel();
      api.send("output.clearContent", { transitionMs: get().settings?.transitionMs ?? 400 });
      patchProjection({ requested: null, atEnd: false });
    },

    openSelected() {
      const song = visible()[get().listIndex];
      if (song) controller.openSong(song.id);
    },

    openSong(id) {
      guard(() => { void openFromServer(id); });
    },

    newSong() {
      guard(() => { showEditor(emptyEditor()); });
    },

    duplicate(id) {
      guard(() => {
        void (async () => {
          try {
            const song = await api.request("song.get", { id });
            const sections = song.sections.map((s) => ({ ...s, id: null }));
            const changed = await sendAndWait("song.save", {
              id: null, expectedRevision: null, title: `${song.title} (copia)`, author: song.author, copyright: song.copyright, ccliNumber: song.ccliNumber, notes: song.notes,
              background: song.background, sections, arrangement: song.arrangement, reason: "save",
            });
            if (gone()) return;
            await controller.refreshList();
            await openFromServer(changed.songId);
          } catch (error) {
            if (!gone()) set({ message: errorMessage(error) });
          }
        })();
      });
    },

    askDelete(id) {
      const target = id !== undefined ? (visible().find((s) => s.id === id) ?? get().list.find((s) => s.id === id)) : visible()[get().listIndex];
      const currentId = id ?? get().editor?.songId ?? target?.id;
      if (currentId === undefined) return;
      void (async () => {
        try {
          const song = await api.request("song.get", { id: currentId });
          if (!gone()) set({ deleteAsk: { id: song.id, title: song.title, used: song.usedInServices } });
        } catch (error) {
          if (!gone()) set({ message: errorMessage(error) });
        }
      })();
    },

    cancelDelete() {
      set({ deleteAsk: null });
    },

    async confirmDelete() {
      const ask = get().deleteAsk;
      if (!ask) return;
      try {
        const changed = await sendAndWait("song.delete", { id: ask.id });
        if (gone()) return;
        set({ deleteAsk: null, message: changed.usedInServices > 0 ? `Canción borrada. Estaba en ${String(changed.usedInServices)} servicio(s): quedó como «Falta» en cada uno.` : null });
        if (get().editor?.songId === ask.id) backToList();
        await controller.refreshList();
      } catch (error) {
        if (!gone()) set({ deleteAsk: null, message: errorMessage(error) });
      }
    },

    setField(field, value) {
      patch({ [field]: value });
      scheduleDraft();
    },

    setBackground(background) {
      patch({ background });
      scheduleDraft();
    },

    setText(text) {
      patch({ text });
      scheduleParse();
    },

    selectOrder(index) {
      const e = get().editor;
      if (!e) return;
      patch({ selectedOrder: Math.max(0, Math.min(Math.max(0, e.order.length - 1), index)) });
    },

    moveOrder(from, to) {
      const e = get().editor;
      if (!e || from === to || from < 0 || from >= e.order.length) return;
      const target = Math.max(0, Math.min(e.order.length - 1, to));
      const order = [...e.order];
      const [moved] = order.splice(from, 1);
      if (!moved) return;
      order.splice(target, 0, moved);
      patch({ order, selectedOrder: target });
      scheduleSlides();
      scheduleDraft();
    },

    removeOrder(index) {
      const e = get().editor;
      if (!e || index < 0 || index >= e.order.length) return;
      const order = e.order.filter((_, i) => i !== index);
      patch({ order, selectedOrder: Math.max(0, Math.min(order.length - 1, index)) });
      scheduleSlides();
      scheduleDraft();
    },

    repeatOrder(index) {
      const e = get().editor;
      const entry = e?.order[index];
      if (!e || !entry) return;
      const order = [...e.order];
      order.splice(index + 1, 0, { ...entry });
      patch({ order, selectedOrder: index + 1 });
      scheduleSlides();
      scheduleDraft();
    },

    insertSection(uid) {
      const e = get().editor;
      const section = e?.sections.find((s) => s.uid === uid);
      if (!e || !section) return;
      const order = [...e.order];
      const at = Math.min(order.length, e.selectedOrder + 1 <= order.length && order.length > 0 ? e.selectedOrder + 1 : order.length);
      order.splice(at, 0, { uid: section.uid, label: section.label });
      patch({ order, selectedOrder: at });
      scheduleSlides();
      scheduleDraft();
    },

    selectSlide(index) {
      patch({ selectedSlide: Math.max(0, index) });
    },

    save() {
      return doSave();
    },

    async saveAndClose() {
      if (await doSave()) backToList();
    },

    closeEditor() {
      guard(backToList);
    },

    async resolveLeave(choice) {
      if (choice === "stay") { pendingProceed = null; set({ confirmLeave: false }); return; }
      const proceed = pendingProceed;
      if (choice === "save") {
        const ok = await doSave();
        if (!ok) { set({ confirmLeave: false }); return; }   // no se pudo guardar: se queda en el editor con el aviso
      } else {
        deps.storage?.clear();
      }
      pendingProceed = null;
      set({ confirmLeave: false });
      // «Descartar»: el editor deja de estar «sin guardar» para que `proceed` no vuelva a preguntar.
      if (choice === "discard") { const e = get().editor; if (e) patch({ baselinePrint: fingerprint(editableOf(e)) }); }
      proceed?.();
    },

    async openHistory() {
      const e = get().editor;
      if (!e || e.songId === null) return;
      const history = await loadHistory(e.songId);
      if (!gone()) patch({ history, historyOpen: true });
    },

    closeHistory() {
      patch({ historyOpen: false });
    },

    async undoLastSave() {
      const e = get().editor;
      if (!e || e.songId === null || e.revision === null || get().saving) return;
      set({ saving: true, message: null });
      try {
        const changed = await sendAndWait("song.restoreRevision", { songId: e.songId, revisionId: null, expectedRevision: e.revision });
        await openFromServer(changed.songId);
        if (!gone()) set({ saving: false, message: "Se deshizo el último guardado (la canción vuelve a como estaba antes). Puedes volver a deshacerlo." });
        void controller.refreshList();
      } catch (error) {
        if (!gone()) set({ saving: false, conflict: errorCode(error) === "song.conflict", message: errorMessage(error) });
      }
    },

    async restoreVersion(revisionId) {
      const e = get().editor;
      if (!e || e.songId === null || e.revision === null || get().saving) return;
      set({ saving: true, message: null });
      try {
        const changed = await sendAndWait("song.restoreRevision", { songId: e.songId, revisionId, expectedRevision: e.revision });
        await openFromServer(changed.songId);
        if (!gone()) set({ saving: false, message: "Versión restaurada." });
        void controller.refreshList();
      } catch (error) {
        if (!gone()) set({ saving: false, conflict: errorCode(error) === "song.conflict", message: errorMessage(error) });
      }
    },

    async resolveConflict(choice) {
      const e = get().editor;
      if (!e) return;
      if (choice === "reload" && e.songId !== null) {
        deps.storage?.clear();
        await openFromServer(e.songId);
      } else if (choice === "saveCopy") {
        await doSave(true);
      }
    },

    // ───────── importar ─────────

    openImport(initialText = "") {
      set({ importDraft: emptyImport(initialText), message: null });
      if (initialText !== "") scheduleImportParse();
    },

    closeImport() {
      importToken++;
      set({ importDraft: null });
    },

    importSetText(text) {
      patchImport({ text, fileName: null, bytes: null, encoding: null, decodeWarnings: [], fileError: null });
      scheduleImportParse();
    },

    importSetTitle(title) {
      patchImport({ title });
    },

    importSetMerge(merge) {
      patchImport({ merge });
      scheduleImportParse();
    },

    async importFile(file) {
      if (!get().importDraft) set({ importDraft: emptyImport() });
      const name = file.name;
      const baseName = name.replace(/\.[^.]*$/, "");
      if (file.size > MAX_FILE_BYTES) {
        patchImport({ fileName: name, bytes: null, encoding: null, decodeWarnings: [], text: "", parsed: null, fileError: `El archivo es demasiado grande para ser la letra de una canción (${String(Math.round(file.size / 1024))} KB; el máximo es ${String(MAX_FILE_BYTES / 1024)} KB).` });
        return;
      }
      try {
        const bytes = new Uint8Array(await file.arrayBuffer());
        if (gone()) return;
        const forced = get().importDraft?.forced ?? "auto";
        const decoded = decodeSongFile(bytes, forced);
        if (!decoded.ok) { patchImport({ fileName: name, bytes: null, encoding: null, decodeWarnings: [], text: "", parsed: null, fileError: decoded.message }); return; }
        const title = get().importDraft?.title.trim() ? get().importDraft?.title ?? baseName : baseName;
        patchImport({ fileName: name, bytes, encoding: decoded.encoding, decodeWarnings: decoded.warnings, fileError: null, text: decoded.text, title });
        scheduleImportParse();
      } catch (error) {
        if (!gone()) patchImport({ fileName: name, fileError: `No se pudo leer el archivo: ${errorMessage(error)}` });
      }
    },

    importForceEncoding(encoding) {
      const d = get().importDraft;
      if (!d) return;
      patchImport({ forced: encoding });
      if (!d.bytes) return;
      const decoded = decodeSongFile(d.bytes, encoding);
      if (!decoded.ok) { patchImport({ fileError: decoded.message }); return; }
      patchImport({ encoding: decoded.encoding, decodeWarnings: decoded.warnings, fileError: null, text: decoded.text });
      scheduleImportParse();
    },

    applyImport() {
      const d = get().importDraft;
      const parsed = d?.parsed;
      if (!d || !parsed || parsed.errors.length > 0 || parsed.sections.length === 0) return;
      const current = get().editor;
      const previous = current?.sections ?? [];
      const sections = reconcileSections(parsed.sections, previous, newUid);
      const order: OrderEntry[] = parsed.arrangement.map((i) => ({ uid: sections[i]?.uid ?? "", label: sections[i]?.label ?? "" })).filter((o) => o.uid !== "");
      const base = current ?? emptyEditor();
      const editor: EditorState = {
        ...base,
        title: base.title.trim() === "" ? d.title.trim() : base.title,
        fromImport: true,
        text: formatSectionsText(sections),
        sections,
        order,
        parse: { detected: parsed.detected, warnings: parsed.warnings, errors: [], orderIgnored: false, stale: false },
        slides: null,
        selectedOrder: 0,
        selectedSlide: 0,
      };
      showEditor(editor);
      scheduleDraft();
    },

    // ───────── tema ─────────

    openTheme() {
      set({ screen: "theme" });
    },

    closeTheme() {
      set({ screen: "main" });
    },

    async saveSettings(next) {
      api.send("song.setSettings", next);
      set({ settings: next });
      pagesCache.clear();
      if (get().editor) scheduleSlides();
      try {
        // Comandos y consultas comparten orden de llegada: esta lectura ya ve lo guardado (o, si el backend lo rechazó, el valor anterior).
        const confirmed = await api.request("song.getSettings", {});
        if (!gone()) {
          set({ settings: confirmed });
          pagesCache.clear();
          if (get().editor) scheduleSlides();
          const proj = get().projection;
          if (proj) void loadProjection(proj.songId, true);   // otro tema = otras diapositivas
        }
      } catch (error) {
        if (!gone()) set({ message: errorMessage(error) });
      }
    },

    // ───────── borrador recuperado ─────────

    async openRecovered() {
      const raw = deps.storage?.get();
      if (!raw) { set({ recovered: null }); return; }
      let d: {
        songId: number | null; revision: number | null; fromImport: boolean; title: string; author: string; copyright: string; ccli: string; notes: string;
        background: SongBackgroundDto | null; text: string; sections: { serverId: number | null; kind: string; label: string; text: string }[]; order: number[];
      };
      try { d = JSON.parse(raw) as typeof d; } catch { deps.storage?.clear(); set({ recovered: null }); return; }
      let base: EditorState | null = null;
      let note: string | null = null;
      if (d.songId !== null) {
        try {
          const [song, history] = await Promise.all([api.request("song.get", { id: d.songId }), loadHistory(d.songId)]);
          base = editorFromSong(song, history);
          if (song.revision !== d.revision) note = "La canción se guardó de nuevo después de que se hiciera este borrador: al guardar se pedirá confirmación (conflicto).";
        } catch {
          base = null;
          note = "La canción del borrador ya no existe: se abre como canción nueva.";
        }
      }
      const sections: EditorSection[] = d.sections.map((s) => ({ uid: newUid(), serverId: base ? s.serverId : null, kind: s.kind, label: s.label, text: s.text }));
      const order: OrderEntry[] = d.order.map((i) => ({ uid: sections[i]?.uid ?? "", label: sections[i]?.label ?? "" })).filter((o) => o.uid !== "");
      const editor: EditorState = {
        ...(base ?? emptyEditor()),
        fromImport: d.fromImport,
        title: d.title, author: d.author, copyright: d.copyright, ccli: d.ccli, notes: d.notes, background: d.background, text: d.text, sections, order,
        revision: base ? (d.revision ?? base.revision) : null,
        songId: base ? d.songId : null,
        parse: { ...emptyParse, detected: sections.length === 0 ? "empty" : "brackets" },
      };
      showEditor(editor);
      set({ recovered: null, message: note ?? "Borrador recuperado: todavía no está guardado." });
      scheduleParse();   // el texto manda: se vuelve a interpretar para que las secciones sean las de lo que se ve
    },

    discardRecovered() {
      deps.storage?.clear();
      set({ recovered: null });
    },

    dismissMessage() {
      set({ message: null });
    },

    dispose() {
      life.disposed = true;
      sender.cancel();
      for (const timer of [queryTimer, parseTimer, slidesTimer, draftTimer, importTimer, listRefreshTimer, selectTimer]) if (timer !== null) clock.clearTimeout(timer);
      for (const w of waiters.values()) clock.clearTimeout(w.timer);
      waiters.clear();
      for (const off of unsubscribers) off();
      unsubscribers.length = 0;
      fitCache?.dispose();
      fitCache = null;
    },
  };
  return controller;
}

// Reexportados para la vista.
export { isNaturalOrder, naturalOrder, orderIndexes, unresolvedOrder };
