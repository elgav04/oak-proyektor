import type { OutputState, OutputStateChangedPayload } from "@/ipc/protocol";
import { isNewer } from "@/ipc/sequence";

/**
 * Lo que la salida tiene pintado y lo que tiene guardado.
 *
 * FREEZE (definición): al llegar un snapshot con `master: "freeze"` la salida DEJA DE APLICAR snapshots de contenido y capas y conserva lo que tenía
 * pintado (`shown`); los snapshots posteriores solo actualizan `latest`. Al llegar uno con otro modo maestro (descongelar) se aplica el ÚLTIMO.
 * El video de fondo que ya estaba en pantalla SIGUE reproduciéndose (no se toca el elemento; el motor de fondo no recibe ningún cambio), y también
 * se retienen los CAMBIOS de fondo hasta descongelar. Limitación: si la página se recarga con la salida congelada no hay nada «pintado» que conservar y se
 * pinta el último estado (ver CLAUDE.md).
 *
 * EXCEPCIÓN (v18): el VIDEO EN PRIMER PLANO (`foreground`) tampoco se congela, por la misma razón: poder pausarlo, silenciarlo o detenerlo siempre (con Freeze, `shown` toma `foreground` del snapshot más reciente).
 *
 * EXCEPCIÓN (v15): la ALERTA (`overlays`) NO se congela. Mostrarla y, sobre todo, QUITARLA (botón, caducidad del reloj del backend o el atajo global) debe funcionar siempre: si Freeze la
 * retuviera, un aviso quedaría clavado en pantalla sin forma de quitarlo. Con Freeze, `shown` conserva texto y fondo pero toma `overlays` del snapshot más reciente.
 */
export interface OutputView {
  /** Secuencia del último snapshot recibido (también de los retenidos): el boundary de errores se reinicia con ella. */
  seq: number;
  /** Lo que se pinta. */
  shown: OutputState;
  /** El estado más reciente que mandó el backend (lo que se aplicará al descongelar). */
  latest: OutputState;
  frozen: boolean;
  panelAlert: boolean;
}

/** ¿Es la misma alerta (o ninguna)? Por contenido: cada snapshot llega con objetos nuevos aunque nada haya cambiado, y no hay que re-renderizar de más. */
function sameAlert(a: OutputState["overlays"], b: OutputState["overlays"]): boolean {
  if (a === b) return true;
  if (a.alert === null || b.alert === null) return a.alert === b.alert;
  return JSON.stringify(a.alert) === JSON.stringify(b.alert);
}

/** ¿Es el mismo video en primer plano (o ninguno)? Por contenido, igual que la alerta. */
function sameForeground(a: OutputState["foreground"], b: OutputState["foreground"]): boolean {
  if (a === b) return true;
  if (a === null || b === null) return false;
  return JSON.stringify(a) === JSON.stringify(b);
}

/** Aplica un snapshot. Devuelve `prev` sin cambios si no es más nuevo que el último aplicado. */
export function nextView(prev: OutputView | null, snap: OutputStateChangedPayload): OutputView | null {
  if (!isNewer(prev?.seq ?? null, snap.seq)) return prev;
  const panelAlert = snap.window.panelUnresponsive && snap.window.panelAlertOnOutput;
  const freezing = snap.state.master === "freeze";
  // Congelado: texto y fondo se retienen; la alerta y el video en primer plano siguen al snapshot más reciente (misma instancia si no cambiaron: no se re-renderiza de más).
  const shown =
    freezing && prev !== null
      ? sameAlert(prev.shown.overlays, snap.state.overlays) && sameForeground(prev.shown.foreground, snap.state.foreground)
        ? prev.shown
        : { ...prev.shown, overlays: snap.state.overlays, foreground: snap.state.foreground }
      : snap.state;
  return { seq: snap.seq, shown, latest: snap.state, frozen: freezing && prev !== null, panelAlert };
}
