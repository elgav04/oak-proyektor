import { createStore, type StoreApi } from "zustand/vanilla";
import type {
  BibleDto,
  BibleSearchHitDto,
  BibleSettingsPayload,
  BibleSuggestionDto,
  BibleVerseDto,
  CitationDto,
  CommandPayloads,
  CommandType,
  QueryPayloads,
  QueryType,
} from "@/ipc/protocol";
import { createLatestSender, realClock, type Clock, type LatestSender } from "./latest";
import { createBlocksMeasurer } from "./measure";
import { buildPassage, projectionTheme, type BuiltPassage } from "./slide";

/** Lo mínimo del cliente IPC que usa la pestaña (así se prueba con uno falso). */
export interface BibleApi {
  request<T extends QueryType>(type: T, payload: QueryPayloads[T]["request"], timeoutMs?: number): Promise<QueryPayloads[T]["reply"]>;
  send<T extends CommandType>(type: T, payload: CommandPayloads[T]): void;
}

/** ¿Caben estos bloques en el área de texto? La implementación real mide con el layout del navegador (`measure.ts`). */
export type BlocksFit = (blocks: readonly string[]) => boolean;

export interface BibleControllerDeps {
  api: BibleApi;
  /** Crea la medición para un tema; `dispose` la libera. Por defecto, la real (DOM fuera de pantalla). */
  createFit?: (theme: BibleSettingsPayload["theme"]) => { fits: BlocksFit; dispose: () => void };
  clock?: Clock;
  /** Mínimo entre dos envíos de proyección al navegar con flechas (el último siempre llega). */
  sendIntervalMs?: number;
  searchDebounceMs?: number;
  recordDelayMs?: number;
}

export interface PassageView {
  bible: string;
  attribution: string;
  /** Versículos que muestra la lista: el capítulo entero o, si el pasaje cruza capítulos, solo los pedidos. */
  verses: BibleVerseDto[];
}

export interface Projected {
  reference: string;
  page: number;
  pages: number;
}

export interface BibleViewState {
  loaded: boolean;
  bibles: BibleDto[];
  settings: BibleSettingsPayload | null;
  activeBible: string | null;
  query: string;
  suggestions: BibleSuggestionDto[];
  suggestionIndex: number;
  hits: BibleSearchHitDto[];
  hitsTruncated: boolean;
  /** Hay una búsqueda de texto libre disponible para lo escrito (se ofrece junto a las sugerencias de libro). */
  canSearchText: boolean;
  passage: PassageView | null;
  anchor: number;
  active: number;
  built: BuiltPassage | null;
  pageIndex: number;
  projected: Projected | null;
  history: CitationDto[];
  message: string | null;
}

const initialState: BibleViewState = {
  loaded: false,
  bibles: [],
  settings: null,
  activeBible: null,
  query: "",
  suggestions: [],
  suggestionIndex: 0,
  hits: [],
  hitsTruncated: false,
  canSearchText: false,
  passage: null,
  anchor: 0,
  active: 0,
  built: null,
  pageIndex: 0,
  projected: null,
  history: [],
  message: null,
};

interface ProjectionMessage {
  blocks: string[];
}

const errorMessage = (error: unknown): string => (error instanceof Error ? error.message : String(error));
const errorCode = (error: unknown): string => (typeof error === "object" && error !== null && "code" in error ? String(error.code) : "");

/**
 * Controlador de la pestaña de Biblia. NO guarda estado de negocio: el contenido viene del backend (consultas) y lo que se proyecta es un comando; aquí solo
 * hay estado de VISTA (qué se escribió, qué versículo está seleccionado, en qué página). Sin DOM ni React: se prueba con un cliente IPC falso.
 */
export interface BibleController {
  store: StoreApi<BibleViewState>;
  init: () => Promise<void>;
  setQuery: (query: string) => void;
  /** Enter en el cuadro de búsqueda. */
  submitSearch: () => void;
  moveSuggestion: (delta: number) => void;
  pickSuggestion: (bookNumber: number) => void;
  searchText: () => void;
  openHit: (hit: BibleSearchHitDto) => void;
  selectIndex: (index: number, extend?: boolean) => void;
  moveSelection: (delta: number, extend?: boolean) => void;
  /** Enter sobre la selección. */
  project: () => void;
  pageBy: (delta: number) => void;
  /** Esc: quita el texto proyectado. */
  clearProjection: () => void;
  setBible: (abbreviation: string) => void;
  reproject: (entry: CitationDto) => void;
  saveSettings: (next: BibleSettingsPayload) => Promise<void>;
  saveAttribution: (bible: string, attribution: string) => Promise<void>;
  dismissMessage: () => void;
  dispose: () => void;
}

export function createBibleController(deps: BibleControllerDeps): BibleController {
  const { api } = deps;
  const clock = deps.clock ?? realClock;
  const store = createStore<BibleViewState>(() => ({ ...initialState }));
  const get = store.getState;
  const set = store.setState;

  let fitCache: { key: string; fits: BlocksFit; dispose: () => void } | null = null;
  const builtCache = new Map<string, BuiltPassage>();
  let queryToken = 0;
  let queryTimer: unknown = null;
  let recordTimer: unknown = null;
  let lastRecorded = "";
  // Objeto (no variable suelta): TypeScript no estrecha `gone()` a «false» tras un await, que es justo cuando puede haber cambiado.
  const life = { disposed: false };
  const gone = (): boolean => life.disposed;

  const sender: LatestSender<ProjectionMessage> = createLatestSender<ProjectionMessage>(
    ({ blocks }) => {
      const s = get().settings;
      if (!s) return;
      api.send("output.showContent", { blocks, theme: projectionTheme(s.theme), transitionMs: s.transitionMs });
    },
    deps.sendIntervalMs ?? 60,
    clock,
  );

  // ───────── medición y páginas ─────────

  const themeKey = (t: BibleSettingsPayload["theme"]): string => JSON.stringify([t.fontFamily, t.fontSize, t.lineHeight, t.outlineWidth, t.shadowBlur, t.shadowOffset, t.marginTop, t.marginBottom, t.marginLeft, t.marginRight, t.align]);

  const fitFor = (theme: BibleSettingsPayload["theme"]): BlocksFit => {
    const key = themeKey(theme);
    if (fitCache?.key === key) return fitCache.fits;
    fitCache?.dispose();
    const made = (deps.createFit ?? ((t) => {
      const m = createBlocksMeasurer(t);
      return { fits: (blocks: readonly string[]) => m.fits(blocks), dispose: () => { m.dispose(); } };
    }))(theme);
    fitCache = { key, ...made };
    builtCache.clear();
    return made.fits;
  };

  const bibleInfo = (abbreviation: string): BibleDto | undefined => get().bibles.find((b) => b.abbreviation === abbreviation);

  /** Páginas de la selección actual (medición real; memorizadas por pasaje y ajustes: moverse de ida y vuelta no vuelve a medir). */
  const computeBuilt = (): BuiltPassage | null => {
    const { passage, settings, anchor, active } = get();
    if (!passage || !settings || passage.verses.length === 0) return null;
    const lo = Math.min(anchor, active);
    const hi = Math.max(anchor, active);
    const verses = passage.verses.slice(lo, hi + 1);
    if (verses.length === 0) return null;
    const info = bibleInfo(passage.bible);
    const shortName = settings.shortNames.find((n) => n.bible === passage.bible)?.shortName ?? passage.bible;
    const attribution = info?.attribution ?? passage.attribution;
    const options = { bibleName: shortName, attribution, showAttribution: settings.showAttribution, superscript: settings.superscriptNumbers };
    const key = JSON.stringify([passage.bible, verses.map((v) => [v.bookNumber, v.chapter, v.verseNumber]), options, themeKey(settings.theme)]);
    const fits = fitFor(settings.theme);   // puede vaciar la memoria si cambió el tema
    const cached = builtCache.get(key);
    if (cached) return cached;
    const built = buildPassage(verses, options, fits);
    if (builtCache.size > 60) builtCache.clear();
    builtCache.set(key, built);
    return built;
  };

  const refreshBuilt = (): void => {
    const built = computeBuilt();
    set({ built, pageIndex: 0 });
  };

  // ───────── proyección ─────────

  const scheduleRecord = (reference: string, bible: string): void => {
    if (reference === lastRecorded) return;
    if (recordTimer !== null) clock.clearTimeout(recordTimer);
    recordTimer = clock.setTimeout(() => {
      recordTimer = null;
      lastRecorded = reference;
      // Anotar en el historial = una consulta con `record: true` (el backend aplica el límite configurado). Un fallo del historial no afecta a la proyección.
      void api
        .request("bible.lookup", { reference, bible, record: true })
        .then(() => api.request("bible.history", { limit: 30 }))
        .then((h) => { if (!gone()) set({ history: h.entries }); })
        .catch(() => undefined);
    }, deps.recordDelayMs ?? 700);
  };

  const projectNow = (immediate: boolean): void => {
    const { built, pageIndex, passage } = get();
    const page = built?.pages[pageIndex];
    if (!built || !page || !passage) return;
    const message = { blocks: page };
    if (immediate) sender.now(message);
    else sender.push(message);
    set({ projected: { reference: built.reference, page: pageIndex, pages: built.pages.length } });
    scheduleRecord(built.reference, passage.bible);
  };

  // ───────── carga de pasajes ─────────

  const showPassage = (view: PassageView, anchor: number, active: number): void => {
    set({ passage: view, anchor, active, suggestions: [], hits: [], canSearchText: false, message: null });
    refreshBuilt();
  };

  const bibleArg = (): string | null => get().activeBible;

  const loadChapter = async (bookNumber: number, chapter: number, select: (verses: BibleVerseDto[]) => [number, number]): Promise<void> => {
    const bible = bibleArg();
    const p = await api.request("bible.chapter", { bible, bookNumber, chapter });
    if (gone()) return;
    const [a, b] = select(p.verses);
    showPassage({ bible: p.bible, attribution: p.attribution, verses: p.verses }, a, b);
  };

  const indexOfVerse = (verses: BibleVerseDto[], chapter: number, verse: number): number =>
    Math.max(0, verses.findIndex((v) => v.chapter === chapter && v.verseNumber === verse));

  /** ¿Lo escrito pide un capítulo entero («Sal 23») y no un rango? Entonces se selecciona solo el primer versículo (proyectar 31 de golpe no tiene sentido). */
  const isWholeChapter = (query: string, asked: BibleVerseDto[], chapter: BibleVerseDto[]): boolean =>
    asked.length === chapter.length && asked.length > 1 && !/\d\s*[:.,\-]\s*\d/.test(query) && !/\d\s*-/.test(query);

  const lookupReference = async (query: string, token: number): Promise<boolean> => {
    const bible = bibleArg();
    let passage;
    try {
      passage = await api.request("bible.lookup", { reference: query, bible, record: false });
    } catch (error) {
      if (token !== queryToken || gone()) return true;
      if (errorCode(error) === "bible.invalidReference") return false;
      set({ message: errorMessage(error), suggestions: [], hits: [] });
      return true;
    }
    if (token !== queryToken || gone() || passage.verses.length === 0) return true;
    const asked = passage.verses;
    const first = asked[0];
    const last = asked[asked.length - 1];
    if (!first || !last) return true;
    const sameChapter = asked.every((v) => v.bookNumber === first.bookNumber && v.chapter === first.chapter);
    if (!sameChapter) {
      showPassage({ bible: passage.bible, attribution: passage.attribution, verses: asked }, 0, asked.length - 1);
      return true;
    }
    try {
      const chapter = await api.request("bible.chapter", { bible, bookNumber: first.bookNumber, chapter: first.chapter });
      if (token !== queryToken || gone()) return true;
      const a = indexOfVerse(chapter.verses, first.chapter, first.verseNumber);
      const b = isWholeChapter(query, asked, chapter.verses) ? a : indexOfVerse(chapter.verses, last.chapter, last.verseNumber);
      showPassage({ bible: chapter.bible, attribution: chapter.attribution, verses: chapter.verses }, a, b);
    } catch {
      showPassage({ bible: passage.bible, attribution: passage.attribution, verses: asked }, 0, asked.length - 1);
    }
    return true;
  };

  const runSearch = async (query: string, token: number): Promise<void> => {
    try {
      const result = await api.request("bible.search", { query, bible: bibleArg(), limit: 50 });
      if (token !== queryToken || gone()) return;
      set({ hits: result.hits, suggestionIndex: 0, hitsTruncated: result.truncated, message: result.hits.length === 0 ? "Sin resultados." : null });
    } catch (error) {
      if (token !== queryToken || gone()) return;
      set({ hits: [], message: errorMessage(error) });
    }
  };

  const runQuery = async (): Promise<void> => {
    const query = get().query.trim();
    const token = ++queryToken;
    if (query.length === 0) {
      set({ suggestions: [], suggestionIndex: 0, hits: [], canSearchText: false, message: null });
      return;
    }
    if (/\d/.test(query)) {
      // Con números se interpreta como referencia; si el parser no la entiende («40 días»), se busca como texto.
      const handled = await lookupReference(query, token);
      if (!handled && token === queryToken && !gone()) {
        set({ suggestions: [], canSearchText: false });
        await runSearch(query, token);
      }
      return;
    }
    try {
      const result = await api.request("bible.suggest", { prefix: query, limit: 8 });
      if (token !== queryToken || gone()) return;
      set({ suggestions: result.suggestions, suggestionIndex: 0, canSearchText: result.suggestions.length > 0, hits: [], message: null });
      if (result.suggestions.length === 0) await runSearch(query, token);
    } catch (error) {
      if (token === queryToken && !gone()) set({ message: errorMessage(error) });
    }
  };

  const initialSelection = (): [number, number] => [0, 0];

  // ───────── acciones ─────────

  const controller: BibleController = {
    store,

    async init() {
      try {
        const [list, settings, history] = await Promise.all([
          api.request("bible.list", {}),
          api.request("bible.getSettings", {}),
          api.request("bible.history", { limit: 30 }),
        ]);
        if (gone()) return;
        const installed = list.bibles.map((b) => b.abbreviation);
        const preferred = settings.defaultBible && installed.includes(settings.defaultBible) ? settings.defaultBible : (installed[0] ?? null);
        set({ loaded: true, bibles: list.bibles, settings, activeBible: preferred, history: history.entries, message: list.bibles.length === 0 ? "No hay ninguna Biblia instalada." : null });
      } catch (error) {
        if (!gone()) set({ loaded: true, message: errorMessage(error) });
      }
    },

    setQuery(query) {
      set({ query });
      if (queryTimer !== null) clock.clearTimeout(queryTimer);
      queryTimer = clock.setTimeout(() => {
        queryTimer = null;
        void runQuery();
      }, deps.searchDebounceMs ?? 180);
    },

    submitSearch() {
      if (queryTimer !== null) {
        clock.clearTimeout(queryTimer);
        queryTimer = null;
      }
      const s = get();
      if (s.suggestions.length > 0) {
        const picked = s.suggestions[s.suggestionIndex];
        if (picked) controller.pickSuggestion(picked.bookNumber);
        return;
      }
      if (s.hits.length > 0) {
        const chosen = s.hits[s.suggestionIndex];
        if (chosen) controller.openHit(chosen);
        return;
      }
      // Una referencia: se resuelve y se proyecta (type «jn 3:16» + Enter = en pantalla).
      const token = ++queryToken;
      const query = s.query.trim();
      if (query.length === 0) return;
      void (async () => {
        if (/\d/.test(query)) {
          const handled = await lookupReference(query, token);
          if (handled && token === queryToken && get().passage && !get().message) projectNow(true);
          else if (!handled && token === queryToken) await runSearch(query, token);
        } else {
          await runSearch(query, token);
        }
      })();
    },

    moveSuggestion(delta) {
      const { suggestions, suggestionIndex, hits } = get();
      if (suggestions.length === 0 && hits.length === 0) return;
      const size = suggestions.length > 0 ? suggestions.length : hits.length;
      set({ suggestionIndex: Math.max(0, Math.min(size - 1, suggestionIndex + delta)) });
    },

    pickSuggestion(bookNumber) {
      const token = ++queryToken;
      void loadChapter(bookNumber, 1, initialSelection).catch((error: unknown) => {
        if (token === queryToken && !gone()) set({ message: errorMessage(error) });
      });
    },

    searchText() {
      const query = get().query.trim();
      if (query.length === 0) return;
      const token = ++queryToken;
      set({ suggestions: [], canSearchText: false });
      void runSearch(query, token);
    },

    openHit(hit) {
      const token = ++queryToken;
      void loadChapter(hit.bookNumber, hit.chapter, (verses) => {
        const i = indexOfVerse(verses, hit.chapter, hit.verseNumber);
        return [i, i];
      }).catch((error: unknown) => {
        if (token === queryToken && !gone()) set({ message: errorMessage(error) });
      });
    },

    selectIndex(index, extend = false) {
      const { passage, active, anchor } = get();
      if (!passage) return;
      const i = Math.max(0, Math.min(passage.verses.length - 1, index));
      if (i === active && (extend || anchor === i)) return;
      set(extend ? { active: i } : { anchor: i, active: i });
      refreshBuilt();
      if (get().settings?.projectOnNavigate) projectNow(false);
    },

    moveSelection(delta, extend = false) {
      controller.selectIndex(get().active + delta, extend);
    },

    project() {
      projectNow(true);
    },

    pageBy(delta) {
      const { built, pageIndex } = get();
      if (!built) return;
      const next = Math.max(0, Math.min(built.pages.length - 1, pageIndex + delta));
      if (next === pageIndex) return;   // en el límite no pasa nada (ni se vuelve a proyectar)
      set({ pageIndex: next });
      projectNow(false);
    },

    clearProjection() {
      sender.cancel();
      const s = get().settings;
      api.send("output.clearContent", { transitionMs: s?.transitionMs ?? 280 });
      set({ projected: null });
    },

    setBible(abbreviation) {
      const { passage, anchor, active } = get();
      set({ activeBible: abbreviation });
      builtCache.clear();
      if (!passage) return;
      const first = passage.verses[Math.min(anchor, active)];
      const last = passage.verses[Math.max(anchor, active)];
      if (!first || !last) return;
      const token = ++queryToken;
      void loadChapter(first.bookNumber, first.chapter, (verses) => [indexOfVerse(verses, first.chapter, first.verseNumber), indexOfVerse(verses, last.chapter, last.verseNumber)]).catch(
        (error: unknown) => {
          if (token === queryToken && !gone()) set({ message: errorMessage(error) });
        },
      );
    },

    reproject(entry) {
      const token = ++queryToken;
      set({ activeBible: bibleInfo(entry.bible) ? entry.bible : get().activeBible });
      void (async () => {
        try {
          const passage = await api.request("bible.lookup", { reference: entry.label, bible: entry.bible, record: false });
          if (token !== queryToken || gone() || passage.verses.length === 0) return;
          const verses = passage.verses;
          const first = verses[0];
          if (first && verses.every((v) => v.chapter === first.chapter && v.bookNumber === first.bookNumber)) {
            const chapter = await api.request("bible.chapter", { bible: entry.bible, bookNumber: first.bookNumber, chapter: first.chapter });
            if (token !== queryToken || gone()) return;
            const a = indexOfVerse(chapter.verses, first.chapter, first.verseNumber);
            const lastV = verses[verses.length - 1] ?? first;
            showPassage({ bible: chapter.bible, attribution: chapter.attribution, verses: chapter.verses }, a, indexOfVerse(chapter.verses, lastV.chapter, lastV.verseNumber));
          } else {
            showPassage({ bible: passage.bible, attribution: passage.attribution, verses }, 0, verses.length - 1);
          }
          lastRecorded = "";   // volver a proyectar una cita la sube al principio del historial
          projectNow(true);
        } catch (error) {
          if (token === queryToken && !gone()) set({ message: errorMessage(error) });
        }
      })();
    },

    async saveSettings(next) {
      api.send("bible.setSettings", next);
      set({ settings: next });
      builtCache.clear();
      refreshBuilt();
      try {
        // Los comandos y las consultas comparten carril (FIFO): esta lectura ya ve lo guardado (o, si el backend lo rechazó, el valor anterior).
        const confirmed = await api.request("bible.getSettings", {});
        if (!gone()) {
          set({ settings: confirmed });
          builtCache.clear();
          refreshBuilt();
        }
      } catch (error) {
        if (!gone()) set({ message: errorMessage(error) });
      }
    },

    async saveAttribution(bible, attribution) {
      api.send("bible.setAttribution", { bible, attribution });
      try {
        const list = await api.request("bible.list", {});
        if (!gone()) {
          set({ bibles: list.bibles });
          builtCache.clear();
          refreshBuilt();
        }
      } catch (error) {
        if (!gone()) set({ message: errorMessage(error) });
      }
    },

    dismissMessage() {
      set({ message: null });
    },

    dispose() {
      life.disposed = true;
      sender.cancel();
      if (queryTimer !== null) clock.clearTimeout(queryTimer);
      if (recordTimer !== null) clock.clearTimeout(recordTimer);
      fitCache?.dispose();
      fitCache = null;
    },
  };
  return controller;
}
