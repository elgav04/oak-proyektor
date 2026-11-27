import { ipc } from "@/ipc/instance";
import { createPlaybackController, type PlaybackController } from "./controller";

let controller: PlaybackController | null = null;

/** Controlador único del reproductor y los ajustes de audio. Vive mientras vive la página: cambiar de pestaña no pierde nada. Lo que suena y se ve se deriva SIEMPRE del estado que confirma el backend. */
export function getPlaybackController(): PlaybackController {
  controller ??= createPlaybackController({ api: ipc });
  return controller;
}
