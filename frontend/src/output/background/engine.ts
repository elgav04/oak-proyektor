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
  /**
   * Contador de reintentos del MISMO medio (v9). Si sube y ese medio había fallado, se vuelve a intentar una vez; si el medio se reproduce bien, se ignora
   * (nunca reinicia un video sano). Opcional: sin él vale 0.
   */
  retry?: number;
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
  /** Cuánto esperar tras un evento `stalled`, o sin que `currentTime` avance, antes de darlo por atascado. */
  stallMs?: number;
  /** Cuánto esperar a que el medio cargue su primer fotograma (solo corre con la ventana visible). */
  loadTimeoutMs?: number;
  /** Cada cuánto se comprueba, con la ventana visible, que el video realmente avanza. */
  watchMs?: number;
  /** Máximo de medios en fundido de salida a la vez (cada video retiene un decodificador): el más viejo se libera ya. */
  maxLeaving?: number;
}

type LayerState = "loading" | "visible" | "leaving";
type Timer = ReturnType<typeof setTimeout>;

interface MediaLayer {
  el: HTMLImageElement | HTMLVideoElement;
  kind: "image" | "video";
  url: string;
  state: LayerState;
  released: boolean;
  failed: boolean;
  timers: Set<Timer>;
  stallTimer: Timer | null;
  /** Tiempo de espera de carga (se suspende con la ventana oculta y se rearma al mostrarse). */
  loadTimer: Timer | null;
  /** Comprobación periódica de que el video avanza (solo con la ventana visible). */
  watchTimer: ReturnType<typeof setInterval> | null;
  lastTime: number;
  lastMoveAt: number;
  /** 0 = primer intento; 1 = ya se recargó una vez tras un atasco o una carga fallida (el siguiente fallo es definitivo). */
  attempt: number;
  /** Recargando el mismo medio tras un fallo blando (el elemento sigue en pantalla, sin imagen, hasta que decodifique). */
  reloading: boolean;
  /** Fallo blando ocurrido con la ventana oculta: se recarga al volver a ser visible. */
  pendingReload: { reason: BackgroundFailureReason; detail: string } | null;
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
  const watchMs = options.watchMs ?? 2000;
  const maxLeaving = options.maxLeaving ?? 2;

  const base = doc.createElement("div");
  base.style.cssText = "position:absolute;left:0;top:0;width:100%;height:100%;";
  container.appendChild(base);

  const layers: MediaLayer[] = [];
  let spec: BackgroundSpec | null = null;
  /** Medio pedido (kind|url), o null si el fondo actual no es un medio. Se conserva tras un fallo: el mismo pedido no se reintenta en bucle. */
  let wantedKey: string | null = null;
  /** El medio pedido falló definitivamente (cayó al color de reserva): solo un `retry` mayor lo vuelve a intentar. */
  let wantedFailed = false;
  let wantedRetry = 0;
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

  const disarmLoad = (layer: MediaLayer): void => {
    if (layer.loadTimer !== null) {
      clearTimeout(layer.loadTimer);
      layer.loadTimer = null;
    }
  };

  const stopWatch = (layer: MediaLayer): void => {
    if (layer.watchTimer !== null) {
      clearInterval(layer.watchTimer);
      layer.watchTimer = null;
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
    disarmLoad(layer);
    stopWatch(layer);
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
    layer.pendingReload = null;
    disarmStall(layer);
    disarmLoad(layer);
    stopWatch(layer);
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

  /** Fallo DEFINITIVO: cae al color de reserva e informa. Un medio que ya estaba saliendo (fundido) solo se libera: no es el fondo vigente (H7). */
  const fail = (layer: MediaLayer, reason: BackgroundFailureReason, detail: string): void => {
    if (layer.released || layer.failed || disposed) return;
    if (layer.state === "leaving") {
      release(layer);
      return;
    }
    layer.failed = true;
    if (`${layer.kind}|${layer.url}` === wantedKey) wantedFailed = true;
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

  /** Arma el tiempo de espera de carga. NO corre con la ventana oculta (Chromium puede diferir la carga): se rearma al mostrarse. */
  const armLoad = (layer: MediaLayer): void => {
    disarmLoad(layer);
    if (layer.released || doc.hidden) return;
    layer.loadTimer = setTimeout(() => {
      layer.loadTimer = null;
      if (layer.state === "loading" || layer.reloading) softFail(layer, layer.kind === "video" ? "stalled" : "imageError", "load-timeout");
    }, loadTimeoutMs);
  };

  /** Vuelve a cargar el MISMO medio en el mismo elemento (un solo intento): el color de base se ve mientras tanto. */
  const reload = (layer: MediaLayer): void => {
    if (layer.released || layer.state === "leaving") return;
    layer.attempt = 1;
    layer.pendingReload = null;
    layer.reloading = true;
    disarmStall(layer);
    stopWatch(layer);
    layer.el.removeAttribute("src");
    try {
      if (layer.kind === "video") (layer.el as HTMLVideoElement).load();
    } catch {
      /* un elemento roto: el tiempo de espera de carga lo dará por fallido */
    }
    layer.el.setAttribute("src", layer.url);
    ensurePlaying(layer);
    armLoad(layer);
  };

  /**
   * Fallo BLANDO (atasco, tiempo de carga): antes de dejar el fondo en el color de reserva se recarga el medio UNA vez; con la ventana oculta la recarga espera a
   * que vuelva a ser visible (no se puede juzgar un video que Chromium tiene en pausa). El segundo fallo es definitivo.
   */
  const softFail = (layer: MediaLayer, reason: BackgroundFailureReason, detail: string): void => {
    if (layer.released || layer.failed || disposed || layer.state === "leaving") return;
    if (layer.attempt >= 1) {
      fail(layer, reason, detail);
      return;
    }
    if (doc.hidden) {
      layer.pendingReload = { reason, detail };
      return;
    }
    reload(layer);
  };

  /** Con la ventana visible, comprueba cada `watchMs` que el video realmente avanza: un fotograma congelado sin evento de error también se detecta. */
  const startWatch = (layer: MediaLayer): void => {
    if (layer.kind !== "video" || layer.released || layer.state === "leaving" || layer.watchTimer !== null || doc.hidden) return;
    const video = layer.el as HTMLVideoElement;
    layer.lastTime = video.currentTime;
    layer.lastMoveAt = Date.now();
    layer.watchTimer = setInterval(() => {
      if (layer.released || layer.state === "leaving" || doc.hidden || layer.reloading) return;
      ensurePlaying(layer);
      const now = Date.now();
      if (video.currentTime !== layer.lastTime) {
        layer.lastTime = video.currentTime;
        layer.lastMoveAt = now;
        layer.attempt = 0;   // volvió a avanzar: un atasco futuro (minutos u horas después) vuelve a tener su recarga
        return;
      }
      if (now - layer.lastMoveAt >= stallMs) softFail(layer, "stalled", "no-progress");
    }, watchMs);
  };

  const show = (layer: MediaLayer): void => {
    if (layer.released || layer.state !== "loading") return;
    disarmLoad(layer);
    layer.state = "visible";
    startWatch(layer);
    const ms = spec?.transitionMs ?? 0;
    layer.el.style.transition = `opacity ${String(Math.max(0, ms))}ms linear`;
    layer.el.style.opacity = "1";
    // El medio anterior sigue debajo, opaco, hasta que este termine de aparecer: no hay hueco ni destello del color de base.
    for (const other of [...layers]) if (other !== layer && other.state === "visible") leave(other, ms);
  };

  /** Primer fotograma decodificado: termina la carga (la primera o una recarga) y, si hace falta, lo muestra. */
  const ready = (layer: MediaLayer): void => {
    if (layer.released) return;
    if (layer.reloading) {
      layer.reloading = false;
      disarmLoad(layer);
      startWatch(layer);
    }
    show(layer);
  };

  const create = (kind: "image" | "video", url: string, s: BackgroundSpec): MediaLayer => {
    const el = kind === "video" ? doc.createElement("video") : doc.createElement("img");
    el.style.cssText = `${FILL}opacity:0;`;
    const layer: MediaLayer = {
      el, kind, url, state: "loading", released: false, failed: false, timers: new Set(), stallTimer: null, loadTimer: null, watchTimer: null,
      lastTime: 0, lastMoveAt: 0, attempt: 0, reloading: false, pendingReload: null, detach: () => undefined,
    };
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
      on("loadeddata", () => { ready(layer); });
      on("canplay", () => { ready(layer); disarmStall(layer); });
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
          softFail(layer, "stalled", "stalled");
        }, stallMs);
      });
      // Chromium puede pausar un video silenciado cuando no es visible (ventana minimizada por la pausa de la salida): al volver sigue donde estaba.
      on("pause", () => {
        if (!doc.hidden) ensurePlaying(layer);
      });
    } else {
      on("load", () => { ready(layer); });
      on("error", () => { fail(layer, "imageError", "img error"); });
    }

    armLoad(layer);
    layers.push(layer);
    container.appendChild(el);
    el.setAttribute("src", url);
    if (kind === "video") ensurePlaying(layer);
    return layer;
  };

  const onVisibility = (): void => {
    for (const layer of [...layers]) {
      if (doc.hidden) {
        // Con la ventana oculta Chromium puede diferir la carga y pausar el video: no se juzga ni el tiempo de carga ni el avance.
        disarmStall(layer);
        disarmLoad(layer);
        stopWatch(layer);
        continue;
      }
      if (layer.state === "leaving") continue;
      if (layer.pendingReload) {
        reload(layer);   // un fallo blando ocurrido en la pausa: UNA recarga antes de darlo por perdido
        continue;
      }
      if (layer.state === "loading" || layer.reloading) armLoad(layer);
      ensurePlaying(layer);
      startWatch(layer);
    }
  };
  doc.addEventListener("visibilitychange", onVisibility);

  return {
    apply(next: BackgroundSpec): void {
      if (disposed) return;
      spec = next;
      setBase(next.color, next.transitionMs);
      const retry = next.retry ?? 0;

      const isMedia = (next.kind === "image" || next.kind === "video") && next.mediaUrl !== null;
      const key = isMedia ? `${next.kind}|${String(next.mediaUrl)}` : null;
      if (key === wantedKey) {
        if (key !== null && wantedFailed && retry !== wantedRetry && next.mediaUrl !== null) {
          // «Reintentar» / reaplicar el mismo archivo tras un fallo (H2): el operador lo pidió de nuevo (p. ej. el USB volvió).
          wantedFailed = false;
          wantedRetry = retry;
          for (const layer of [...layers]) if (layer.state === "loading") release(layer);
          create(next.kind as "image" | "video", next.mediaUrl, next);
          return;
        }
        wantedRetry = retry;
        // El mismo medio (p. ej. cambió el texto): NO se toca el elemento. Solo puede cambiar el silencio.
        for (const layer of layers) if (layer.kind === "video" && layer.state !== "leaving") (layer.el as HTMLVideoElement).muted = next.muted;
        return;
      }
      wantedKey = key;
      wantedFailed = false;
      wantedRetry = retry;
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
