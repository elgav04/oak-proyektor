import { createStore, type StoreApi } from "zustand/vanilla";
import type {
  BibleSettingsPayload,
  CommandPayloads,
  CommandType,
  EventPayloads,
  EventType,
  OutputStateChangedPayload,
  PlaylistItemDto,
  PlaylistPayload,
  PlaylistSettingsPayload,
  PlaylistSummaryDto,
  QueryPayloads,
  QueryType,
  SlideOrigin,
  SongBackgroundDto,
  SongSettingsPayload,
  TextTheme,
} from "@/ipc/protocol";
import { BIBLE_DEFAULT_THEME } from "../bible/defaults";
import { createLatestSender, realClock, type Clock } from "../bible/latest";
import { createBlocksMeasurer } from "../bible/measure";
import { buildPassage, projectionTheme } from "../bible/slide";
import { SONG_DEFAULT_SETTINGS } from "../songs/defaults";
import { deriveSlides, paginateChunk, uniqueChunks, warmChunks } from "../songs/slides";
import { activeFromOutput, localDate, pickDefaultList, stepItem, whyNotProjectable, type ActiveItem } from "./active";

/**
 * Controlador de la pestaña «Lista» (modo EN VIVO). Estado de VISTA (cursor, modo, lo cargado) + IPC; todo lo que importa lo manda el backend:
 *  - la lista y sus elementos vienen de `playlist.get` (réplica; se vuelve a leer en cada `playlist.changed`),
 *  - el elemento ACTIVO se deriva del `origin` del slide que CONFIRMA el backend (`output.stateChanged`), nunca de lo que el panel «cree» haber enviado,
 *  - proyectar es `output.showContent` (carril en vivo) con `origin` = ids; NO se escribe NADA en la BD al cambiar de elemento ni se espera trabajo lento.
 * Un elemento sin fondo propio NO manda ningún `output.setBackground` (no corta el video actual); uno con fondo lo manda una sola vez (carril lento) antes del texto.
 */

export interface ListApi {
  request<T extends QueryType>(type: T, payload: QueryPayloads[T]["request"], timeoutMs?: number): Promise<QueryPayloads[T]["reply"]>;
  send<T extends CommandType>(type: T, payload: CommandPayloads[T]): string;
  subscribe<T extends EventType>(type: T, handler: (payload: EventPayloads[T]) => void): () => void;
}

export type BlocksFit = (blocks: readonly string[]) => boolean;

export interface ListStorage {
  get(): string | null;
  set(value: string): void;
}

export interface ListControllerDeps {
  api: ListApi;
  /** El estado de la salida que CONFIRMA el backend (en la app: el de la réplica del panel). */
  getOutput: () => OutputStateChangedPayload | null;
  createFit?: (theme: TextTheme) => { fits: BlocksFit; dispose: () => void };
  clock?: Clock;
  storage?: ListStorage | null;
  /** Mínimo entre dos envíos de proyección (la última siempre llega). */
  sendIntervalMs?: number;
  /** Cuánto reposo hace falta antes de precargar los elementos que se van a necesitar. */
  prefetchDelayMs?: number;
  sliceBudgetMs?: number;
  yieldNow?: () => Promise<void>;
  now?: () => number;
  /** Una proyección enviada y aún sin confirmar vale como «dónde estamos» durante este tiempo (ráfagas de teclas). */
  inflightMs?: number;
  today?: () => string;
}

export interface ItemContent {
  itemId: number;
  key: string;
  title: string;
  /** Una diapositiva (canción) o página (pasaje) = los bloques que se mandan a la salida. */
  slides: string[][];
  /** Tema con que se proyecta (con la red de seguridad del ajuste automático). */
  theme: TextTheme;
  transitionMs: number;
  /** Fondo propio (solo canciones). Sin fondo = no se toca el actual. */
  background: SongBackgroundDto | null;
}

export type ContentState = { status: "loading" } | { status: "ready"; content: ItemContent } | { status: "error"; message: string };

/** Lo último que el panel PIDIÓ proyectar (para encadenar teclas en ráfaga); el elemento activo «de verdad» se deriva del estado confirmado. */
export interface Requested {
  itemId: number;
  position: number;
  inflight: boolean;
  at: number;
}

export interface ListViewState {
  loaded: boolean;
  mode: "live" | "edit";
  lists: PlaylistSummaryDto[];
  /** Error al cargar las listas o la abierta (mensaje para el operador); null si va bien. */
  error: string | null;
  openId: number | null;
  list: PlaylistPayload | null;
  loadingList: boolean;
  settings: PlaylistSettingsPayload | null;
  /** Posición (en la lista) del elemento seleccionado con ↑/↓: estado de vista; sigue al elemento proyectado. */
  cursor: number;
  /** «FIN»: se pidió avanzar en la última diapositiva y se quedó (o se limpió). */
  atEnd: boolean;
  notice: string | null;
  contents: Record<number, ContentState>;
  requested: Requested | null;
}

const initialState: ListViewState = {
  loaded: false, mode: "live", lists: [], error: null, openId: null, list: null, loadingList: false, settings: null,
  cursor: 0, atEnd: false, notice: null, contents: {}, requested: null,
};

const errorMessage = (error: unknown): string => (error instanceof Error ? error.message : typeof error === "object" && error !== null && "message" in error ? String(error.message) : String(error));
const errorCode = (error: unknown): string => (typeof error === "object" && error !== null && "code" in error ? String(error.code) : "");

export interface ListController {
  store: StoreApi<ListViewState>;
  init(): Promise<void>;
  /** La pestaña se muestra: vuelve a leer ajustes y listas (se pudieron cambiar desde otra pestaña u otra ventana). */
  onShow(): Promise<void>;
  openList(id: number): Promise<void>;
  setMode(mode: "live" | "edit"): void;
  toggleMode(): void;
  moveCursor(delta: number): void;
  setCursor(index: number): void;
  /** Enter: activa el elemento del cursor. */
  activate(): void;
  nextSlide(): void;
  prevSlide(): void;
  nextItem(): void;
  prevItem(): void;
  /** Esc: quita el texto de la pantalla (no cambia el elemento activo). */
  clear(): void;
  /** Clic en una diapositiva de la tira: la proyecta. */
  projectSlide(itemId: number, position: number): void;
  dismissNotice(): void;
  /** Dónde está el panel para encadenar teclas: lo último pedido si sigue en vuelo; si no, el activo confirmado; si no, lo último que se mostró. */
  anchor(): ActiveItem | null;
  dispose(): void;
}

export function createListController(deps: ListControllerDeps): ListController {
  const { api } = deps;
  const clock = deps.clock ?? realClock;
  const now = deps.now ?? (() => (typeof performance !== "undefined" ? performance.now() : Date.now()));
  const yieldNow = deps.yieldNow ?? (() => new Promise<void>((resolve) => { setTimeout(resolve, 0); }));
  const store = createStore<ListViewState>(() => ({ ...initialState }));
  const get = store.getState;
  const set = store.setState;
  const life = { disposed: false };
  const gone = (): boolean => life.disposed;
  const unsubscribers: (() => void)[] = [];

  let songSettings: SongSettingsPayload = SONG_DEFAULT_SETTINGS;
  let bibleSettings: Pick<BibleSettingsPayload, "theme" | "transitionMs" | "shortNames" | "showAttribution" | "superscriptNumbers"> = {
    theme: BIBLE_DEFAULT_THEME, transitionMs: 280, shortNames: [], showAttribution: true, superscriptNumbers: true,
  };

  // ───────── medición ─────────

  const themeKey = (t: TextTheme): string => JSON.stringify([t.fontFamily, t.fontSize, t.lineHeight, t.outlineWidth, t.shadowBlur, t.shadowOffset, t.marginTop, t.marginBottom, t.marginLeft, t.marginRight, t.align]);
  const fits = new Map<string, { fits: BlocksFit; dispose: () => void }>();
  const fitFor = (theme: TextTheme): BlocksFit => {
    const key = themeKey(theme);
    const found = fits.get(key);
    if (found) return found.fits;
    if (fits.size >= 3) {
      const oldest = fits.keys().next().value;
      if (oldest !== undefined) { fits.get(oldest)?.dispose(); fits.delete(oldest); }
    }
    const made = (deps.createFit ?? ((th) => {
      const m = createBlocksMeasurer(th);
      return { fits: (blocks: readonly string[]) => m.fits(blocks), dispose: () => { m.dispose(); } };
    }))(theme);
    fits.set(key, made);
    return made.fits;
  };
  const pagesCache = new Map<string, string[]>();
  const pagesOf = (theme: TextTheme) => (chunk: string): string[] => {
    const key = `${themeKey(theme)}\u0000${chunk}`;
    let pages = pagesCache.get(key);
    if (!pages) {
      const fit = fitFor(theme);
      pages = paginateChunk(chunk, (text) => fit([text]));
      if (pagesCache.size > 800) pagesCache.clear();
      pagesCache.set(key, pages);
    }
    return pages;
  };

  // ───────── contenido de los elementos (lo que se proyectaría) ─────────

  const songTheme = (): TextTheme => projectionTheme(songSettings.theme);
  const bibleTheme = (): TextTheme => projectionTheme(bibleSettings.theme);

  const contentKey = (item: PlaylistItemDto): string => {
    const theme = item.itemType === "song" ? songTheme() : bibleTheme();
    const extra = item.itemType === "song" ? songSettings.transitionMs : [bibleSettings.transitionMs, bibleSettings.showAttribution, bibleSettings.superscriptNumbers, bibleSettings.shortNames.find((n) => n.bible === item.bibleAbbreviation)?.shortName ?? ""];
    return JSON.stringify([item.id, item.itemType, item.songId, item.bookNumber, item.chapter, item.verseStart, item.verseEnd, item.bibleAbbreviation, themeKey(theme), extra]);
  };

  const setContent = (itemId: number, state: ContentState | null): void => {
    set((s) => {
      const contents: Record<number, ContentState> = {};
      for (const [id, value] of Object.entries(s.contents)) if (Number(id) !== itemId) contents[Number(id)] = value;
      if (state) contents[itemId] = state;
      return { contents };
    });
  };

  const cachedReady = (item: PlaylistItemDto): ItemContent | null => {
    const c = get().contents[item.id];
    return c?.status === "ready" && c.content.key === contentKey(item) ? c.content : null;
  };

  const buildSong = async (item: PlaylistItemDto, key: string): Promise<ItemContent> => {
    if (item.songId === null) throw new Error("La canción ya no existe.");
    const song = await api.request("song.get", { id: item.songId });
    const theme = songTheme();
    const sources = song.sections.map((s, i) => ({ uid: String(s.id ?? i), label: s.label, text: s.text }));
    const pages = pagesOf(theme);
    const done = await warmChunks(uniqueChunks(sources, song.arrangement), pages, { budgetMs: deps.sliceBudgetMs ?? 12, now, yieldNow, cancelled: gone });
    if (!done) throw new Error("cancelado");
    const slides = deriveSlides(sources, song.arrangement, pages).map((s) => [s.text]);
    if (slides.length === 0) throw new Error("La canción no tiene diapositivas (sin texto o sin orden).");
    return { itemId: item.id, key, title: song.title, slides, theme, transitionMs: songSettings.transitionMs, background: song.background };
  };

  const MAX_PASSAGE_VERSES = 60;
  const buildPassageContent = async (item: PlaylistItemDto, key: string): Promise<ItemContent> => {
    if (item.bookNumber === null || item.chapter === null || item.verseStart === null || item.bibleAbbreviation === null) throw new Error("El pasaje está incompleto.");
    const chapter = await api.request("bible.chapter", { bible: item.bibleAbbreviation, bookNumber: item.bookNumber, chapter: item.chapter });
    const last = item.verseEnd ?? item.verseStart;
    const verses = chapter.verses.filter((v) => v.verseNumber >= (item.verseStart ?? 0) && v.verseNumber <= last);
    if (verses.length === 0) throw new Error("El pasaje ya no está en esa Biblia.");
    if (verses.length > MAX_PASSAGE_VERSES) throw new Error(`El pasaje tiene ${String(verses.length)} versículos: es demasiado largo para proyectarlo desde la lista (máximo ${String(MAX_PASSAGE_VERSES)}).`);
    const theme = bibleTheme();
    const shortName = bibleSettings.shortNames.find((n) => n.bible === chapter.bible)?.shortName ?? chapter.bible;
    const built = buildPassage(verses, { bibleName: shortName, attribution: chapter.attribution, showAttribution: bibleSettings.showAttribution, superscript: bibleSettings.superscriptNumbers }, fitFor(theme));
    if (built.pages.length === 0) throw new Error("El pasaje no tiene páginas.");
    return { itemId: item.id, key, title: built.reference, slides: built.pages, theme, transitionMs: bibleSettings.transitionMs, background: null };
  };

  const loading = new Map<number, { key: string; promise: Promise<ItemContent | null> }>();

  /** El contenido del elemento: el memorizado, el que ya se está cargando o uno nuevo (lectura corta de `song.get` / `bible.chapter` + paginación por tandas). Null si falló (queda el motivo en `contents`). */
  const ensureContent = (item: PlaylistItemDto): Promise<ItemContent | null> => {
    const cached = cachedReady(item);
    if (cached) return Promise.resolve(cached);
    const key = contentKey(item);
    const pending = loading.get(item.id);
    if (pending?.key === key) return pending.promise;
    setContent(item.id, { status: "loading" });
    const promise = (item.itemType === "song" ? buildSong(item, key) : buildPassageContent(item, key))
      .then((content): ItemContent | null => {
        if (gone() || loading.get(item.id)?.key !== key) return null;
        setContent(item.id, { status: "ready", content });
        return content;
      })
      .catch((error: unknown): ItemContent | null => {
        if (gone() || loading.get(item.id)?.key !== key) return null;
        const message = errorCode(error) === "song.notFound" ? "La canción ya no existe." : errorCode(error) === "bible.notFound" ? "Esa Biblia no está instalada." : errorMessage(error);
        setContent(item.id, { status: "error", message });
        return null;
      })
      .finally(() => { if (loading.get(item.id)?.key === key) loading.delete(item.id); });
    loading.set(item.id, { key, promise });
    return promise;
  };

  // ───────── precarga (el siguiente elemento y el del cursor) ─────────

  let prefetchTimer: unknown = null;
  let prefetchToken = 0;
  const schedulePrefetch = (): void => {
    if (prefetchTimer !== null) clock.clearTimeout(prefetchTimer);
    const token = ++prefetchToken;
    prefetchTimer = clock.setTimeout(() => {
      prefetchTimer = null;
      void (async () => {
        const list = get().list;
        if (!list || gone()) return;
        const a = anchor();
        const fromIndex = a ? list.items.findIndex((i) => i.id === a.itemId) : get().cursor - 1;
        const targets: PlaylistItemDto[] = [];
        const here = list.items[get().cursor];
        if (here?.projectable) targets.push(here);
        const next = list.items[stepItem(list.items, a ? fromIndex : get().cursor, 1).index ?? -1];
        if (next && !targets.includes(next)) targets.push(next);
        for (const item of targets) {
          if (token !== prefetchToken || gone()) return;
          await ensureContent(item);   // de uno en uno: nunca se acumulan mediciones
        }
      })();
    }, deps.prefetchDelayMs ?? 300);
  };

  // ───────── dónde estamos ─────────

  function anchor(): ActiveItem | null {
    const r = get().requested;
    const inflightFor = deps.inflightMs ?? 4000;
    if (r?.inflight && now() - r.at < inflightFor) return { itemId: r.itemId, position: r.position };
    const active = activeFromOutput(deps.getOutput(), get().list);
    if (active) return active;
    return r && get().list?.items.some((i) => i.id === r.itemId) ? { itemId: r.itemId, position: r.position } : null;
  }

  // ───────── proyección ─────────

  interface ProjectionMessage { blocks: string[]; theme: TextTheme; transitionMs: number; origin: SlideOrigin }
  let lastShowId: string | null = null;
  const sender = createLatestSender<ProjectionMessage>(
    (m) => { lastShowId = api.send("output.showContent", m); },
    deps.sendIntervalMs ?? 60,
    clock,
  );
  let appliedBackground: string | null = null;
  let backgroundCommandId: string | null = null;
  let projToken = 0;
  const BACKGROUND_FADE_MS = 600;

  /** Fondo propio de la canción: solo si lo tiene y no es el que ya se aplicó; UN `output.setBackground` por el carril lento. Sin fondo propio NO se manda nada (no corta el video actual). */
  const applyBackground = (content: ItemContent, item: PlaylistItemDto): void => {
    const bg = content.background;
    if (!bg) return;
    const key = `${String(item.songId)}:${JSON.stringify(bg)}`;
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

  const indexOfItem = (itemId: number): number => get().list?.items.findIndex((i) => i.id === itemId) ?? -1;

  const showSlide = (index: number, item: PlaylistItemDto, content: ItemContent, position: number, immediate: boolean): void => {
    const blocks = content.slides[position];
    const list = get().list;
    if (!blocks || !list) return;
    applyBackground(content, item);   // antes que el texto (otro carril, sin orden garantizado: el fondo es una capa independiente)
    set({ requested: { itemId: item.id, position, inflight: true, at: now() }, cursor: index, atEnd: false, notice: null });
    const message: ProjectionMessage = { blocks, theme: content.theme, transitionMs: content.transitionMs, origin: { playlistId: list.id, itemId: item.id, itemType: item.itemType, position } };
    if (immediate) sender.now(message);
    else sender.push(message);
    schedulePrefetch();
  };

  /** Proyecta la diapositiva `position` del elemento en `index`. Con el contenido ya memorizado es síncrono; si no, lo carga (y si mientras tanto se pidió otra cosa, GANA LA ÚLTIMA). */
  const projectItemSlide = (index: number, position: number, immediate: boolean): void => {
    const item = get().list?.items[index];
    if (!item || get().mode !== "live") return;
    const token = ++projToken;
    const why = whyNotProjectable(item);
    if (why) { set({ cursor: index, notice: why }); return; }
    const cached = cachedReady(item);
    if (cached) { showSlide(index, item, cached, Math.min(position, cached.slides.length - 1), immediate); return; }
    set({ cursor: index });
    void ensureContent(item).then((content) => {
      if (token !== projToken || gone()) return;
      if (!content) { set({ notice: `No se pudo preparar «${item.title}»: ${errorOf(item.id)}` }); return; }
      showSlide(index, item, content, Math.min(position, content.slides.length - 1), immediate);
    });
  };

  const errorOf = (itemId: number): string => {
    const c = get().contents[itemId];
    return c?.status === "error" ? c.message : "error desconocido";
  };

  const clearScreen = (): void => {
    sender.cancel();
    projToken++;
    const r = get().requested;
    const content = r ? (get().contents[r.itemId]) : undefined;
    const transitionMs = content?.status === "ready" ? content.content.transitionMs : songSettings.transitionMs;
    api.send("output.clearContent", { transitionMs });
    if (r) set({ requested: { ...r, inflight: false } });
  };

  const noticeFor = (skipped: string[]): string | null => (skipped.length === 0 ? null : `Se omitió: ${skipped.join("; ")}`);

  const jumpItem = (direction: 1 | -1): void => {
    const list = get().list;
    if (!list || get().mode !== "live") return;
    const a = anchor();
    const from = a ? indexOfItem(a.itemId) : direction > 0 ? get().cursor - 1 : get().cursor;
    const r = stepItem(list.items, from, direction);
    if (r.index === null) {
      set({ notice: [noticeFor(r.skipped), direction > 0 ? "No hay más elementos que proyectar después de este." : "No hay elementos anteriores que proyectar."].filter(Boolean).join(" · ") });
      return;
    }
    projectItemSlide(r.index, 0, true);
    const skipped = noticeFor(r.skipped);
    if (skipped) set({ notice: skipped });
  };

  const stepSlide = (direction: 1 | -1): void => {
    const list = get().list;
    if (!list || get().mode !== "live") return;
    const a = anchor();
    if (!a) {
      // Nada en pantalla ni pedido: la primera pulsación proyecta el elemento del cursor (o el siguiente que se pueda proyectar), como en Canciones.
      if (direction > 0) jumpItem(1);
      return;
    }
    const index = indexOfItem(a.itemId);
    const item = list.items[index];
    if (!item) return;
    const content = cachedReady(item);
    if (!content) {
      void ensureContent(item).then((loaded) => { if (loaded && !gone()) stepSlide(direction); });
      return;
    }
    const target = a.position + direction;
    if (target >= 0 && target < content.slides.length) { projectItemSlide(index, target, false); return; }
    if (direction < 0) { set({ atEnd: false }); return; }   // antes de la primera no pasa nada
    // Al final del elemento (decisión aprobada): por defecto se QUEDA («FIN · siguiente: …»); configurable a pasar al siguiente o a limpiar.
    switch (get().settings?.onItemEnd ?? "stay") {
      case "next": {
        const r = stepItem(list.items, index, 1);
        if (r.index === null) set({ atEnd: true, notice: noticeFor(r.skipped) });
        else { projectItemSlide(r.index, 0, true); const n = noticeFor(r.skipped); if (n) set({ notice: n }); }
        break;
      }
      case "clear":
        clearScreen();
        set({ atEnd: true });
        break;
      default:
        set({ atEnd: true });
    }
  };

  // ───────── carga de listas ─────────

  const remember = (id: number | null): void => { if (id !== null) { try { deps.storage?.set(String(id)); } catch { /* comodidad */ } } };
  const remembered = (): number | null => {
    try { const v = deps.storage?.get(); const n = v ? Number(v) : NaN; return Number.isInteger(n) && n > 0 ? n : null; } catch { return null; }
  };

  const loadSettings = async (): Promise<void> => {
    const [pl, song, bible] = await Promise.allSettled([api.request("playlist.getSettings", {}), api.request("song.getSettings", {}), api.request("bible.getSettings", {})]);
    if (gone()) return;
    if (pl.status === "fulfilled") set({ settings: pl.value });
    if (song.status === "fulfilled") songSettings = song.value;
    if (bible.status === "fulfilled") bibleSettings = bible.value;
  };

  const refreshLists = async (): Promise<void> => {
    const reply = await api.request("playlist.list", {});
    if (gone()) return;
    set({ lists: reply.playlists, error: null });
  };

  let listToken = 0;
  /** Lee la lista abierta. `reset` = abrirla de nuevo (cursor en el activo o en el primer elemento proyectable); si no, se conserva el elemento del cursor. */
  const loadList = async (id: number, reset: boolean): Promise<void> => {
    const token = ++listToken;
    set({ loadingList: true, ...(reset ? { openId: id } : {}) });
    try {
      const list = await api.request("playlist.get", { id });
      if (gone() || token !== listToken) return;
      const previous = get().list?.items[get().cursor]?.id ?? null;
      let cursor: number;
      if (reset || previous === null) {
        const active = activeFromOutput(deps.getOutput(), list);
        const activeIndex = active ? list.items.findIndex((i) => i.id === active.itemId) : -1;
        cursor = activeIndex >= 0 ? activeIndex : Math.max(0, stepItem(list.items, null, 1).index ?? 0);
      } else {
        const found = list.items.findIndex((i) => i.id === previous);
        cursor = found >= 0 ? found : Math.min(get().cursor, Math.max(0, list.items.length - 1));
      }
      set({ list, openId: id, loadingList: false, error: null, cursor, ...(reset ? { atEnd: false, notice: null, requested: null } : {}) });
      if (reset) remember(id);
      schedulePrefetch();
    } catch (error) {
      if (gone() || token !== listToken) return;
      if (errorCode(error) === "playlist.notFound") set({ list: null, openId: null, loadingList: false, error: "Esa lista ya no existe." });
      else set({ loadingList: false, error: errorMessage(error) });
    }
  };

  let refreshTimer: unknown = null;
  const scheduleRefresh = (): void => {
    if (refreshTimer !== null) clock.clearTimeout(refreshTimer);
    refreshTimer = clock.setTimeout(() => {
      refreshTimer = null;
      void refreshLists().catch(() => undefined);
      const id = get().openId;
      if (id !== null) void loadList(id, false);
    }, 80);
  };

  const onPlaylistChanged = (p: EventPayloads["playlist.changed"]): void => {
    if (p.kind === "deleted" && p.playlistId === get().openId) {
      listToken++;
      set({ list: null, openId: null, cursor: 0, requested: null });
    }
    scheduleRefresh();
  };

  const onSongChanged = (p: EventPayloads["song.changed"]): void => {
    const list = get().list;
    if (!list) return;
    const affected = list.items.filter((i) => i.songId === p.songId);
    if (affected.length === 0) return;
    for (const item of affected) { loading.delete(item.id); setContent(item.id, null); }   // la letra cambió (o la canción se borró): el contenido memorizado ya no vale
    scheduleRefresh();
    schedulePrefetch();
  };

  const onOutput = (payload: OutputStateChangedPayload): void => {
    const r = get().requested;
    const origin = payload.state.content.slide?.origin;
    const list = get().list;
    if (r?.inflight && origin && list && origin.playlistId === list.id && origin.itemId === r.itemId && origin.position === r.position) set({ requested: { ...r, inflight: false } });
  };

  const onFailed = (p: EventPayloads["command.failed"]): void => {
    if (p.commandId === backgroundCommandId) appliedBackground = null;   // el fondo no se pudo aplicar: la próxima proyección de la canción lo reintenta
    if (p.commandId === lastShowId) {
      const r = get().requested;
      set({ requested: r ? { ...r, inflight: false } : null, notice: `La salida rechazó la diapositiva: ${p.error.message}` });
    }
  };

  const controller: ListController = {
    store,

    async init() {
      if (get().loaded || gone()) return;
      if (unsubscribers.length === 0) {
        unsubscribers.push(
          api.subscribe("playlist.changed", onPlaylistChanged),
          api.subscribe("song.changed", onSongChanged),
          api.subscribe("output.stateChanged", onOutput),
          api.subscribe("command.failed", onFailed),
        );
      }
      try {
        await loadSettings();
        await refreshLists();
        if (gone()) return;
        const id = pickDefaultList(get().lists, (deps.today ?? (() => localDate(new Date())))(), remembered());
        set({ loaded: true });
        if (id !== null) await loadList(id, true);
      } catch (error) {
        if (!gone()) set({ error: `No se pudieron cargar las listas: ${errorMessage(error)}` });
      }
    },

    async onShow() {
      if (!get().loaded) { await controller.init(); return; }
      try {
        await loadSettings();
        await refreshLists();
        const id = get().openId;
        if (id !== null) await loadList(id, false);
      } catch (error) {
        if (!gone()) set({ error: errorMessage(error) });
      }
    },

    async openList(id) {
      await loadList(id, true);
    },

    setMode(mode) {
      if (get().mode === mode) return;
      set({ mode });
    },
    toggleMode() {
      set({ mode: get().mode === "live" ? "edit" : "live" });
    },

    moveCursor(delta) {
      const list = get().list;
      if (!list || get().mode !== "live" || list.items.length === 0) return;
      const cursor = Math.max(0, Math.min(list.items.length - 1, get().cursor + delta));
      if (cursor === get().cursor) return;
      set({ cursor });
      schedulePrefetch();
    },
    setCursor(index) {
      const list = get().list;
      if (!list || index < 0 || index >= list.items.length) return;
      set({ cursor: index });
      schedulePrefetch();
    },

    activate() {
      const list = get().list;
      const item = list?.items[get().cursor];
      if (!list || !item || get().mode !== "live") return;
      if (item.itemType === "note") {
        // Una nota no se proyecta. Solo si la nota lo pide (apagado por defecto) quita el texto de la pantalla; si no, no cambia nada.
        set({ notice: null, atEnd: false });
        if (item.clearOnActivate) clearScreen();
        return;
      }
      projectItemSlide(get().cursor, 0, true);
    },
    nextSlide: () => { stepSlide(1); },
    prevSlide: () => { stepSlide(-1); },
    nextItem: () => { jumpItem(1); },
    prevItem: () => { jumpItem(-1); },
    clear() {
      if (get().mode !== "live") return;
      clearScreen();
    },
    projectSlide(itemId, position) {
      const index = indexOfItem(itemId);
      if (index >= 0) projectItemSlide(index, position, true);
    },
    dismissNotice() {
      set({ notice: null });
    },
    anchor,

    dispose() {
      life.disposed = true;
      sender.cancel();
      if (prefetchTimer !== null) clock.clearTimeout(prefetchTimer);
      if (refreshTimer !== null) clock.clearTimeout(refreshTimer);
      unsubscribers.splice(0).forEach((off) => { off(); });
      fits.forEach((f) => { f.dispose(); });
      fits.clear();
    },
  };
  return controller;
}
