import type { OutputStateChangedPayload } from "@/ipc/protocol";
import { bibleLiveStatus, type LiveStatus } from "../bible/liveStatus";
import type { SongSlide } from "./slides";

/**
 * Qué diapositiva de la canción está EN PANTALLA y si se ve, DERIVADO del estado que confirma el backend (`output.stateChanged`), nunca de lo que el panel
 * «cree» haber enviado (hallazgo H5; CLAUDE.md: el panel solo es réplica). Funciones puras.
 */

/**
 * Índice de la diapositiva cuyo texto es EXACTAMENTE el slide actual del estado confirmado, o null si el estado no es de esta canción. Un coro repetido da varias
 * coincidencias idénticas: `hint` (la que se pidió proyectar) desempata; sin pista, la primera.
 */
export function confirmedSlideIndex(output: OutputStateChangedPayload | null, slides: readonly SongSlide[] | null | undefined, hint: number | null): number | null {
  const slide = output?.state.content.slide;
  if (!slides || !slide || slide.blocks.length !== 1) return null;
  const text = slide.blocks[0]?.text;
  if (text === undefined) return null;
  if (hint !== null && slides[hint]?.text === text) return hint;
  const found = slides.findIndex((s) => s.text === text);
  return found >= 0 ? found : null;
}

/** EN VIVO solo si ese texto es el slide actual, la salida está abierta, sin pausa y en modo Live; «OCULTO · …» si es el slide actual pero no se ve; si no, LISTO. */
export function songLiveStatus(output: OutputStateChangedPayload | null, slides: readonly SongSlide[] | null | undefined, hint: number | null): { status: LiveStatus; liveIndex: number | null } {
  const liveIndex = confirmedSlideIndex(output, slides, hint);
  if (liveIndex === null) return { status: { kind: "ready" }, liveIndex: null };
  return { status: bibleLiveStatus(output, [slides?.[liveIndex]?.text ?? ""]), liveIndex };
}
