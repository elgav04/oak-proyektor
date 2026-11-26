import { ipc } from "@/ipc/instance";
import { useControlStore } from "../store";
import { createListController, type ListController, type ListStorage } from "./controller";
import { createListEditor, type ListEditor } from "./editor";

let controller: ListController | null = null;

const KEY = "oak-proyektor.lists.open";

/** La lista abierta por última vez, en el almacenamiento local de la página (de mejor esfuerzo: si no hay o falla, simplemente no se recuerda). */
function createLocalStorage(): ListStorage | null {
  try {
    const storage = typeof window !== "undefined" ? window.localStorage : null;
    if (!storage) return null;
    return {
      get: () => { try { return storage.getItem(KEY); } catch { return null; } },
      set: (value) => { try { storage.setItem(KEY, value); } catch { /* sin espacio o bloqueado: es una comodidad */ } },
    };
  } catch {
    return null;
  }
}

/**
 * Controlador único de la pestaña «Lista». Vive mientras vive la página: cambiar de pestaña no pierde el cursor ni lo cargado. El elemento activo se deriva SIEMPRE del
 * estado de la salida que confirma el backend (`useControlStore.output`), así que tras recargar el panel (Ctrl+Alt+Shift+R) se recupera solo.
 */
export function getListController(): ListController {
  controller ??= createListController({ api: ipc, getOutput: () => useControlStore.getState().output, storage: createLocalStorage() });
  return controller;
}

let editor: ListEditor | null = null;

/** El editor de la lista (modo EDITAR). También lo usa la pestaña de Biblia para «Añadir a la lista»: por eso vive con la página, no con la pestaña. */
export function getListEditor(): ListEditor {
  editor ??= createListEditor({ api: ipc, list: getListController() });
  return editor;
}