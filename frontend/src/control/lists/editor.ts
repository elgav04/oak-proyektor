import { createStore, type StoreApi } from "zustand/vanilla";
import type { BibleDto, CommandPayloads, CommandType, PlaylistChangedPayload, PlaylistItemDto, PlaylistNewItemDto, SongSummaryDto } from "@/ipc/protocol";
import { realClock, type Clock } from "../bible/latest";
import { localDate } from "./active";
import type { ListApi, ListController } from "./controller";
import { describeEditError, dropIndex, itemAsNew, movedOrder, orderedItems, suggestedCopyDate } from "./editModel";

/**
 * Edición de la lista (modo EDITAR). Cada edición es UN comando pequeño del backend (`playlist.*`), con la revisión de lo que el operador VE: si caducó
 * (`playlist.conflict`) no se aplica nada, se avisa y se vuelve a leer la lista SIN perder lo que había escrito en los formularios. Las ediciones se encadenan de una en
 * una (cada una con la revisión que dejó la anterior), así que una ráfaga de Alt+↓ o dos arrastres seguidos no se pisan. El orden se muestra de forma optimista
 * (`localOrder`) mientras hay movimientos sin confirmar y vuelve a salir del backend al terminar. NADA de esto toca la salida ni la proyección.
 */

export type EditForm =
  | { kind: "new"; name: string; date: string }
  | { kind: "meta"; name: string; date: string }
  | { kind: "duplicate"; name: string; date: string }
  | { kind: "delete" }
  | { kind: "note"; itemId: number | null; title: string; text: string; clear: boolean };

export interface EditMessage {
  kind: "info" | "error" | "conflict";
  text: string;
}

export interface EditorViewState {
  form: EditForm | null;
  message: EditMessage | null;
  /** Ediciones en cola o enviándose. */
  busy: number;
  selectedId: number | null;
  /** Orden optimista (ids) mientras hay movimientos sin confirmar; null = el de la lista. */
  localOrder: number[] | null;
  /** Elementos que se acaban de quitar y el backend aún no confirmó: se esconden de lo mostrado. */
  hidden: number[];
  query: string;
  results: SongSummaryDto[];
  resultIndex: number;
  searching: boolean;
  resultsTruncated: boolean;
  /** Elemento faltante que se está reemplazando: la próxima canción que se elija lo sustituye. */
  replaceTarget: number | null;
  /** Lo último que se quitó, para «Deshacer». */
  lastRemoved: { dto: PlaylistNewItemDto; index: number; title: string } | null;
  bibles: BibleDto[];
}

const initialState: EditorViewState = {
  form: null, message: null, busy: 0, selectedId: null, localOrder: null, hidden: [], query: "", results: [], resultIndex: 0, searching: false, resultsTruncated: false,
  replaceTarget: null, lastRemoved: null, bibles: [],
};

class EditCommandError extends Error {
  constructor(readonly code: string, message: string) {
    super(message);
  }
}

const errorCode = (e: unknown): string => (typeof e === "object" && e !== null && "code" in e ? String(e.code) : "");
const errorText = (e: unknown): string => (e instanceof Error ? e.message : typeof e === "object" && e !== null && "message" in e ? String(e.message) : String(e));

export interface ListEditorDeps {
  api: ListApi;
  list: ListController;
  clock?: Clock;
  /** Cuánto se espera la confirmación (`playlist.changed`) de un comando antes de darlo por perdido. */
  confirmTimeoutMs?: number;
  searchDebounceMs?: number;
  today?: () => string;
}

export interface AddResult {
  ok: boolean;
  message: string;
}

export interface ListEditor {
  store: StoreApi<EditorViewState>;
  /** Al entrar en modo EDITAR: lee las Biblias instaladas y la lista de canciones. */
  enter(): Promise<void>;
  // listas
  openForm(kind: "new" | "meta" | "duplicate" | "delete" | "note", itemId?: number): void;
  setField(field: "name" | "date" | "title" | "text", value: string): void;
  setClear(value: boolean): void;
  closeForm(): void;
  submitForm(): void;
  // elementos
  select(id: number): void;
  selectBy(delta: number): void;
  moveItem(itemId: number, toIndex: number): void;
  dropItem(activeId: number, overId: number): void;
  moveSelected(delta: number): void;
  removeItem(itemId: number): void;
  removeSelected(): void;
  duplicateItem(itemId: number): void;
  duplicateSelected(): void;
  undo(): void;
  setPassageBible(itemId: number, bible: string): void;
  // búsqueda de canciones
  setQuery(query: string): void;
  moveResult(delta: number): void;
  addSong(songId: number, where: "end" | "after"): void;
  addResult(where: "end" | "after"): void;
  startReplace(itemId: number | null): void;
  /** Añade un pasaje (desde la pestaña de Biblia) a la lista abierta. Devuelve qué pasó para decírselo al operador. */
  addPassage(item: PlaylistNewItemDto, label: string): Promise<AddResult>;
  /** Hay algo pendiente que se perdería al pasar a EN VIVO (motivo) o null. */
  pendingReason(): string | null;
  discard(): void;
  dismissMessage(): void;
  /** Un aviso de error en la barra del editor. */
  warn(text: string): void;
  displayedItems(): PlaylistItemDto[];
  dispose(): void;
}

export function createListEditor(deps: ListEditorDeps): ListEditor {
  const { api, list } = deps;
  const clock = deps.clock ?? realClock;
  const store = createStore<EditorViewState>(() => ({ ...initialState }));
  const get = store.getState;
  const set = store.setState;
  const life = { disposed: false };
  const gone = (): boolean => life.disposed;
  const today = deps.today ?? (() => localDate(new Date()));

  // ───────── la revisión de lo que el operador VE ─────────
  const known = { listId: null as number | null, revision: 0 };
  let pendingSelect: number | null = null;
  const sync = (): void => {
    const l = list.store.getState().list;
    if (!l) { known.listId = null; return; }
    if (l.id !== known.listId) { known.listId = l.id; known.revision = l.revision; set({ selectedId: null, localOrder: null, hidden: [] }); }
    else known.revision = Math.max(known.revision, l.revision);
    const sel = get().selectedId;
    const items = l.items;
    // Un elemento recién añadido puede no estar aún en la lista leída: no se le quita la selección hasta que aparezca (o termine la edición y se relea).
    if (sel !== null && sel === pendingSelect) { if (items.some((i) => i.id === sel)) pendingSelect = null; return; }
    if (sel === null || !items.some((i) => i.id === sel)) set({ selectedId: items[Math.min(list.store.getState().cursor, items.length - 1)]?.id ?? null });
  };
  let lastMode = list.store.getState().mode;
  const unsubscribe = list.store.subscribe((s) => {
    sync();
    if (s.mode !== lastMode) {
      lastMode = s.mode;
      // Al volver a EN VIVO el cursor cae en el elemento seleccionado.
      if (s.mode === "live") {
        const index = s.list?.items.findIndex((i) => i.id === get().selectedId) ?? -1;
        if (index >= 0) list.setCursor(index);
      }
    }
  });
  sync();
  list.setLeaveGuard(() => pendingReason(), () => { discard(); });

  const displayed = (): PlaylistItemDto[] => orderedItems(list.store.getState().list?.items ?? [], get().localOrder, get().hidden);
  const displayedIds = (): number[] => displayed().map((i) => i.id);
  const openId = (): number | null => list.store.getState().openId;
  const notify = (message: EditMessage | null): void => { set({ message }); };

  // ───────── enviar un comando y esperar SU resultado ─────────
  interface Waiter { resolve: (p: PlaylistChangedPayload) => void; reject: (e: Error) => void; timer: unknown }
  const waiters = new Map<string, Waiter>();
  const recent = new Map<string, PlaylistChangedPayload | Error>();
  const remember = (id: string, value: PlaylistChangedPayload | Error): void => {
    recent.set(id, value);
    if (recent.size > 50) { const first = recent.keys().next().value; if (first !== undefined) recent.delete(first); }
  };
  const offs = [
    api.subscribe("playlist.changed", (p) => {
      const w = waiters.get(p.commandId);
      if (!w) { remember(p.commandId, p); return; }
      clock.clearTimeout(w.timer); waiters.delete(p.commandId); w.resolve(p);
    }),
    api.subscribe("command.failed", (p) => {
      if (!p.commandType.startsWith("playlist.")) return;
      const error = new EditCommandError(p.error.code, p.error.message);
      const w = waiters.get(p.commandId);
      if (!w) { remember(p.commandId, error); return; }
      clock.clearTimeout(w.timer); waiters.delete(p.commandId); w.reject(error);
    }),
  ];

  const send = <T extends CommandType>(
    type: T, payload: CommandPayloads[T],
  ): Promise<PlaylistChangedPayload> => {
    const id = api.send(type, payload);
    const early = recent.get(id);
    if (early) { recent.delete(id); return early instanceof Error ? Promise.reject(early) : Promise.resolve(early); }
    return new Promise<PlaylistChangedPayload>((resolve, reject) => {
      const timer = clock.setTimeout(() => { waiters.delete(id); reject(new EditCommandError("timeout", "sin confirmación")); }, deps.confirmTimeoutMs ?? 20_000);
      waiters.set(id, { resolve, reject, timer });
    });
  };
  /** Un cambio PROPIO confirmado: si subió la revisión exactamente en uno, es el mismo estado que el operador ve más su cambio. */
  const bump = (r: PlaylistChangedPayload): void => { if (r.revision === known.revision + 1) known.revision = r.revision; };

  // ───────── cola: una edición a la vez ─────────
  let chain: Promise<void> = Promise.resolve();
  const onError = async (error: unknown): Promise<void> => {
    if (gone()) return;
    const d = describeEditError(errorCode(error), errorText(error));
    set({ message: { kind: d.kind, text: d.text }, localOrder: null, hidden: [] });
    await list.reload();
  };
  const afterIdle = async (): Promise<void> => {
    await list.reload();
    if (!gone() && get().busy === 0) { pendingSelect = null; set({ localOrder: null, hidden: [] }); }
  };
  const enqueue = (run: () => Promise<void>): Promise<void> => {
    set((s) => ({ busy: s.busy + 1 }));
    const p = chain.then(run).catch(onError).finally(() => {
      if (gone()) return;
      set((s) => ({ busy: s.busy - 1 }));
      if (get().busy === 0) void afterIdle();
    });
    chain = p.then(() => undefined, () => undefined);
    return p;
  };

  // ───────── formularios y listas ─────────
  const currentList = () => list.store.getState().list;
  const defaultName = (kind: "new" | "meta" | "duplicate"): string => (kind === "new" ? "" : kind === "meta" ? (currentList()?.name ?? "") : `Copia de ${currentList()?.name ?? ""}`.trim());

  const doCreate = (name: string, date: string): Promise<void> =>
    enqueue(async () => {
      const r = await send("playlist.create", { name, serviceDate: date.trim() === "" ? null : date.trim(), notes: null });
      set({ form: null });
      await list.openList(r.playlistId);
      notify({ kind: "info", text: `Lista «${name.trim()}» creada${date.trim() === "" ? " (sin fecha: es una plantilla)" : ""}.` });
    });

  const doMeta = (name: string, date: string): Promise<void> =>
    enqueue(async () => {
      const l = currentList();
      const id = openId();
      if (!l || id === null) return;
      const r = await send("playlist.update", { id, name, serviceDate: date.trim() === "" ? null : date.trim(), notes: l.notes === "" ? null : l.notes, expectedRevision: known.revision });
      bump(r);
      set({ form: null });
      notify({ kind: "info", text: "Lista actualizada." });
    });

  const doDuplicate = (name: string, date: string): Promise<void> =>
    enqueue(async () => {
      const l = currentList();
      const id = openId();
      if (!l || id === null) return;
      const missing = l.items.filter((i) => i.isMissing).map((i) => `«${i.title}»`);
      const r = await send("playlist.duplicate", { id, name, serviceDate: date.trim() === "" ? null : date.trim() });
      set({ form: null });
      await list.openList(r.playlistId);
      const omitted = r.skipped > 0 ? ` Se OMITIERON ${String(r.skipped)} porque faltaba su canción${missing.length > 0 ? `: ${missing.join(", ")}` : ""} (siguen en la lista original).` : "";
      notify({ kind: r.skipped > 0 ? "error" : "info", text: `Lista duplicada: se copiaron ${String(r.copied)} elemento(s).${omitted}` });
    });

  const doDelete = (): Promise<void> =>
    enqueue(async () => {
      const id = openId();
      const name = currentList()?.name ?? "";
      if (id === null) return;
      await send("playlist.delete", { id });
      set({ form: null });
      await list.reload();
      const next = list.store.getState().lists[0];
      if (next) await list.openList(next.id);
      notify({ kind: "info", text: `Lista «${name}» borrada.` });
    });

  const doNote = (form: Extract<EditForm, { kind: "note" }>, where: "end" | "after"): Promise<void> =>
    enqueue(async () => {
      const id = openId();
      if (id === null) return;
      if (form.itemId === null) {
        const r = await send("playlist.addItem", { playlistId: id, item: { itemType: "note", songId: null, bookNumber: null, chapter: null, verseStart: null, verseEnd: null, bibleAbbreviation: null, title: form.title.trim(), note: form.text.trim() === "" ? null : form.text.trim(), clearOnActivate: form.clear }, position: positionFor(where) });
        bump(r);
        if (r.itemId !== null) { pendingSelect = r.itemId; set({ selectedId: r.itemId }); }
      } else {
        const r = await send("playlist.updateItem", { playlistId: id, itemId: form.itemId, title: form.title.trim(), note: form.text.trim(), clearOnActivate: form.clear, bibleAbbreviation: null, expectedRevision: known.revision });
        bump(r);
      }
      set({ form: null });
    });

  /** Posición para un elemento nuevo: al final (null) o justo después del seleccionado, según el orden que se ve. */
  const positionFor = (where: "end" | "after"): number | null => {
    if (where === "end") return null;
    const ids = displayedIds();
    const at = ids.indexOf(get().selectedId ?? -1);
    return at < 0 ? null : at + 1;
  };

  // ───────── elementos ─────────
  const moveItem = (itemId: number, toIndex: number): void => {
    const ids = displayedIds();
    const next = movedOrder(ids, itemId, toIndex);
    if (next.every((id, i) => id === ids[i])) return;
    const target = next.indexOf(itemId);
    set({ localOrder: next, selectedId: itemId });
    const id = openId();
    if (id === null) return;
    void enqueue(async () => {
      bump(await send("playlist.moveItem", { playlistId: id, itemId, toIndex: target, expectedRevision: known.revision }));
    });
  };

  const removeItem = (itemId: number): void => {
    const items = displayed();
    const index = items.findIndex((i) => i.id === itemId);
    const item = items[index];
    const id = openId();
    if (!item || id === null) return;
    const dto = itemAsNew(item);
    const ids = items.map((i) => i.id).filter((x) => x !== itemId);
    set({ localOrder: ids, hidden: [...get().hidden, itemId], selectedId: ids[Math.min(index, ids.length - 1)] ?? null });
    void enqueue(async () => {
      bump(await send("playlist.removeItem", { playlistId: id, itemId, expectedRevision: known.revision }));
      set({ lastRemoved: dto ? { dto, index, title: item.title } : null, message: { kind: "info", text: `Se quitó «${item.title}» de la lista.${dto ? " Ctrl+Z lo recupera." : ""}` } });
    });
  };

  const addItem = (dto: PlaylistNewItemDto, position: number | null, replaceId: number | null = null): Promise<void> =>
    enqueue(async () => {
      const id = openId();
      if (id === null) throw new EditCommandError("playlist.notFound", "No hay una lista abierta.");
      const r = await send("playlist.addItem", { playlistId: id, item: dto, position });
      bump(r);
      if (r.itemId !== null) { pendingSelect = r.itemId; set({ selectedId: r.itemId }); }
      if (replaceId !== null) {
        try {
          bump(await send("playlist.removeItem", { playlistId: id, itemId: replaceId, expectedRevision: known.revision }));
        } catch (error) {
          set({ message: { kind: "error", text: `Se añadió la canción nueva, pero NO se pudo quitar la que faltaba: ${describeEditError(errorCode(error), errorText(error)).text}` } });
          await list.reload();
          return;
        }
      }
    });

  const songDto = (songId: number): PlaylistNewItemDto => ({ itemType: "song", songId, bookNumber: null, chapter: null, verseStart: null, verseEnd: null, bibleAbbreviation: null, title: null, note: null, clearOnActivate: false });

  // ───────── búsqueda de canciones ─────────
  let searchToken = 0;
  let searchTimer: unknown = null;
  const runSearch = async (query: string): Promise<void> => {
    const token = ++searchToken;
    set({ searching: true });
    try {
      const q = query.trim();
      const reply = q === "" ? await api.request("song.list", {}) : await api.request("song.search", { query: q, limit: 30 });
      if (gone() || token !== searchToken) return;
      const songs = reply.songs.slice(0, 30);
      set({ results: songs, resultIndex: 0, searching: false, resultsTruncated: reply.truncated || reply.songs.length > 30 });
    } catch (error) {
      if (gone() || token !== searchToken) return;
      set({ searching: false, results: [], message: { kind: "error", text: `No se pudo buscar canciones: ${errorText(error)}` } });
    }
  };

  const discard = (): void => {
    searchToken++;
    set({ form: null, replaceTarget: null, localOrder: null, hidden: [], query: "", message: null });
  };

  function pendingReason(): string | null {
    if (get().busy > 0) return "Se está guardando una edición de la lista: espera a que termine.";
    const f = get().form;
    if (f) {
      const what = f.kind === "new" ? "«Nueva lista»" : f.kind === "meta" ? "«Renombrar o cambiar la fecha»" : f.kind === "duplicate" ? "«Duplicar»" : f.kind === "delete" ? "«Borrar la lista» (sin confirmar)" : f.itemId === null ? "«Nueva nota»" : "la edición de una nota";
      return `Hay un formulario sin terminar: ${what}.`;
    }
    return null;
  }

  const editor: ListEditor = {
    store,

    async enter() {
      try {
        const [bibles, songs] = await Promise.all([api.request("bible.list", {}), api.request("song.list", {})]);
        if (gone()) return;
        set({ bibles: bibles.bibles, results: songs.songs.slice(0, 30), resultsTruncated: songs.truncated || songs.songs.length > 30, resultIndex: 0 });
      } catch { /* sin Biblias o sin canciones: el resto del editor funciona */ }
      sync();
    },

    openForm(kind, itemId) {
      if (kind === "note") {
        const item = itemId === undefined ? undefined : displayed().find((i) => i.id === itemId);
        set({ form: { kind: "note", itemId: item?.id ?? null, title: item?.title ?? "", text: item?.note ?? "", clear: item?.clearOnActivate ?? false } });
        return;
      }
      if (kind === "delete") { set({ form: { kind: "delete" } }); return; }
      const l = currentList();
      const date = kind === "new" ? "" : kind === "meta" ? (l?.serviceDate ?? "") : suggestedCopyDate(l?.serviceDate ?? null, today());
      set({ form: { kind, name: defaultName(kind), date } });
    },
    setField(field, value) {
      set((s) => (s.form && s.form.kind !== "delete" ? { form: { ...s.form, [field]: value } as EditForm } : s));
    },
    setClear(value) {
      set((s) => (s.form?.kind === "note" ? { form: { ...s.form, clear: value } } : s));
    },
    closeForm() {
      set({ form: null });
    },
    submitForm() {
      const f = get().form;
      if (!f) return;
      if (f.kind === "delete") { void doDelete(); return; }
      if (f.kind === "note") {
        if (f.title.trim() === "") { notify({ kind: "error", text: "La nota necesita un título («Ofrendas», «Anuncios»…)." }); return; }
        void doNote(f, "after");
        return;
      }
      if (f.name.trim() === "") { notify({ kind: "error", text: "La lista necesita un nombre." }); return; }
      if (f.date.trim() !== "" && !/^\d{4}-\d{2}-\d{2}$/.test(f.date.trim())) { notify({ kind: "error", text: "La fecha debe ser un día válido (o déjala vacía: sin fecha es una plantilla)." }); return; }
      if (f.kind === "new") void doCreate(f.name, f.date);
      else if (f.kind === "meta") void doMeta(f.name, f.date);
      else void doDuplicate(f.name, f.date);
    },

    select(id) {
      if (displayed().some((i) => i.id === id)) set({ selectedId: id });
    },
    selectBy(delta) {
      const ids = displayedIds();
      if (ids.length === 0) return;
      const at = ids.indexOf(get().selectedId ?? -1);
      const next = ids[Math.max(0, Math.min(ids.length - 1, (at < 0 ? 0 : at) + delta))];
      if (next !== undefined) set({ selectedId: next });
    },
    moveItem,
    dropItem(activeId, overId) {
      const to = dropIndex(displayedIds(), activeId, overId);
      if (to !== null) moveItem(activeId, to);
    },
    moveSelected(delta) {
      const ids = displayedIds();
      const sel = get().selectedId;
      const at = sel === null ? -1 : ids.indexOf(sel);
      if (at < 0 || sel === null) return;
      moveItem(sel, at + delta);
    },
    removeItem,
    removeSelected() {
      const sel = get().selectedId;
      if (sel !== null) removeItem(sel);
    },
    duplicateItem(itemId) {
      const item = displayed().find((i) => i.id === itemId);
      if (!item) return;
      const dto = itemAsNew(item);
      if (!dto) { notify({ kind: "error", text: `«${item.title}» no se puede duplicar (falta la canción).` }); return; }
      const at = displayedIds().indexOf(itemId);
      void addItem(dto, at + 1);
    },
    duplicateSelected() {
      const sel = get().selectedId;
      if (sel !== null) editor.duplicateItem(sel);
    },
    undo() {
      const removed = get().lastRemoved;
      if (!removed) return;
      set({ lastRemoved: null });
      void addItem(removed.dto, removed.index).then(() => { notify({ kind: "info", text: `Se recuperó «${removed.title}».` }); });
    },
    setPassageBible(itemId, bible) {
      const id = openId();
      if (id === null || bible.trim() === "") return;
      void enqueue(async () => {
        bump(await send("playlist.updateItem", { playlistId: id, itemId, title: null, note: null, clearOnActivate: null, bibleAbbreviation: bible, expectedRevision: known.revision }));
        notify({ kind: "info", text: `El pasaje ahora usa la Biblia ${bible}.` });
      });
    },

    setQuery(query) {
      set({ query });
      if (searchTimer !== null) clock.clearTimeout(searchTimer);
      searchTimer = clock.setTimeout(() => { searchTimer = null; void runSearch(query); }, deps.searchDebounceMs ?? 250);
    },
    moveResult(delta) {
      const n = get().results.length;
      if (n === 0) return;
      set((s) => ({ resultIndex: Math.max(0, Math.min(n - 1, s.resultIndex + delta)) }));
    },
    addSong(songId, where) {
      const target = get().replaceTarget;
      if (target !== null) {
        const at = displayedIds().indexOf(target);
        set({ replaceTarget: null });
        void addItem(songDto(songId), at < 0 ? null : at, target);
        return;
      }
      void addItem(songDto(songId), positionFor(where));
    },
    addResult(where) {
      const r = get().results[get().resultIndex];
      if (r) editor.addSong(r.id, where);
    },
    startReplace(itemId) {
      set({ replaceTarget: itemId });
    },

    async addPassage(item, label) {
      let outcome: AddResult = { ok: false, message: "No se pudo añadir el pasaje." };
      try {
        await list.init();
        if (list.store.getState().openId === null) return { ok: false, message: "No hay ninguna lista abierta: crea o abre una en la pestaña «Lista»." };
        const name = list.store.getState().list?.name ?? "la lista";
        await enqueue(async () => {
          const id = openId();
          if (id === null) throw new EditCommandError("playlist.notFound", "No hay una lista abierta.");
          const r = await send("playlist.addItem", { playlistId: id, item, position: null });
          bump(r);
          if (r.itemId !== null) { pendingSelect = r.itemId; set({ selectedId: r.itemId }); }
          outcome = { ok: true, message: `«${label}» se añadió al final de «${name}».` };
        });
        if (!outcome.ok) outcome = { ok: false, message: get().message?.text ?? outcome.message };
      } catch (error) {
        outcome = { ok: false, message: errorText(error) };
      }
      return outcome;
    },

    pendingReason,
    discard,
    dismissMessage() {
      set({ message: null });
    },
    warn(text) {
      set({ message: { kind: "error", text } });
    },
    displayedItems: displayed,
    dispose() {
      life.disposed = true;
      unsubscribe();
      offs.forEach((off) => { off(); });
      waiters.forEach((w) => { clock.clearTimeout(w.timer); });
      waiters.clear();
      if (searchTimer !== null) clock.clearTimeout(searchTimer);
      list.setLeaveGuard(null, null);
    },
  };
  return editor;
}
