export interface Clock {
  now(): number;
  setTimeout(fn: () => void, ms: number): unknown;
  clearTimeout(handle: unknown): void;
}

export const realClock: Clock = {
  now: () => Date.now(),
  setTimeout: (fn, ms) => setTimeout(fn, ms),
  clearTimeout: (handle) => {
    clearTimeout(handle as ReturnType<typeof setTimeout>);
  },
};

export interface LatestSender<T> {
  /** Manda ya si hace más de `intervalMs` que no se manda nada; si no, deja SOLO el último valor para cuando toque (los intermedios se descartan). */
  push(value: T): void;
  /** Manda ya (Enter): cancela lo pendiente. */
  now(value: T): void;
  /** Descarta lo pendiente sin mandarlo (p. ej. antes de limpiar la salida). */
  cancel(): void;
}

/**
 * «El último gana»: con las flechas pulsadas muy rápido, a la salida no llega cada versículo intermedio (cada uno sería otro fundido y otro mensaje):
 * como mucho uno cada `intervalMs`, y el ÚLTIMO valor siempre llega (el pendiente se manda al vencer el intervalo). El backend y la salida ya reducen una
 * ráfaga al primero y al último; esto evita además inundar el canal de entrada.
 */
export function createLatestSender<T>(send: (value: T) => void, intervalMs: number, clock: Clock = realClock): LatestSender<T> {
  let lastSent = Number.NEGATIVE_INFINITY;
  let pending: { value: T } | null = null;
  let timer: unknown = null;

  const fire = (value: T): void => {
    lastSent = clock.now();
    send(value);
  };
  const stopTimer = (): void => {
    if (timer !== null) clock.clearTimeout(timer);
    timer = null;
  };

  return {
    push(value) {
      const wait = lastSent + intervalMs - clock.now();
      if (wait <= 0 && timer === null) {
        pending = null;
        fire(value);
        return;
      }
      pending = { value };
      if (timer === null) {
        timer = clock.setTimeout(() => {
          timer = null;
          const next = pending;
          pending = null;
          if (next) fire(next.value);
        }, Math.max(0, wait));
      }
    },
    now(value) {
      stopTimer();
      pending = null;
      fire(value);
    },
    cancel() {
      stopTimer();
      pending = null;
    },
  };
}
