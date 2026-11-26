import { ipc } from "@/ipc/instance";
import { createMediaController, type MediaController } from "./controller";
import { createBrowserProbeRunner } from "./verifier";

let controller: MediaController | null = null;

/**
 * Controlador único de la biblioteca de medios. Vive mientras vive la página: cambiar de pestaña no pierde los filtros ni una importación en curso (su avance sigue llegando).
 * La comprobación «¿se reproduce?» y las miniaturas las hace el reproductor oculto del navegador, de una en una, solo después de que la pestaña haya cargado la lista.
 */
export function getMediaController(): MediaController {
  controller ??= createMediaController({ api: ipc, run: typeof document === "undefined" ? null : createBrowserProbeRunner() });
  return controller;
}
