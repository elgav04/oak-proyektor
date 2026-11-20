import type { BackgroundFailureReason, BackgroundKind } from "@/ipc/protocol";

/** Lo que el motor necesita saber del fondo. Son SOLO primitivos: el motor compara por valor, no por identidad del objeto del snapshot. */
export interface BackgroundSpec {
  kind: BackgroundKind;
  /** Color sólido: el fondo de tipo color, el que se ve mientras el medio carga y el color de reserva si el medio falla. */
  color: string;
  /** URL `file:///` ya validada por el backend; la salida no valida nada. */
  mediaUrl: string | null;
  muted: boolean;
  transitionMs: number;
}

export interface BackgroundFailure {
  mediaUrl: string;
  reason: BackgroundFailureReason;
  /** Técnico (código de error del navegador); solo para el log del backend. */
  detail: string;
}

/**
 * Motor de la capa de fondo. Esta interfaz es el punto de sustitución: hoy hay UN motor (`createSingleVideoEngine`, un `<video>` con el atributo
 * `loop`); más adelante puede reemplazarse por otro de dos videos con crossfade en el punto de reinicio del bucle sin tocar `BackgroundLayer` ni
 * el resto de la salida. Un motor nuevo debe respetar los mismos contratos: (1) `apply` con el mismo medio NO remonta ni reinicia el elemento;
 * (2) cambiar de fondo libera el medio anterior de verdad; (3) un fallo cae al color sólido y se informa por `onFailure`.
 */
export interface BackgroundEngine {
  apply(spec: BackgroundSpec): void;
  dispose(): void;
}

export interface EngineOptions {
  onFailure?: (failure: BackgroundFailure) => void;
  /** Cuánto esperar tras un evento `stalled` sin que el video avance antes de darlo por fallido. */
  stallMs?: number;
  /** Cuánto esperar a que el medio cargue su primer fotograma. */
  loadTimeoutMs?: number;
  /** Máximo de medios en fundido de salida a la vez (cada video retiene un decodificador): el más viejo se libera ya. */
  maxLeaving?: number;
}

type LayerState = "loading" | "visible" | "leaving";

interface MediaLayer {
  el: HTMLImageElement | HTMLVideoElement;
  kind: "image" | "video";
  url: string;
  state: LayerState;
  released: boolean;
  failed: boolean;
  timers: Set<ReturnType<typeof setTimeout>>;
  stallTimer: ReturnType<typeof setTimeout> | null;
  detach: () => void;
}

// Códigos de MediaError.
const MEDIA_ERR_NETWORK = 2;
const MEDIA_ERR_DECODE = 3;
const MEDIA_ERR_SRC_NOT_SUPPORTED = 4;

const FILL = "position:absolute;left:0;top:0;width:100%;height:100%;object-fit:cover;pointer-events:none;";

/** Un `<video loop>` persistente por fondo: nunca se remonta al cambiar el contenido y se libera por completo al cambiar de fondo. */
export function createSingleVideoEngine(container: HTMLElement, options: EngineOptions = {}): BackgroundEngine {
  const doc = container.ownerDocument;
  const stallMs = options.stallMs ?? 5000;
  const loadTimeoutMs = options.loadTimeoutMs ?? 10000;
  const maxLeaving = options.maxLeaving ?? 2;

  const base = doc.createElement("div");
  base.style.cssText = "position:absolute;left:0;top:0;width:100%;height:100%;";
  container.appendChild(base);

  const layers: MediaLayer[] = [];
  let spec: BackgroundSpec | null = null;
  /** Medio pedido (kind|url), o null si el fondo actual no es un medio. Se conserva tras un fallo: el mismo pedido no se reintenta en bucle. */
  let wantedKey: string | null = null;
  let disposed = false;

  const setBase = (color: string, ms: number): void => {
    base.style.transition = `background-color ${String(Math.max(0, ms))}ms linear`;
    base.style.backgroundColor = color;
  };

  const later = (layer: MediaLayer, ms: number, fn: () => void): void => {
    const timer = setTimeout(() => {
      layer.timers.delete(timer);
      fn();
    }, ms);
    layer.timers.add(timer);
  };

  const disarmStall = (layer: MediaLayer): void => {
    if (layer.stallTimer !== null) {
      clearTimeout(layer.stallTimer);
      layer.stallTimer = null;
    }
  };

  /** Libera el medio DE VERDAD: detiene, quita `src`, `load()` (suelta el decodificador y el archivo) y quita el nodo. */
  const release = (layer: MediaLayer): void => {
    if (layer.released) return;
    layer.released = true;
    layer.detach();
    for (const t of layer.timers) clearTimeout(t);
    layer.timers.clear();
    disarmStall(layer);
    const index = layers.indexOf(layer);
    if (index >= 0) layers.splice(index, 1);
    try {
      if (layer.kind === "video") (layer.el as HTMLVideoElement).pause();
    } catch {
      /* un elemento ya roto no debe impedir liberarlo */
    }
    layer.el.removeAttribute("src");
    try {
      if (layer.kind === "video") (layer.el as HTMLVideoElement).load();
    } catch {
      /* idem */
    }
    layer.el.remove();
  };

  const leave = (layer: MediaLayer, ms: number): void => {
    if (layer.released || layer.state === "leaving") return;
    layer.state = "leaving";
    disarmStall(layer);
    if (ms <= 0) {
      release(layer);
      return;
    }
    layer.el.style.transition = `opacity ${String(ms)}ms linear`;
    layer.el.style.opacity = "0";
    later(layer, ms + 60, () => {
      release(layer);
    });
    const leaving = layers.filter((l) => l.state === "leaving");
    while (leaving.length > maxLeaving) {
      const oldest = leaving.shift();
      if (oldest) release(oldest);
    }
  };

  const fail = (layer: MediaLayer, reason: BackgroundFailureReason, detail: string): void => {
    if (layer.released || layer.failed || disposed) return;
    layer.failed = true;
    const ms = spec?.transitionMs ?? 0;
    // Cae al color sólido (la base ya lo muestra): todo medio se retira con fundido, el que falló incluido; si nunca llegó a verse, se libera ya.
    for (const l of [...layers]) {
      if (l === layer && l.state === "loading") release(l);
      else leave(l, ms);
    }
    options.onFailure?.({ mediaUrl: layer.url, reason, detail });
  };

  const ensurePlaying = (layer: MediaLayer): void => {
    if (layer.kind !== "video" || layer.released || layer.state === "leaving") return;
    const video = layer.el as HTMLVideoElement;
    if (!video.paused || video.ended) return;
    // En navegadores reales play() devuelve una promesa; en algunos entornos (y en los tests de DOM) puede no hacerlo: se tolera.
    const attempt = (): Promise<void> | undefined => video.play();
    const started = attempt();
    started?.catch(() => {
      // Sin permiso para reproducir con sonido: se reintenta silenciado antes de rendirse.
      if (!video.muted && !layer.released) {
        video.muted = true;
        attempt()?.catch(() => undefined);
      }
    });
  };

  const show = (layer: MediaLayer): void => {
    if (layer.released || layer.state !== "loading") return;
    for (const timer of layer.timers) clearTimeout(timer);   // el tiempo de espera de carga
    layer.timers.clear();
    layer.state = "visible";
    const ms = spec?.transitionMs ?? 0;
    layer.el.style.transition = `opacity ${String(Math.max(0, ms))}ms linear`;
    layer.el.style.opacity = "1";
    // El medio anterior sigue debajo, opaco, hasta que este termine de aparecer: no hay hueco ni destello del color de base.
    for (const other of [...layers]) if (other !== layer && other.state === "visible") leave(other, ms);
  };

  const create = (kind: "image" | "video", url: string, s: BackgroundSpec): MediaLayer => {
    const el = kind === "video" ? doc.createElement("video") : doc.createElement("img");
    el.style.cssText = `${FILL}opacity:0;`;
    const layer: MediaLayer = { el, kind, url, state: "loading", released: false, failed: false, timers: new Set(), stallTimer: null, detach: () => undefined };
    const listeners: [string, () => void][] = [];
    const on = (type: string, fn: () => void): void => {
      el.addEventListener(type, fn);
      listeners.push([type, fn]);
    };
    layer.detach = () => {
      for (const [type, fn] of listeners) el.removeEventListener(type, fn);
      listeners.length = 0;
    };

    if (kind === "video") {
      const video = el as HTMLVideoElement;
      video.muted = s.muted;
      video.loop = true;        // bucle por el atributo nativo (ver BackgroundEngine: aquí se puede cambiar por dos videos más adelante)
      video.autoplay = true;
      video.playsInline = true;
      video.preload = "auto";
      video.controls = false;
      video.disablePictureInPicture = true;
      on("loadeddata", () => { show(layer); });
      on("canplay", () => { show(layer); disarmStall(layer); });
      on("playing", () => { disarmStall(layer); });
      on("timeupdate", () => { disarmStall(layer); });
      on("error", () => {
        const code = video.error?.code;
        if (code === MEDIA_ERR_SRC_NOT_SUPPORTED) fail(layer, "unsupportedFormat", "MEDIA_ERR_SRC_NOT_SUPPORTED");
        else if (code === MEDIA_ERR_DECODE) fail(layer, "decodeError", "MEDIA_ERR_DECODE");
        else if (code === MEDIA_ERR_NETWORK) fail(layer, "readError", "MEDIA_ERR_NETWORK");
        else fail(layer, "readError", `MediaError ${String(code)}`);
      });
      on("stalled", () => {
        // `stalled` también salta un instante con discos lentos: solo cuenta si el video no avanza durante `stallMs`. Con la ventana oculta no se arma.
        if (doc.hidden || layer.released || layer.state === "leaving" || layer.stallTimer !== null) return;
        layer.stallTimer = setTimeout(() => {
          layer.stallTimer = null;
          fail(layer, "stalled", "stalled");
        }, stallMs);
      });
      // Chromium puede pausar un video silenciado cuando no es visible (ventana minimizada por la pausa de la salida): al volver sigue donde estaba.
      on("pause", () => {
        if (!doc.hidden) ensurePlaying(layer);
      });
    } else {
      on("load", () => { show(layer); });
      on("error", () => { fail(layer, "imageError", "img error"); });
    }

    later(layer, loadTimeoutMs, () => {
      if (layer.state === "loading") fail(layer, kind === "video" ? "stalled" : "imageError", "load-timeout");
    });
    layers.push(layer);
    container.appendChild(el);
    el.setAttribute("src", url);
    if (kind === "video") ensurePlaying(layer);
    return layer;
  };

  const onVisibility = (): void => {
    for (const layer of layers) {
      if (doc.hidden) disarmStall(layer);
      else ensurePlaying(layer);
    }
  };
  doc.addEventListener("visibilitychange", onVisibility);

  return {
    apply(next: BackgroundSpec): void {
      if (disposed) return;
      spec = next;
      setBase(next.color, next.transitionMs);

      const isMedia = (next.kind === "image" || next.kind === "video") && next.mediaUrl !== null;
      const key = isMedia ? `${next.kind}|${String(next.mediaUrl)}` : null;
      if (key === wantedKey) {
        // El mismo medio (p. ej. cambió el texto): NO se toca el elemento. Solo puede cambiar el silencio.
        for (const layer of layers) if (layer.kind === "video" && layer.state !== "leaving") (layer.el as HTMLVideoElement).muted = next.muted;
        return;
      }
      wantedKey = key;
      for (const layer of [...layers]) if (layer.state === "loading") release(layer);   // nunca llegó a verse
      if (!isMedia || next.mediaUrl === null) {
        for (const layer of [...layers]) leave(layer, next.transitionMs);
        return;
      }
      create(next.kind as "image" | "video", next.mediaUrl, next);
    },
    dispose(): void {
      if (disposed) return;
      disposed = true;
      doc.removeEventListener("visibilitychange", onVisibility);
      for (const layer of [...layers]) release(layer);
      base.remove();
    },
  };
}
