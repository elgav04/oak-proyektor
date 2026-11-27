import type { AudioRouteDto, AudioTrackPhase, MasterMode, OutputStateChangedPayload } from "@/ipc/protocol";

const HIDDEN_BY_MASTER: Partial<Record<MasterMode, string>> = { black: "Negro", clearText: "Texto limpio", logo: "Logo" };

/**
 * Qué pasa con el video en primer plano, DERIVADO del estado que confirma el backend (nunca de lo que el panel envió; tras recargar el panel se recupera sola).
 * `onScreen` = suena y se ve; `paused` = en pausa o listo, y se vería; `ended` = terminó con «quedarse»; `hidden` = hay un video pero la salida no lo muestra (y está pausado y callado).
 * Freeze NO oculta el video (a diferencia del texto).
 */
export type ForegroundStatus =
  | { kind: "none" }
  | { kind: "onScreen"; fileName: string }
  | { kind: "paused"; fileName: string }
  | { kind: "ended"; fileName: string }
  | { kind: "hidden"; fileName: string; why: string };

export function foregroundStatus(output: OutputStateChangedPayload | null): ForegroundStatus {
  const fg = output?.state.foreground ?? null;
  if (!output || fg === null) return { kind: "none" };
  const win = output.window;
  const why = !win.isOpen ? "Salida cerrada" : win.isPaused ? "Salida en pausa" : (HIDDEN_BY_MASTER[output.state.master] ?? null);
  if (why !== null) return { kind: "hidden", fileName: fg.fileName, why };
  if (fg.phase === "playing") return { kind: "onScreen", fileName: fg.fileName };
  return fg.phase === "ended" ? { kind: "ended", fileName: fg.fileName } : { kind: "paused", fileName: fg.fileName };
}

export function foregroundBadge(status: ForegroundStatus): string {
  switch (status.kind) {
    case "none": return "";
    case "onScreen": return "EN PANTALLA";
    case "paused": return "EN PAUSA";
    case "ended": return "TERMINÓ";
    case "hidden": return `OCULTO · ${status.why}`;
  }
}

/** 0:00 / 1:02:03. Tiempos negativos o no finitos se muestran como 0:00. */
export function formatClock(ms: number): string {
  const total = Number.isFinite(ms) && ms > 0 ? Math.floor(ms / 1000) : 0;
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  const two = (n: number): string => String(n).padStart(2, "0");
  return h > 0 ? `${String(h)}:${two(m)}:${two(s)}` : `${String(m)}:${two(s)}`;
}

export const TRACK_PHASE_LABEL: Record<AudioTrackPhase, string> = {
  playing: "SONANDO",
  paused: "EN PAUSA",
  ended: "TERMINÓ",
  deviceLost: "DISPOSITIVO DESCONECTADO",
  error: "ERROR",
};

export interface RouteSummary {
  /** Por dónde salen las pistas («Interfaz USB», «Predeterminado de Windows: …»). */
  tracks: string;
  /** Por dónde sale el audio del video en primer plano (siempre el predeterminado de Windows). */
  video: string;
  /** Avisos VISIBLES, el primero es el más grave. Vacío = todo coincide. */
  warnings: string[];
}

/**
 * El cableado en palabras: el panel muestra SIEMPRE los dos dispositivos y avisa si no coinciden. Las pistas usan el «dispositivo de la iglesia» (o el predeterminado de Windows si no hay uno elegido);
 * el audio del video sale por el predeterminado de Windows (limitación de WebView2). No se cambia nunca el predeterminado de Windows por programa.
 */
export function routeSummary(route: AudioRouteDto): RouteSummary {
  const dflt = route.defaultDeviceName ?? "ninguno";
  const warnings: string[] = [];
  let tracks: string;
  if (route.usesWindowsDefault) tracks = `Predeterminado de Windows: ${dflt}`;
  else tracks = route.churchConnected ? (route.churchDeviceName ?? "Dispositivo de la iglesia") : `${route.churchDeviceName ?? "Dispositivo de la iglesia"} (NO conectado)`;
  if (!route.usesWindowsDefault && !route.churchConnected) {
    warnings.push(`El dispositivo de audio de la iglesia (${route.churchDeviceName ?? "sin nombre"}) no está conectado: las pistas no suenan hasta que vuelva. No se usa otro dispositivo por su cuenta.`);
  }
  if (route.mismatch) {
    warnings.push(`Las pistas salen por «${route.churchDeviceName ?? "el dispositivo de la iglesia"}» pero el audio del video sale por el predeterminado de Windows: «${dflt}». Cámbialo en Windows (Sonido) o comprueba el cableado.`);
  }
  if (route.defaultDeviceName === null) warnings.push("Windows no tiene ningún dispositivo de audio predeterminado: el video no tiene por dónde sonar.");
  return { tracks, video: dflt, warnings };
}

/** Texto para el aviso de que Windows cambió el predeterminado durante el servicio. */
export function defaultChangeMessage(fromName: string | null, toName: string | null): string {
  return `Windows cambió el dispositivo de audio predeterminado: de «${fromName ?? "ninguno"}» a «${toName ?? "ninguno"}». El audio del video sale por el nuevo.`;
}

/** Porcentaje 0–100 de un deslizador, acotado. */
export function clampPct(n: number): number {
  return Number.isFinite(n) ? Math.min(100, Math.max(0, Math.round(n))) : 0;
}
