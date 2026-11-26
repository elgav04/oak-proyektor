import type { CSSProperties } from "react";
import type { AlertTheme } from "@/ipc/protocol";
import { CANVAS_HEIGHT, CANVAS_WIDTH } from "./canvas";

/** Interlineado fijo de la cinta (el tema de la alerta no lo expone: una cinta no necesita más). */
export const ALERT_LINE_HEIGHT = 1.2;
/** Margen lateral del texto dentro de la cinta (la cinta misma ocupa todo el ancho del lienzo). */
export const ALERT_SIDE_PADDING = 60;
/** La cinta nunca pasa del 30 % del alto del lienzo (3 renglones): si el texto no cabe ni así, el panel ya lo avisó y la salida recorta antes que tapar la pantalla. */
export const ALERT_MAX_HEIGHT = Math.round(CANVAS_HEIGHT * 0.3);
/** Duración del fundido al aparecer y al desaparecer (compositor: solo `opacity`). */
export const ALERT_FADE_MS = 200;

/**
 * Cuánto dura UNA pasada del desplazamiento: el texto entra por la derecha del lienzo y sale por la izquierda, a velocidad constante (px del lienzo por segundo).
 * Recorrido = ancho del lienzo + ancho del texto. Nunca menos de 1 s (un texto vacío o una medición fallida no deben dar una animación instantánea).
 */
export function tickerDurationSec(textWidth: number, speed: number, canvasWidth: number = CANVAS_WIDTH): number {
  const safeSpeed = Number.isFinite(speed) && speed > 0 ? speed : 180;
  const travel = canvasWidth + (Number.isFinite(textWidth) && textWidth > 0 ? textWidth : 0);
  return Math.max(1, Math.round((travel / safeSpeed) * 100) / 100);
}

/** ¿Cabe el texto en `maxLines` renglones a este tamaño? `height` es la altura medida del texto (con `lineHeight` fijo). Un píxel de holgura por el redondeo del layout. */
export function fitsInLines(height: number, fontSize: number, maxLines: number): boolean {
  return height <= maxLines * fontSize * ALERT_LINE_HEIGHT + 1;
}

/** Estilo del texto de la alerta (el tamaño lo fija aparte el ajuste automático). El contorno va DETRÁS del relleno para no adelgazar las letras. */
export function alertTextStyle(theme: AlertTheme, fontSize: number): CSSProperties {
  const style: CSSProperties = {
    fontFamily: theme.fontFamily,
    fontSize: `${String(fontSize)}px`,
    lineHeight: ALERT_LINE_HEIGHT,
    color: theme.color,
    textAlign: theme.align,
    fontWeight: 700,
  };
  if (theme.outlineWidth > 0) {
    style.WebkitTextStroke = `${String(theme.outlineWidth * 2)}px ${theme.outlineColor}`;
    style.paintOrder = "stroke fill";
  }
  return style;
}

/** La cinta: todo el ancho del lienzo, anclada arriba o abajo, con la altura que pida el contenido (hasta el tope). */
export function alertBandStyle(theme: AlertTheme): CSSProperties {
  return {
    position: "absolute",
    left: 0,
    width: CANVAS_WIDTH,
    ...(theme.position === "top" ? { top: 0 } : { bottom: 0 }),
    boxSizing: "border-box",
    maxHeight: ALERT_MAX_HEIGHT,
    overflow: "hidden",
    background: theme.bandColor,
    padding: `${String(theme.paddingY)}px ${String(ALERT_SIDE_PADDING)}px`,
  };
}
