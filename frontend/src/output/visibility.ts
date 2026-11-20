import type { MasterMode } from "@/ipc/protocol";

/** Qué capas se ven en cada control maestro. Cambiar de modo es instantáneo: nunca espera a una transición. */
export interface LayerVisibility {
  background: boolean;
  content: boolean;
  overlays: boolean;
  /** Cortina negra encima de todo (el video de fondo sigue reproduciéndose por debajo). */
  black: boolean;
  logo: boolean;
}

export function visibilityFor(master: MasterMode): LayerVisibility {
  switch (master) {
    case "live":
    // Freeze solo llega aquí si es el PRIMER snapshot (no había nada pintado que conservar): se pinta como Live.
    case "freeze":
      return { background: true, content: true, overlays: true, black: false, logo: false };
    case "clearText":
      return { background: true, content: false, overlays: false, black: false, logo: false };
    case "black":
      return { background: false, content: false, overlays: false, black: true, logo: false };
    case "logo":
      return { background: false, content: false, overlays: false, black: false, logo: true };
  }
}
