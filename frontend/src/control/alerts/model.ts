import type { AlertOverlay, AlertSettingsPayload, AlertTheme, OutputStateChangedPayload } from "@/ipc/protocol";
import { ALERT_LINE_HEIGHT, ALERT_MAX_HEIGHT, ALERT_SIDE_PADDING, alertTextStyle, fitsInLines } from "@/output/alert";
import { CANVAS_WIDTH } from "@/output/canvas";
import { fitFontSize } from "@/output/fit";
import { applyStyle } from "../bible/measure";

/** Límites del texto (los mismos del backend: `AlertRules`). */
export const ALERT_MAX_CHARS = 300;
export const ALERT_MAX_PINNED = 30;
/** Duraciones que ofrece el panel (0 = «hasta quitarla», con un tope de 30 min en el backend). */
export const DURATION_CHOICES: readonly { sec: number; label: string }[] = [
  { sec: 15, label: "15 s" },
  { sec: 30, label: "30 s" },
  { sec: 60, label: "1 min" },
  { sec: 120, label: "2 min" },
  { sec: 300, label: "5 min" },
  { sec: 0, label: "Hasta quitarla (tope 30 min)" },
];

// ───────────────────────── estado de la alerta, DERIVADO del que confirma el backend ─────────────────────────

/**
 * Qué hay en pantalla, DERIVADO de `output.stateChanged` (nunca de lo que el panel «cree» haber enviado: tras recargar el panel con Ctrl+Alt+Shift+R se recupera solo de `output.getState`).
 * `live` = se ve (ventana abierta y sin pausa, y el modo maestro no la oculta); `hidden` = está en el estado pero no se ve (Texto limpio, Negro, Logo o salida cerrada), con el motivo.
 * Freeze NO la oculta (la alerta no se congela).
 */
export type AlertStatus = { kind: "none" } | { kind: "live"; alert: AlertOverlay } | { kind: "hidden"; alert: AlertOverlay; why: string };

const HIDDEN_BY_MASTER: Record<string, string> = { black: "Negro", clearText: "Texto limpio", logo: "Logo" };

export function alertLiveStatus(output: OutputStateChangedPayload | null): AlertStatus {
  const alert = output?.state.overlays.alert ?? null;
  if (!output || !alert) return { kind: "none" };
  if (!output.window.isOpen) return { kind: "hidden", alert, why: "Salida cerrada" };
  if (output.window.isPaused) return { kind: "hidden", alert, why: "Salida en pausa" };
  const why = HIDDEN_BY_MASTER[output.state.master];
  return why ? { kind: "hidden", alert, why } : { kind: "live", alert };
}

/** Segundos que faltan para que caduque (cuenta atrás COSMÉTICA: quien la quita es el reloj del backend). null si la fecha no se entiende. */
export function remainingSeconds(alert: Pick<AlertOverlay, "expiresAt">, nowMs: number): number | null {
  const at = Date.parse(alert.expiresAt);
  if (Number.isNaN(at)) return null;
  return Math.max(0, Math.ceil((at - nowMs) / 1000));
}

/** «0:23», «12:05». */
export function formatRemaining(seconds: number): string {
  const m = Math.floor(seconds / 60);
  const s = seconds % 60;
  return `${String(m)}:${String(s).padStart(2, "0")}`;
}

/** «hace 3 d», «hace 5 min»: la antigüedad de una alerta guardada (para no reutilizar por error un aviso viejo). */
export function ageLabel(iso: string | null, nowMs: number): string {
  if (iso === null) return "nunca mostrada";
  const at = Date.parse(iso);
  if (Number.isNaN(at)) return "";
  const sec = Math.max(0, Math.floor((nowMs - at) / 1000));
  if (sec < 60) return "hace un momento";
  if (sec < 3600) return `hace ${String(Math.floor(sec / 60))} min`;
  if (sec < 86400) return `hace ${String(Math.floor(sec / 3600))} h`;
  return `hace ${String(Math.floor(sec / 86400))} d`;
}

// ───────────────────────── ¿cabe en la cinta? (medición ANTES de mostrar) ─────────────────────────

/**
 * - `fits`: cabe en los renglones del tema al tamaño pedido.
 * - `shrunk`: cabe reduciendo la letra (ajuste automático) a `fontSize`.
 * - `grows`: no cabe en los renglones ni con la letra mínima: la cinta crecería (hasta su tope) → se SUGIERE el desplazamiento.
 * - `blocked`: ni creciendo cabe en la altura máxima: no se puede mostrar como cinta (nunca se corta el aviso).
 */
export type FitState = "fits" | "shrunk" | "grows" | "blocked";
export interface AlertFit {
  state: FitState;
  fontSize: number;
}

/**
 * Misma regla que la salida (`BandText`): con ajuste automático la letra baja de `fontSize` a `minFontSize` hasta caber en `maxLines` renglones; si ni el mínimo cabe, la cinta crece
 * hasta `ALERT_MAX_HEIGHT`. `heightAt(size)` = altura del texto a ese tamaño (en la aplicación, medida con el layout real; en los tests, simulada).
 */
export function evaluateFit(theme: AlertTheme, heightAt: (fontSize: number) => number): AlertFit {
  const budget = ALERT_MAX_HEIGHT - 2 * theme.paddingY;
  if (fitsInLines(heightAt(theme.fontSize), theme.fontSize, theme.maxLines)) return { state: "fits", fontSize: theme.fontSize };
  if (!theme.autoFit) return { state: heightAt(theme.fontSize) <= budget ? "grows" : "blocked", fontSize: theme.fontSize };
  const size = fitFontSize(theme.minFontSize, theme.fontSize, (c) => fitsInLines(heightAt(c), c, theme.maxLines));
  if (fitsInLines(heightAt(size), size, theme.maxLines)) return { state: "shrunk", fontSize: size };
  return { state: heightAt(size) <= budget ? "grows" : "blocked", fontSize: size };
}

/** Mide el texto con el layout REAL del navegador: reproduce, fuera de pantalla, la cinta de la salida (ancho del lienzo menos el relleno lateral, mismo estilo de texto). */
export interface AlertMeasurer {
  fit(text: string, theme: AlertTheme): AlertFit;
  dispose(): void;
}

export function createAlertMeasurer(doc: Document = document): AlertMeasurer {
  const host = doc.createElement("div");
  host.setAttribute("aria-hidden", "true");
  host.style.cssText = `all:initial;position:fixed;left:-20000px;top:0;width:${String(CANVAS_WIDTH - 2 * ALERT_SIDE_PADDING)}px;visibility:hidden;pointer-events:none;contain:layout style;`;
  const node = doc.createElement("div");
  host.appendChild(node);
  doc.body.appendChild(host);
  return {
    fit(text, theme) {
      node.textContent = text;
      applyStyle(node, { ...(alertTextStyle(theme, theme.fontSize) as Record<string, unknown>), overflowWrap: "anywhere" });
      return evaluateFit(theme, (size) => {
        node.style.fontSize = `${String(size)}px`;
        return node.offsetHeight;
      });
    },
    dispose() {
      host.remove();
    },
  };
}

/** Texto para el operador sobre cómo queda la alerta (o null si no hay nada que decir). */
export function fitMessage(fit: AlertFit, theme: AlertTheme): string | null {
  switch (fit.state) {
    case "fits":
      return null;
    case "shrunk":
      return `Cabe reduciendo la letra a ${String(fit.fontSize)} px.`;
    case "grows":
      return `No cabe en ${String(theme.maxLines)} renglones ni con la letra mínima (${String(fit.fontSize)} px): la cinta crecería. Usa el desplazamiento.`;
    case "blocked":
      return "No cabe en la cinta ni creciendo: se cortaría. Acorta el aviso o usa el desplazamiento.";
  }
}

// ───────────────────────── mínimo del ajuste y validación de los ajustes ─────────────────────────

export const HEX = /^#[0-9a-fA-F]{6}([0-9a-fA-F]{2})?$/;

/** Mínimo del ajuste automático: SIEMPRE menor que el tamaño (si no, la red de seguridad no podría reducir nada) y nunca menos de 12. */
export function alertMinFontSize(size: number, min: number): number {
  return Math.max(12, Math.min(Math.round(min), size - 1));
}

/** Al cambiar el tamaño: el mínimo propuesto es el 60 % (como el tema por defecto), o el que había si sigue siendo menor que el nuevo tamaño. */
export function themeWithAlertFontSize(theme: AlertTheme, size: number): AlertTheme {
  const s = Math.round(size);
  const min = theme.minFontSize < s ? theme.minFontSize : Math.floor(s * 0.6);
  return { ...theme, fontSize: s, minFontSize: alertMinFontSize(s, min) };
}

/** La validación de la pantalla (la del backend es la que manda; esto solo evita mandar lo evidentemente inválido). Devuelve el motivo o null. */
export function alertSettingsProblem(s: AlertSettingsPayload): string | null {
  const t = s.theme;
  if (![t.color, t.outlineColor, t.bandColor].every((c) => HEX.test(c))) return "Los colores deben ser #RRGGBB o #RRGGBBAA.";
  if (!(t.fontSize >= 16 && t.fontSize <= 200)) return "El tamaño de la letra debe estar entre 16 y 200.";
  if (!(t.minFontSize >= 12 && t.minFontSize < t.fontSize)) return "El tamaño mínimo debe estar entre 12 y el tamaño de la letra (menor que él).";
  if (!(t.outlineWidth >= 0 && t.outlineWidth <= 20)) return "El grosor del contorno debe estar entre 0 y 20.";
  if (!(t.paddingY >= 0 && t.paddingY <= 120)) return "El relleno vertical debe estar entre 0 y 120.";
  if (!(t.maxLines >= 1 && t.maxLines <= 3)) return "Los renglones de la cinta deben estar entre 1 y 3.";
  if (!(t.tickerSpeed >= 60 && t.tickerSpeed <= 400)) return "La velocidad del desplazamiento debe estar entre 60 y 400 px/s.";
  if (s.durationSec !== 0 && !(s.durationSec >= 5 && s.durationSec <= 1800)) return "La duración debe ser 0 (hasta quitarla) o de 5 a 1800 s.";
  if (s.mode === "ticker" && !s.tickerEnabled) return "El desplazamiento está desactivado: el modo por defecto debe ser la cinta.";
  return null;
}

export { ALERT_LINE_HEIGHT };
