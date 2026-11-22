import { ipc } from "@/ipc/instance";
import { createBibleController, type BibleController } from "./controller";
import { createImportController, type ImportController } from "./importController";

let controller: BibleController | null = null;
let importer: ImportController | null = null;

/**
 * Controlador único de la página del panel. Vive mientras vive la página: cambiar de pestaña no pierde la búsqueda, la selección ni la página; solo se
 * desmonta la vista (y con ella los atajos).
 */
export function getBibleController(): BibleController {
  controller ??= createBibleController({ api: ipc });
  return controller;
}

/** Importación de Biblias: también vive con la página (cambiar de pantalla no pierde el avance). Al terminar bien, refresca la lista de Biblias. */
export function getImportController(): ImportController {
  importer ??= createImportController(ipc, (result) => {
    if (result.ok) void getBibleController().refreshBibles();
  });
  return importer;
}
