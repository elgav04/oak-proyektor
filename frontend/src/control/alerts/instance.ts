import { ipc } from "@/ipc/instance";
import type { BindingsStorage } from "./alertShortcuts";
import { createAlertController, type AlertController } from "./controller";
import { createAlertMeasurer } from "./model";

let controller: AlertController | null = null;

const KEY = "oak-proyektor.alerts.shortcuts";

/** Los atajos configurados, en el almacenamiento local de la página (de mejor esfuerzo: si no hay o falla, valen los de fábrica). */
function createLocalStorage(): BindingsStorage | null {
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
 * Controlador único de la barra de alertas. Vive mientras vive la página: cambiar de pestaña no pierde lo que se está escribiendo. Lo que hay EN PANTALLA se deriva
 * siempre del estado que confirma el backend (`useControlStore.output`), así que tras recargar el panel se recupera solo.
 */
export function getAlertController(): AlertController {
  controller ??= createAlertController({ api: ipc, measurer: typeof document === "undefined" ? null : createAlertMeasurer(), shortcutStorage: createLocalStorage() });
  return controller;
}
