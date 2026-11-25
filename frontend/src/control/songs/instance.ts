import { ipc } from "@/ipc/instance";
import { createSongController, type DraftStorage, type SongController } from "./controller";
// Registra window.__oakSongLayoutCheck (solo lo usa tools/song-layout-test.ps1 por la depuración remota de WebView2).
import "./layoutCheck";

let controller: SongController | null = null;

const DRAFT_KEY = "oak-proyektor.songs.draft";

/** Borrador en el almacenamiento local de la página (de mejor esfuerzo: si no hay o falla, simplemente no se guarda). */
export function createLocalDraftStorage(storage: Pick<Storage, "getItem" | "setItem" | "removeItem"> | null = safeStorage()): DraftStorage | null {
  if (!storage) return null;
  return {
    get: () => { try { return storage.getItem(DRAFT_KEY); } catch { return null; } },
    set: (value) => { try { storage.setItem(DRAFT_KEY, value); } catch { /* sin espacio o bloqueado: el borrador es una comodidad */ } },
    clear: () => { try { storage.removeItem(DRAFT_KEY); } catch { /* idem */ } },
  };
}

function safeStorage(): Storage | null {
  try {
    return typeof window !== "undefined" ? window.localStorage : null;
  } catch {
    return null;
  }
}

/**
 * Controlador único de la pestaña de Canciones. Vive mientras vive la página: cambiar de pestaña NO pierde el texto, el orden ni los cambios sin guardar
 * (solo se desmonta la vista, y con ella los atajos).
 */
export function getSongController(): SongController {
  if (!controller) {
    controller = createSongController({ api: ipc, storage: createLocalDraftStorage() });
    const created = controller;
    // Solo para tools/song-layout-test.ps1 (depuración remota): un resumen SIN letra ni títulos de la proyección seleccionada.
    if (typeof window !== "undefined") {
      Object.defineProperty(window, "__oakSongProjection", {
        value: () => {
          const p = created.store.getState().projection;
          return JSON.stringify(p ? { mode: created.store.getState().mode, songId: p.songId, slides: p.slides?.length ?? null, busy: p.busy, error: p.error, cursor: p.cursor, requested: p.requested, atEnd: p.atEnd } : null);
        },
      });
    }
  }
  return controller;
}
