import { createStore, type StoreApi } from "zustand/vanilla";
import type { IpcClient } from "@/ipc/client";
import type { AudioSettingsPayload, AudioStatePayload, AudioTestTarget, MediaItemDto, PlaybackTarget } from "@/ipc/protocol";
import { isNewer } from "@/ipc/sequence";
import { createLatestSender, type LatestSender } from "../bible/latest";
import { clampPct } from "./model";

/** Lo que el controlador necesita del cliente IPC (así se prueba con un backend falso sobre el cliente real). */
export type PlaybackApi = Pick<IpcClient, "send" | "request" | "subscribe">;

export interface PlaybackState {
  /** El estado del audio que confirma el backend (null hasta que se lee). */
  audio: AudioStatePayload | null;
  /** Avance cosmético del video en primer plano que informó la SALIDA (la verdad es la salida). */
  fgProgress: { id: string; positionMs: number; durationMs: number; playing: boolean } | null;
  /** Avance de la pista (viene del motor de C#). */
  trackProgress: { positionMs: number; durationMs: number; playing: boolean } | null;
  /** Nombre de la pista actual (se pide a la biblioteca; solo para mostrarlo). */
  trackName: string | null;
  /** El último fallo de reproducción del video en primer plano (texto del backend), cerrable. */
  failure: string | null;
  /** `at` del último cambio del predeterminado de Windows que el operador ya cerró. */
  dismissedDefaultChange: string | null;
  /** Se está reproduciendo un tono de prueba. */
  testing: AudioTestTarget | null;
  /** Resultado de la última prueba de sonido o de un ajuste rechazado (texto en español). */
  notice: string | null;
}

export interface PlaybackController {
  store: StoreApi<PlaybackState>;
  /** Lee el estado del audio (al abrir la página y al reconectar). */
  init(): Promise<void>;
  /** Reproduce un elemento de la biblioteca: un VIDEO pasa a primer plano; un AUDIO se carga listo en pausa (`startNow` lo arranca). Las imágenes no se reproducen. */
  playItem(item: Pick<MediaItemDto, "id" | "kind">, options?: { startNow?: boolean; loop?: boolean }): void;
  pause(target: PlaybackTarget): void;
  resume(target: PlaybackTarget): void;
  stop(target: PlaybackTarget): void;
  /** Buscar (a lo sumo una orden cada 100 ms mientras se arrastra; la ÚLTIMA siempre llega). */
  seek(target: PlaybackTarget, positionMs: number): void;
  seekNow(target: PlaybackTarget, positionMs: number): void;
  setClipVolume(target: PlaybackTarget, volumePct: number): void;
  setMuted(muted: boolean): void;
  /** «Silencio»: calla las pistas y pausa el video. Solo calla. Es el mismo mensaje que el atajo de respaldo de la salida. */
  silence(): void;
  setMasterVolume(volumePct: number): void;
  /** Elegir el «dispositivo de audio de la iglesia» por su id estable; nulo = usar el predeterminado de Windows. */
  setDevice(deviceId: string | null): void;
  saveSettings(settings: AudioSettingsPayload): void;
  testSound(target: AudioTestTarget): void;
  dismissDefaultChange(): void;
  dismissFailure(): void;
  dismissNotice(): void;
  dispose(): void;
}

export interface PlaybackControllerOptions {
  api: PlaybackApi;
  /** Cuánto esperar entre órdenes de «buscar» y de volumen mientras se arrastra un deslizador. */
  sliderIntervalMs?: number;
}

export function createPlaybackController(options: PlaybackControllerOptions): PlaybackController {
  const { api, sliderIntervalMs = 100 } = options;
  const store = createStore<PlaybackState>(() => ({
    audio: null, fgProgress: null, trackProgress: null, trackName: null, failure: null, dismissedDefaultChange: null, testing: null, notice: null,
  }));
  const patch = (changes: Partial<PlaybackState>): void => { store.setState(changes); };
  let disposed = false;
  let nameToken = 0;
  let lastAudioSeq: number | null = null;
  let namedTrack: number | null = null;

  const applyAudio = (next: AudioStatePayload): void => {
    if (disposed || !isNewer(lastAudioSeq, next.seq)) return;
    lastAudioSeq = next.seq;
    const mediaId = next.track?.mediaId ?? null;
    patch({ audio: next, ...(next.track === null ? { trackProgress: null, trackName: null } : {}) });
    if (mediaId !== null && mediaId !== namedTrack) {
      namedTrack = mediaId;
      const token = ++nameToken;
      void api.request("media.get", { id: mediaId }).then((item) => { if (!disposed && token === nameToken) patch({ trackName: item.name }); }).catch(() => undefined);
    }
    if (mediaId === null) namedTrack = null;
  };

  const offAudio = api.subscribe("audio.stateChanged", applyAudio);
  const offProgress = api.subscribe("media.progress", (p) => {
    if (disposed) return;
    if (p.target === "foreground" && p.id !== null) patch({ fgProgress: { id: p.id, positionMs: p.positionMs, durationMs: p.durationMs, playing: p.playing } });
    else if (p.target === "track") patch({ trackProgress: { positionMs: p.positionMs, durationMs: p.durationMs, playing: p.playing } });
  });
  const offFailed = api.subscribe("media.playbackFailed", (f) => { if (!disposed) patch({ failure: f.message }); });

  const seekSender: Record<PlaybackTarget, LatestSender<number>> = {
    foreground: createLatestSender((ms) => { api.send("media.seek", { target: "foreground", positionMs: ms }); }, sliderIntervalMs),
    track: createLatestSender((ms) => { api.send("media.seek", { target: "track", positionMs: ms }); }, sliderIntervalMs),
  };
  const volumeSender: Record<PlaybackTarget, LatestSender<number>> = {
    foreground: createLatestSender((pct) => { api.send("media.setVolume", { target: "foreground", volumePct: pct, muted: null }); }, sliderIntervalMs),
    track: createLatestSender((pct) => { api.send("media.setVolume", { target: "track", volumePct: pct, muted: null }); }, sliderIntervalMs),
  };
  const masterSender = createLatestSender((pct: number) => { api.send("audio.setMasterVolume", { volumePct: pct }); }, sliderIntervalMs);

  const controller: PlaybackController = {
    store,
    async init() {
      try { applyAudio(await api.request("audio.getState", {})); }
      catch (error) { if (!disposed) patch({ notice: error instanceof Error ? error.message : "No se pudo leer el estado del audio." }); }
    },
    playItem(item, opts = {}) {
      if (item.kind === "image") return;
      api.send("media.play", {
        mediaId: item.id, startPlaying: item.kind === "audio" ? opts.startNow === true : null, volumePct: null, muted: false, loop: opts.loop === true,
        fadeInMs: null, fadeOutMs: null, backdrop: "black", onEnd: "back",
      });
    },
    pause(target) { api.send("media.pause", { target }); },
    resume(target) { api.send("media.resume", { target }); },
    stop(target) { api.send("media.stop", { target }); },
    seek(target, positionMs) { seekSender[target].push(Math.max(0, Math.round(positionMs))); },
    seekNow(target, positionMs) { seekSender[target].now(Math.max(0, Math.round(positionMs))); },
    setClipVolume(target, volumePct) { volumeSender[target].push(clampPct(volumePct)); },
    setMuted(muted) { api.send("media.setVolume", { target: "foreground", volumePct: null, muted }); },
    silence() { api.send("media.stopAll", {}); },
    setMasterVolume(volumePct) { masterSender.push(clampPct(volumePct)); },
    setDevice(deviceId) { api.send("audio.setDevice", { deviceId }); },
    saveSettings(settings) { api.send("audio.setSettings", settings); },
    testSound(target) {
      patch({ testing: target, notice: null });
      api.send("audio.test", { target });
      // El tono dura ≈ 0,7 s; el resultado de un fallo llega como `command.failed` (lo muestra el panel). El indicador se apaga solo.
      setTimeout(() => { if (!disposed) patch({ testing: null }); }, 1500);
    },
    dismissDefaultChange() {
      const at = store.getState().audio?.defaultChange?.at ?? null;
      if (at !== null) patch({ dismissedDefaultChange: at });
    },
    dismissFailure() { patch({ failure: null }); },
    dismissNotice() { patch({ notice: null }); },
    dispose() {
      disposed = true;
      offAudio(); offProgress(); offFailed();
      for (const t of ["foreground", "track"] as const) { seekSender[t].cancel(); volumeSender[t].cancel(); }
      masterSender.cancel();
    },
  };
  return controller;
}
