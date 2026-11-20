import type { Slide } from "@/ipc/protocol";

/**
 * Crossfade con DOS búferes (A/B) como máquina de estados pura. Reglas:
 * - El slide nuevo entra en el búfer libre y el anterior sale del suyo a la vez: en ningún momento hay un hueco sin contenido.
 * - Si llega otro slide MIENTRAS hay un fundido en curso, no se reinicia (eso dejaría el texto casi invisible en una ráfaga de cambios):
 *   se guarda solo el ÚLTIMO pedido y arranca cuando termina el fundido actual (ráfaga de 50 cambios = el primero y el último).
 * - Un pedido del slide que ya es el destino no hace nada.
 * - Duración 0 = cambio instantáneo en el mismo búfer.
 */
export interface FadeRequest {
  slide: Slide | null;
  ms: number;
}

export interface FadeState {
  slots: readonly [Slide | null, Slide | null];
  /** Búfer que muestra (o al que está llegando) el slide actual. */
  active: 0 | 1;
  fading: boolean;
  /** Cambia con cada fundido: el temporizador de fin solo vale para el suyo. */
  fadeId: number;
  /** Duración del fundido en curso (o del último). */
  ms: number;
  pending: FadeRequest | null;
}

export type FadeAction = { type: "request"; slide: Slide | null; ms: number } | { type: "done"; fadeId: number };

/** Estado inicial: el slide ya presente se muestra directamente, sin fundido (p. ej. al recargar la página). */
export function initFade(slide: Slide | null): FadeState {
  return { slots: [slide, null], active: 0, fading: false, fadeId: 0, ms: 0, pending: null };
}

const idOf = (slide: Slide | null): string | null => slide?.id ?? null;

function start(state: FadeState, slide: Slide | null, ms: number): FadeState {
  if (ms <= 0) {
    const slots: [Slide | null, Slide | null] = [null, null];
    slots[state.active] = slide;
    return { ...state, slots, fading: false, ms: 0, pending: null };
  }
  const next: 0 | 1 = state.active === 0 ? 1 : 0;
  const slots: [Slide | null, Slide | null] = [state.slots[0], state.slots[1]];
  slots[next] = slide;
  return { slots, active: next, fading: true, fadeId: state.fadeId + 1, ms, pending: null };
}

export function fadeReducer(state: FadeState, action: FadeAction): FadeState {
  if (action.type === "request") {
    const target = state.pending ? idOf(state.pending.slide) : idOf(state.slots[state.active]);
    if (idOf(action.slide) === target) return state;
    if (state.fading) return { ...state, pending: { slide: action.slide, ms: action.ms } };
    return start(state, action.slide, action.ms);
  }
  if (!state.fading || action.fadeId !== state.fadeId) return state;
  const old: 0 | 1 = state.active === 0 ? 1 : 0;
  const slots: [Slide | null, Slide | null] = [state.slots[0], state.slots[1]];
  slots[old] = null;
  const settled: FadeState = { ...state, slots, fading: false, pending: null };
  if (state.pending && idOf(state.pending.slide) !== idOf(slots[state.active])) return start(settled, state.pending.slide, state.pending.ms);
  return settled;
}

/**
 * Comprueba que un slide es pintable. Se llama en el render del snapshot que lo trae (aunque el fundido lo deje en cola): si es defectuoso FALLA ahí,
 * antes de que ese snapshot conste como «pintado», y el ErrorBoundary de la salida conserva lo último bueno (el fallback nunca re-renderiza el defecto).
 */
export function assertRenderableSlide(slide: Slide | null): void {
  if (slide === null) return;
  const bad = (what: string): never => {
    throw new Error(`Slide no pintable: ${what}`);
  };
  if (typeof slide.id !== "string") bad("id");
  if (!Array.isArray(slide.blocks)) bad("blocks");
  for (const block of slide.blocks) if (typeof (block as { text: unknown }).text !== "string") bad("el texto de un bloque no es una cadena");
  const theme = slide.theme as unknown as Record<string, unknown> | null;
  if (typeof theme !== "object" || theme === null) bad("theme");
  else if (typeof theme.fontFamily !== "string" || typeof theme.fontSize !== "number" || typeof theme.color !== "string") bad("tema incompleto");
}