import { createStore, type StoreApi } from "zustand/vanilla";
import type { IpcClient } from "@/ipc/client";
import type { MediaFolderDto, MediaImportFinishedPayload, MediaItemDto, MediaItemKind } from "@/ipc/protocol";
import { createMediaVerifier, type MediaVerifier, type ProbeRunner } from "./verifier";

export type MediaApi = Pick<IpcClient, "send" | "request" | "subscribe">;

/** Qué carpeta se está viendo: todas, los que no están en ninguna, o una por id. */
export type FolderFilter = "all" | "unfiled" | number;

export interface ImportState {
  running: boolean;
  /** Id del comando `media.import` en curso (correlaciona el avance y el resultado). */
  importId: string | null;
  phase: string;
  doneBytes: number;
  totalBytes: number;
}

export interface MediaNotice {
  kind: "info" | "warn" | "error";
  text: string;
  /** Un duplicado: el elemento que ya existe (para ofrecer «Mostrarlo»). */
  duplicateOf?: { id: number; name: string };
}

export interface MediaState {
  folders: MediaFolderDto[];
  items: MediaItemDto[];
  truncated: boolean;
  totalItems: number;
  totalBytes: number;
  freeBytes: number;
  loaded: boolean;
  folder: FolderFilter;
  kind: MediaItemKind | null;
  query: string;
  import: ImportState;
  notice: MediaNotice | null;
  /** Id del elemento que el reproductor oculto está comprobando ahora (null = nada). */
  verifying: number | null;
  listError: string | null;
}

export interface MediaController {
  store: StoreApi<MediaState>;
  /** Primera carga (idempotente) y reconexión: lee la lista. */
  init(): void;
  refresh(): void;
  setFolder(folder: FolderFilter): void;
  setKind(kind: MediaItemKind | null): void;
  setQuery(query: string): void;
  /** El operador pegó la ruta COMPLETA de un archivo: se copia a la biblioteca en segundo plano. */
  importFile(path: string, name: string | null, folderId: number | null): boolean;
  cancelImport(): void;
  rename(item: MediaItemDto, name: string): void;
  move(item: MediaItemDto, folderId: number | null): void;
  remove(item: MediaItemDto): void;
  createFolder(name: string): void;
  renameFolder(folder: MediaFolderDto, name: string): void;
  deleteFolder(folder: MediaFolderDto): void;
  /** Usa un video o una imagen de la biblioteca como fondo (el backend resuelve la copia local y la valida). */
  useAsBackground(item: MediaItemDto, muted: boolean): void;
  verifyLibrary(): void;
  dismissNotice(): void;
  /** Muestra el elemento que ya existía (duplicado): quita los filtros y busca su nombre. */
  showExisting(name: string): void;
  dispose(): void;
}

export interface MediaControllerOptions {
  api: MediaApi;
  /** Cómo se comprueba y se saca la miniatura de un elemento (en la aplicación, el reproductor oculto del navegador; en los tests, uno simulado). Sin él no se verifica. */
  run?: ProbeRunner | null;
  /** Espera antes de releer la lista tras varios `media.changed` seguidos (los junta). */
  refreshDelayMs?: number;
  verifierGapMs?: number;
}

const IDLE_IMPORT: ImportState = { running: false, importId: null, phase: "", doneBytes: 0, totalBytes: 0 };

export function createMediaController(options: MediaControllerOptions): MediaController {
  const { api, run = null, refreshDelayMs = 120, verifierGapMs } = options;
  const store = createStore<MediaState>(() => ({
    folders: [], items: [], truncated: false, totalItems: 0, totalBytes: 0, freeBytes: -1, loaded: false, folder: "all", kind: null, query: "",
    import: IDLE_IMPORT, notice: null, verifying: null, listError: null,
  }));
  const patch = (changes: Partial<MediaState>): void => { store.setState(changes); };
  let disposed = false;
  let started = false;
  let token = 0;
  let timer: ReturnType<typeof setTimeout> | null = null;
  let verifier: MediaVerifier | null = null;

  const ensureVerifier = (): MediaVerifier | null => {
    if (!run) return null;
    verifier ??= createMediaVerifier({ api, run, gapMs: verifierGapMs, onChange: () => { patch({ verifying: verifier?.current() ?? null }); } });
    return verifier;
  };

  const doRefresh = async (): Promise<void> => {
    const mine = ++token;
    const s = store.getState();
    try {
      const reply = await api.request("media.list", {
        folderId: typeof s.folder === "number" ? s.folder : null,
        onlyUnfiled: s.folder === "unfiled",
        kind: s.kind,
        query: s.query.trim() === "" ? null : s.query.trim(),
        limit: null,
      });
      if (disposed || mine !== token) return;   // gana siempre la última lectura
      patch({ folders: reply.folders, items: reply.items, truncated: reply.truncated, totalItems: reply.totalItems, totalBytes: reply.totalBytes, freeBytes: reply.freeBytes, loaded: true, listError: null });
      ensureVerifier()?.enqueue(reply.items);
    } catch (error) {
      if (!disposed && mine === token) patch({ listError: error instanceof Error ? error.message : "No se pudo leer la biblioteca." });
    }
  };

  const scheduleRefresh = (): void => {
    if (timer) clearTimeout(timer);
    timer = setTimeout(() => { timer = null; void doRefresh(); }, refreshDelayMs);
  };

  const offs = [
    api.subscribe("media.changed", () => { if (!disposed) scheduleRefresh(); }),
    api.subscribe("media.importProgress", (p) => {
      if (disposed || p.importId !== store.getState().import.importId) return;
      patch({ import: { ...store.getState().import, running: true, phase: p.phase, doneBytes: p.doneBytes, totalBytes: p.totalBytes } });
    }),
    api.subscribe("media.importFinished", (p) => {
      if (disposed || p.importId !== store.getState().import.importId) return;
      patch({ import: IDLE_IMPORT, notice: noticeOf(p) });
      scheduleRefresh();
    }),
  ];

  const controller: MediaController = {
    store,
    init() {
      if (!started) started = true;
      void doRefresh();
    },
    refresh() { void doRefresh(); },
    setFolder(folder) { patch({ folder }); void doRefresh(); },
    setKind(kind) { patch({ kind }); void doRefresh(); },
    setQuery(query) { patch({ query }); scheduleRefresh(); },
    importFile(path, name, folderId) {
      if (store.getState().import.running) return false;
      const trimmed = path.trim();
      if (trimmed === "") { patch({ notice: { kind: "error", text: "Pega la ruta completa del archivo (por ejemplo D:\\Videos\\anuncio.mp4)." } }); return false; }
      const importId = api.send("media.import", { path: trimmed, name: name && name.trim() !== "" ? name.trim() : null, folderId });
      patch({ import: { running: true, importId, phase: "empezando", doneBytes: 0, totalBytes: 0 }, notice: null });
      return true;
    },
    cancelImport() { api.send("media.cancelImport", {}); },
    rename(item, name) { if (name.trim() !== "" && name.trim() !== item.name) api.send("media.rename", { id: item.id, name }); },
    move(item, folderId) { if (folderId !== item.folderId) api.send("media.move", { id: item.id, folderId }); },
    remove(item) { api.send("media.delete", { id: item.id }); },
    createFolder(name) { if (name.trim() !== "") api.send("media.createFolder", { name }); },
    renameFolder(folder, name) { if (name.trim() !== "" && name.trim() !== folder.name) api.send("media.renameFolder", { id: folder.id, name }); },
    deleteFolder(folder) {
      if (store.getState().folder === folder.id) patch({ folder: "all" });
      api.send("media.deleteFolder", { id: folder.id });
    },
    useAsBackground(item, muted) { api.send("media.setBackground", { mediaId: item.id, muted, transitionMs: 600 }); },
    verifyLibrary() { api.send("media.verifyLibrary", {}); },
    dismissNotice() { patch({ notice: null }); },
    showExisting(name) {
      patch({ folder: "all", kind: null, query: name, notice: null });
      void doRefresh();
    },
    dispose() {
      disposed = true;
      if (timer) clearTimeout(timer);
      verifier?.stop();
      for (const off of offs) off();
    },
  };
  return controller;
}

/** El resultado de una importación como aviso para el operador (el nombre de un duplicado solo llega aquí, al panel). */
function noticeOf(p: MediaImportFinishedPayload): MediaNotice {
  if (p.ok) return p.warningMessage ? { kind: "warn", text: `Importado, pero: ${p.warningMessage}` } : { kind: "info", text: "Importado a la biblioteca. Se está comprobando que se reproduzca en este equipo." };
  if (p.errorCode === "media.duplicate" && p.duplicateOfId !== null) return { kind: "info", text: `Ese archivo ya está en la biblioteca como «${p.duplicateOfName ?? ""}».`, duplicateOf: { id: p.duplicateOfId, name: p.duplicateOfName ?? "" } };
  if (p.errorCode === "media.cancelled") return { kind: "info", text: p.errorMessage ?? "Importación cancelada." };
  return { kind: "error", text: p.errorMessage ?? "No se pudo importar el archivo." };
}
