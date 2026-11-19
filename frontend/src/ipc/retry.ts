export interface RetryOptions {
  /** Espera tras el primer fallo. Por defecto 500 ms; se duplica en cada fallo seguido. */
  baseMs?: number;
  /** Tope de la espera. Por defecto 8 s. */
  maxMs?: number;
  /** Para pruebas: sustituye la espera. Debe resolver al cumplirse el tiempo o cuando se aborte la señal. */
  sleep?: (ms: number, signal: AbortSignal) => Promise<void>;
}

export interface Retrier {
  /** Empieza (o reintenta de inmediato si ya está esperando). */
  start(): void;
  /** Como `start`, pero además reinicia la espera creciente: «Reconectar». */
  kick(): void;
  /** Detiene todo para siempre. */
  stop(): void;
}

function defaultSleep(ms: number, signal: AbortSignal): Promise<void> {
  return new Promise((resolve) => {
    if (signal.aborted) {
      resolve();
      return;
    }
    const timer = setTimeout(done, ms);
    signal.addEventListener("abort", done, { once: true });
    function done(): void {
      clearTimeout(timer);
      signal.removeEventListener("abort", done);
      resolve();
    }
  });
}

/**
 * Ejecuta `task` hasta que tenga éxito, reintentando INDEFINIDAMENTE con espera creciente (500 ms, 1 s, 2 s… hasta 8 s).
 * Tras un éxito queda en reposo; `kick()` lo vuelve a ejecutar (resincronizar). Nunca lanza.
 */
export function createRetrier(task: () => Promise<void>, options: RetryOptions = {}): Retrier {
  const baseMs = options.baseMs ?? 500;
  const maxMs = options.maxMs ?? 8000;
  const sleep = options.sleep ?? defaultSleep;
  let stopped = false;
  let running = false;
  let attempt = 0;
  let waiting: AbortController | null = null;

  const run = async (): Promise<void> => {
    running = true;
    try {
      while (!stopped) {
        try {
          await task();
          return;
        } catch {
          const delay = Math.min(baseMs * 2 ** attempt, maxMs);
          attempt++;
          const controller = new AbortController();
          waiting = controller;
          await sleep(delay, controller.signal);
          waiting = null;
        }
      }
    } finally {
      running = false;
    }
  };

  return {
    start() {
      if (stopped) return;
      if (running) {
        waiting?.abort();
        return;
      }
      attempt = 0;
      void run();
    },
    kick() {
      if (stopped) return;
      attempt = 0;
      if (running) {
        waiting?.abort();
        return;
      }
      void run();
    },
    stop() {
      stopped = true;
      waiting?.abort();
    },
  };
}