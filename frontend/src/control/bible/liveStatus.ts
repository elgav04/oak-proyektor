import type { OutputStateChangedPayload } from "@/ipc/protocol";

/**
 * Qué muestra la insignia de la pestaña de Biblia, DERIVADO del estado real que confirma el backend (`output.stateChanged`), nunca de lo que el panel
 * «cree» haber enviado (hallazgo H5; CLAUDE.md: el panel solo es réplica):
 * - `live`: la página seleccionada es EXACTAMENTE el slide actual del estado, la ventana está abierta y sin pausa y el modo maestro es Live (se ve).
 * - `hidden`: es el slide actual pero NO se ve (Negro, Limpiar, Logo, Congelado o salida en pausa/cerrada): se dice por qué.
 * - `ready`: la página seleccionada no es lo que hay en el estado (no se envió, el backend la rechazó, otro texto la reemplazó o se quitó).
 */
export type LiveStatus = { kind: "ready" } | { kind: "live" } | { kind: "hidden"; why: string };

const HIDDEN_BY_MASTER: Record<string, string> = {
  black: "Negro",
  clearText: "Texto limpio",
  logo: "Logo",
  freeze: "Congelado",
};

export function bibleLiveStatus(output: OutputStateChangedPayload | null, page: readonly string[] | null | undefined): LiveStatus {
  if (!output || !page) return { kind: "ready" };
  const slide = output.state.content.slide;
  if (!slide || slide.blocks.length !== page.length || !slide.blocks.every((b, i) => b.text === page[i])) return { kind: "ready" };
  return visibilityStatus(output);
}

/**
 * ¿Se VE lo que hay en la salida? (ventana abierta, sin pausa y en modo Live). Para quien ya sabe que el slide es el suyo (por texto o por `origin`): `live` si se ve,
 * `hidden` con el motivo si no.
 */
export function visibilityStatus(output: OutputStateChangedPayload): LiveStatus {
  const win = output.window;
  if (!win.isOpen) return { kind: "hidden", why: "Salida cerrada" };
  if (win.isPaused) return { kind: "hidden", why: "Salida en pausa" };
  const master = output.state.master;
  if (master === "live") return { kind: "live" };
  return { kind: "hidden", why: HIDDEN_BY_MASTER[master] ?? master };
}
