import { ipc } from "@/ipc/instance";
import { createBibleController, type BibleController } from "./controller";

let controller: BibleController | null = null;

/**
 * Controlador único de la página del panel. Vive mientras vive la página: cambiar de pestaña no pierde la búsqueda, la selección ni la página; solo se
 * desmonta la vista (y con ella los atajos).
 */
export function getBibleController(): BibleController {
  controller ??= createBibleController({ api: ipc });
  return controller;
}
