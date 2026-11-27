import type { ForegroundBackdrop, ForegroundPhase, ReportedPlayback } from "@/ipc/protocol";

/** Lo que el reproductor necesita del video en primer plano (primitivos: se compara por valor, no por la identidad del objeto del snapshot). */
export interface ForegroundSpec {
  id: string;
  /** URL `file:///` de la COPIA local, ya validada por el backend; la salida no valida nada. */
  url: string;
  phase: ForegroundPhase;
  /** Volumen EFECTIVO (clip × maestro), 0–100. */
  volumePct: number;
  muted: boolean;
  loop: boolean;
  seekMs: number;
  /** «Ir a `seekMs`» se aplica cuando este número cambia (0 = ninguno). */
  seekSeq: number;
  fadeInMs: number;
  fadeOutMs: number;
  backdrop: ForegroundBackdrop;
}

/** Lo que la salida informa al backend (`media.reportProgress`): posición y estado REALES del video. */
export interface ForegroundReport {
  id: string;
  positionMs: number;
  durationMs: number;
  state: ReportedPlayback;
  /** unsupportedFormat | decodeError | readError | stalled; solo con `state: "error"`. */
  errorCode: string | null;
}

export interface ForegroundPlayer {
  /**
   * `spec` nulo = no hay video en primer plano (el que hubiera sale con su fundido de salida). `visible` = el control maestro lo muestra (Live y Freeze); si es falso el video se oculta al
   * instante Y se pausa y silencia (el elemento sigue montado: conserva su posición). Con el MISMO id nunca se remonta ni reinicia el elemento.
   */
  apply(spec: ForegroundSpec | null, visible: boolean): void;
  dispose(): void;
}

export interface ForegroundOptions {
  onReport?: (report: ForegroundReport) => void;
  /** Cada cuánto se informa el avance mientras suena. */
  progressMs?: number;
  /** Sin avanzar `currentTime` durante este tiempo (con la ventana visible y sonando) se da por atascado. */
  stallMs?: number;
  /** Tiempo para decodificar el primer fotograma (con la ventana visible). */
  loadTimeoutMs?: number;
  /** Duración de la bajada de volumen antes de pausar (corta: callar tiene que ser inmediato). */
  quietMs?: number;
}

type Timer = ReturnType<typeof setTimeout>;

interface Item {
  spec: ForegroundSpec;
  box: HTMLDivElement;
  video: HTMLVideoElement;
  /** Ya decodificó su primer fotograma. */
  shown: boolean;
  /** El fundido de entrada (opacidad) ya se hizo: ocultar y volver a mostrar por un control maestro es instantáneo. */
  revealed: boolean;
  visible: boolean;
  released: boolean;
  failed: boolean;
  leaving: boolean;
  lastSeekSeq: number;
  /** El primer arranque sube el audio con el fundido de entrada; las reanudaciones, con uno corto. */
  everPlayed: boolean;
  endReported: boolean;
  lastReport: string;
  lastTime: number;
  lastMoveAt: number;
  volumeTimer: ReturnType<typeof setInterval> | null;
  pauseTimer: Timer | null;
  loadTimer: Timer | null;
  progressTimer: ReturnType<typeof setInterval> | null;
  timers: Set<Timer>;
  detach: () => void;
}

// Códigos de MediaError.
const MEDIA_ERR_NETWORK = 2;
const MEDIA_ERR_SRC_NOT_SUPPORTED = 4;

const FILL = "position:absolute;left:0;top:0;width:100%;height:100%;pointer-events:none;";

const clampPct = (n: number): number => Math.min(100, Math.max(0, Number.isFinite(n) ? n : 0));

/**
 * El reproductor del video en primer plano (capa entre el contenido y las superposiciones). Un `<video>` por reproducción (id), montado en un contenedor propio: nunca toca el fondo
 * (otro `<video loop>` debajo, mismo nodo, `currentTime` continuo). Es «tonto»: pinta lo que dice el estado y REPORTA lo que de verdad pasa (avance, fin, errores) para que el backend decida.
 * Garantías: (1) un video oculto (control maestro) o en pausa NUNCA suena: se baja el volumen y se pausa (≈ 100 ms); (2) volver a mostrarlo no lo reanuda; (3) cambiar de video o quitarlo libera
 * el anterior DE VERDAD (pause, sin src, load, nodo fuera); (4) un fallo cae a «nada en primer plano» e informa (nunca negro ni congelado).
 */
export function createForegroundPlayer(container: HTMLElement, options: ForegroundOptions = {}): ForegroundPlayer {
  const doc = container.ownerDocument;
  const progressMs = options.progressMs ?? 250;
  const stallMs = options.stallMs ?? 5000;
  const loadTimeoutMs = options.loadTimeoutMs ?? 15000;
  const quietMs = options.quietMs ?? 90;

  let current: Item | null = null;
  const leaving: Item[] = [];
  let disposed = false;

  const later = (item: Item, ms: number, fn: () => void): void => {
    const timer = setTimeout(() => {
      item.timers.delete(timer);
      fn();
    }, ms);
    item.timers.add(timer);
  };

  const report = (item: Item, state: ReportedPlayback, errorCode: string | null = null, force = false): void => {
    if (item.released || disposed) return;
    const duration = Number.isFinite(item.video.duration) ? Math.round(item.video.duration * 1000) : 0;
    const position = Math.round(item.video.currentTime * 1000);
    // El avance en pausa solo se repite si cambió algo: no se manda ruido al backend.
    const key = `${state}|${errorCode ?? ""}|${String(position)}|${String(duration)}`;
    if (!force && state === "paused" && key === item.lastReport) return;
    item.lastReport = key;
    options.onReport?.({ id: item.spec.id, positionMs: position, durationMs: duration, state, errorCode });
  };

  const stopVolume = (item: Item): void => {
    if (item.volumeTimer !== null) {
      clearInterval(item.volumeTimer);
      item.volumeTimer = null;
    }
  };

  /** Rampa de volumen en pasos de ≈ 10 ms, hasta 20 (nunca un salto: sin chasquidos). 0 ms = al instante. */
  const rampVolume = (item: Item, targetPct: number, ms: number, done?: () => void): void => {
    stopVolume(item);
    const target = clampPct(targetPct) / 100;
    const from = item.video.volume;
    if (ms <= 0 || Math.abs(target - from) < 0.005) {
      item.video.volume = target;
      done?.();
      return;
    }
    let step = 0;
    // Pasos de ≥ 10 ms (los temporizadores del navegador no bajan de ≈ 4 ms): una rampa de 90 ms son 9 pasos, no 20 pasos que tardarían más de lo pedido.
    const steps = Math.max(1, Math.min(20, Math.round(ms / 10)));
    item.volumeTimer = setInterval(() => {
      step++;
      item.video.volume = step >= steps ? target : from + ((target - from) * step) / steps;
      if (step >= steps) {
        stopVolume(item);
        done?.();
      }
    }, Math.max(1, Math.round(ms / steps)));
  };

  const disarmLoad = (item: Item): void => {
    if (item.loadTimer !== null) {
      clearTimeout(item.loadTimer);
      item.loadTimer = null;
    }
  };

  const release = (item: Item): void => {
    if (item.released) return;
    item.released = true;
    item.detach();
    stopVolume(item);
    disarmLoad(item);
    if (item.pauseTimer !== null) clearTimeout(item.pauseTimer);
    if (item.progressTimer !== null) clearInterval(item.progressTimer);
    for (const t of item.timers) clearTimeout(t);
    item.timers.clear();
    const index = leaving.indexOf(item);
    if (index >= 0) leaving.splice(index, 1);
    if (current === item) current = null;
    try {
      item.video.pause();
    } catch {
      /* un elemento ya roto no debe impedir liberarlo */
    }
    item.video.removeAttribute("src");
    try {
      item.video.load();
    } catch {
      /* idem */
    }
    item.box.remove();
  };

  /** Callar y pausar: baja el volumen y pausa (conserva la posición). Cancelable: si llega un «reproducir» antes de pausar, no se pausa. */
  const quiet = (item: Item): void => {
    if (item.released || item.video.paused || item.pauseTimer !== null) return;
    rampVolume(item, 0, quietMs);
    item.pauseTimer = setTimeout(() => {
      item.pauseTimer = null;
      if (item.released) return;
      try {
        item.video.pause();
      } catch {
        /* idem */
      }
    }, quietMs + 10);
  };

  const fail = (item: Item, code: string): void => {
    if (item.released || item.failed || disposed) return;
    item.failed = true;
    item.box.style.opacity = "0";   // lo roto no se muestra: cae a «nada en primer plano» (el backend lo quita al recibir el error)
    quiet(item);
    report(item, "error", code, true);
  };

  const play = (item: Item): void => {
    if (item.released || item.failed) return;
    if (item.pauseTimer !== null) {
      clearTimeout(item.pauseTimer);
      item.pauseTimer = null;
    }
    const video = item.video;
    const target = item.spec.muted ? 0 : item.spec.volumePct;
    if (video.paused || video.ended) {
      // Entra con el fundido de entrada la primera vez y con uno corto al reanudar.
      const rampMs = item.everPlayed ? 60 : item.spec.fadeInMs;
      item.everPlayed = true;
      video.volume = 0;
      const started = video.play() as Promise<void> | undefined;
      started?.catch((err: unknown) => {
        const name = err instanceof Error ? err.name : "";
        if (name === "AbortError" || item.released) return;   // una pausa o una liberación posterior canceló este intento
        // Sin permiso para reproducir con sonido: se reintenta silenciado antes de rendirse (el panel verá que no hay sonido; el video se ve).
        if (!video.muted) {
          video.muted = true;
          (video.play() as Promise<void> | undefined)?.catch(() => undefined);
        } else {
          fail(item, "decodeError");
        }
      });
      rampVolume(item, target, rampMs);
    } else {
      rampVolume(item, target, 100);
    }
  };

  /** Opacidad: el fundido de entrada solo la primera vez que se muestra; ocultar y volver a mostrar por un control maestro es instantáneo. */
  const applyOpacity = (item: Item): void => {
    if (item.released || item.leaving || item.failed) return;
    const show = item.shown && item.visible;
    if (show && !item.revealed) {
      item.revealed = true;
      item.box.style.transition = `opacity ${String(Math.max(0, item.spec.fadeInMs))}ms linear`;
    } else {
      item.box.style.transition = "none";
    }
    item.box.style.opacity = show ? "1" : "0";
  };

  const watch = (item: Item): void => {
    if (item.released || item.failed || item.leaving) return;
    const video = item.video;
    const now = Date.now();
    if (!item.visible || video.paused || doc.hidden || !item.shown) {
      item.lastTime = video.currentTime;
      item.lastMoveAt = now;
      return;
    }
    if (video.currentTime !== item.lastTime) {
      item.lastTime = video.currentTime;
      item.lastMoveAt = now;
    } else if (!video.seeking && now - item.lastMoveAt >= stallMs) {
      fail(item, "stalled");
    }
  };

  const create = (spec: ForegroundSpec): Item => {
    const box = doc.createElement("div");
    box.style.cssText = `${FILL}opacity:0;${spec.backdrop === "black" ? "background:#000;" : ""}`;
    box.setAttribute("data-foreground", spec.id);
    const video = doc.createElement("video");
    video.style.cssText = `${FILL}object-fit:contain;`;
    video.preload = "auto";
    video.playsInline = true;
    video.loop = spec.loop;
    video.muted = spec.muted;
    video.volume = 0;
    video.setAttribute("src", spec.url);
    box.appendChild(video);
    container.appendChild(box);

    const item: Item = {
      spec, box, video, shown: false, revealed: false, visible: false, released: false, failed: false, leaving: false, lastSeekSeq: spec.seekSeq, everPlayed: false, endReported: false,
      lastReport: "", lastTime: 0, lastMoveAt: Date.now(), volumeTimer: null, pauseTimer: null, loadTimer: null, progressTimer: null, timers: new Set<Timer>(), detach: () => undefined,
    };

    const onLoaded = (): void => {
      if (item.shown) return;
      item.shown = true;
      disarmLoad(item);
      applyOpacity(item);
      report(item, video.paused ? "paused" : "playing", null, true);
    };
    const onError = (): void => {
      const code = video.error?.code;
      fail(item, code === MEDIA_ERR_SRC_NOT_SUPPORTED ? "unsupportedFormat" : code === MEDIA_ERR_NETWORK ? "readError" : "decodeError");
    };
    const onEnded = (): void => {
      if (item.endReported || item.spec.loop) return;
      item.endReported = true;
      report(item, "ended", null, true);
    };
    const onState = (): void => {
      report(item, video.paused ? "paused" : "playing", null, true);
    };
    const events: [string, () => void][] = [["loadeddata", onLoaded], ["error", onError], ["ended", onEnded], ["play", onState], ["pause", onState], ["seeked", onState]];
    for (const [name, handler] of events) video.addEventListener(name, handler);
    item.detach = () => {
      for (const [name, handler] of events) video.removeEventListener(name, handler);
    };

    item.progressTimer = setInterval(() => {
      if (item.released || item.failed || item.leaving) return;
      watch(item);
      if (item.shown) report(item, video.paused ? "paused" : "playing");
    }, progressMs);
    const armLoad = (): void => {
      disarmLoad(item);
      item.loadTimer = setTimeout(() => {
        item.loadTimer = null;
        if (item.shown || item.released) return;
        if (doc.hidden) armLoad();   // con la ventana oculta Chromium puede diferir la carga: no se juzga
        else fail(item, "stalled");
      }, loadTimeoutMs);
    };
    armLoad();
    return item;
  };

  const leave = (item: Item): void => {
    if (item.released || item.leaving) return;
    item.leaving = true;
    const ms = Math.max(0, item.spec.fadeOutMs);
    if (current === item) current = null;
    quiet(item);
    if (ms <= 0 || !item.shown) {
      later(item, quietMs + 20, () => {
        release(item);
      });   // da tiempo a bajar el volumen sin chasquido
      item.box.style.transition = "none";
      item.box.style.opacity = "0";
    } else {
      item.box.style.transition = `opacity ${String(ms)}ms linear`;
      item.box.style.opacity = "0";
      later(item, ms + 60, () => {
        release(item);
      });
    }
    leaving.push(item);
    while (leaving.length > 2) {
      const oldest = leaving.shift();
      if (oldest) release(oldest);
    }
  };

  const update = (item: Item, spec: ForegroundSpec, visible: boolean): void => {
    item.spec = spec;
    item.visible = visible;
    item.video.loop = spec.loop;
    item.video.muted = spec.muted;
    if (spec.seekSeq !== item.lastSeekSeq) {
      item.lastSeekSeq = spec.seekSeq;
      item.endReported = false;
      try {
        item.video.currentTime = Math.max(0, spec.seekMs / 1000);
      } catch {
        /* sin metadatos todavía: el navegador ignora el cambio */
      }
    }
    // Un video oculto (control maestro) NUNCA suena aunque el backend aún no lo haya pausado: la salida lo calla por su cuenta.
    if (spec.phase === "playing" && visible) play(item);
    else if (!item.video.paused) quiet(item);
    else if (!visible) item.video.volume = 0;
    applyOpacity(item);
    if (item.shown) report(item, item.video.paused ? "paused" : "playing", null, true);
  };

  return {
    apply(spec, visible) {
      if (disposed) return;
      if (spec === null) {
        if (current) leave(current);
        return;
      }
      if (current && current.spec.id !== spec.id) leave(current);
      current ??= create(spec);
      update(current, spec, visible);
    },
    dispose() {
      disposed = true;
      const all = [...leaving];
      if (current) all.push(current);
      for (const item of all) release(item);
      current = null;
    },
  };
}
