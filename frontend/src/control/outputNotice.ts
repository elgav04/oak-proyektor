import type { DisplayInfo, OutputPauseReason, OutputWindowStatus } from "@/ipc/protocol";

/**
 * Qué aviso muestra el panel cuando la salida está en pausa. Función pura sobre las dos réplicas (lista de monitores y estado de la salida):
 * solo decide cómo PRESENTAR lo que manda C#; no guarda ni decide estado de negocio. Nunca propone abrir la salida por sí sola en otro monitor:
 * lo único que ofrece son botones para que el operador elija.
 */
export type OutputNotice =
  | { kind: "none" }
  /** Pausa «manual», «emergency», «unstable» o «panelCrossed» por arrastrar la ventana (o `displayLost` cuando aún no hay lista de monitores para juzgarlo). */
  | { kind: "paused"; reason: OutputPauseReason }
  /** El monitor de la salida no está conectado y no hay otro que se pueda usar (no aloja el panel): «Proyector desconectado». */
  | { kind: "displayLost" }
  /** El monitor de la salida no está conectado, pero hay OTRO monitor que se podría usar: «Apareció un monitor distinto al de la salida». */
  | { kind: "newDisplay"; alternatives: DisplayInfo[] }
  /** Pausa por `displayLost`, pero el monitor de la salida ya está conectado de nuevo: espera al operador (p. ej. «restaurar automáticamente» desactivado). */
  | { kind: "displayBack" }
  /** El panel está sobre el monitor de la salida: se reanuda sola en cuanto el panel salga de él. Sin botones obligatorios. */
  | { kind: "panelOnOutput" }
  /** Windows está en modo «Duplicar» (Win+P): un solo monitor lógico. Se reanuda sola al volver a «Extender». */
  | { kind: "duplicate" };

export interface OutputDescription {
  notice: OutputNotice;
  /** El monitor asignado a la salida está conectado (o no hay monitor asignado: ventana normal). Solo entonces «Reanudar salida» puede funcionar. */
  canResumeInPlace: boolean;
}

export function describeOutput(displays: readonly DisplayInfo[], window: OutputWindowStatus | null): OutputDescription {
  if (!window?.isOpen || !window.isPaused) return { notice: { kind: "none" }, canResumeInPlace: window?.isOpen ?? false };

  const reason = window.pauseReason ?? "manual";
  // Modo Duplicar: hay un solo monitor lógico; no hay nada que reanudar a mano hasta que vuelva «Extender» (entonces se reanuda sola).
  if (reason === "duplicateMode") return { notice: { kind: "duplicate" }, canResumeInPlace: false };

  // Sin lista de monitores (aún no llegó) no se puede juzgar: se muestra el aviso genérico y no se afirma nada del monitor.
  if (displays.length === 0) return { notice: { kind: "paused", reason }, canResumeInPlace: true };

  const targetConnected = window.displayId === null || displays.some((d) => d.id === window.displayId);
  // «Reanudar» restaura a pantalla completa en el monitor de la salida: si ahora aloja el panel taparía al operador (C# no lo reanuda), así que no se ofrece.
  // Una salida en ventana normal (`isWindowed`) sí se puede reanudar donde esté.
  const targetHostsPanel = !window.isWindowed && displays.some((d) => d.id === window.displayId && d.hostsPanel);
  const canResume = targetConnected && !targetHostsPanel;

  // El panel está sobre el monitor de la salida: aviso propio («Muévelo para reanudar»), sin botones. Se reanuda sola cuando salga.
  if (reason === "panelCrossed" && targetHostsPanel) return { notice: { kind: "panelOnOutput" }, canResumeInPlace: false };

  if (reason !== "displayLost") return { notice: { kind: "paused", reason }, canResumeInPlace: canResume };

  if (targetConnected) return { notice: { kind: "displayBack" }, canResumeInPlace: canResume };

  // El proyector no está. «Otro monitor que se podría usar» = no aloja el panel (ahí la salida taparía al operador).
  const alternatives = displays.filter((d) => !d.hostsPanel && d.id !== window.displayId);
  return alternatives.length > 0
    ? { notice: { kind: "newDisplay", alternatives }, canResumeInPlace: false }
    : { notice: { kind: "displayLost" }, canResumeInPlace: false };
}
