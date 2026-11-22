import type { CSSProperties } from "react";
import type { TextTheme } from "@/ipc/protocol";

/** Estilo CSS del texto a partir del tema (medidas en píxeles del lienzo de 1920×1080). El tamaño de fuente lo fija aparte el ajuste automático. */
export function themeTextStyle(theme: TextTheme, fontSize: number): CSSProperties {
  const style: CSSProperties = {
    fontFamily: theme.fontFamily,
    fontSize: `${String(fontSize)}px`,
    lineHeight: theme.lineHeight,
    color: theme.color,
    textAlign: theme.align,
    whiteSpace: "pre-wrap",
    overflowWrap: "anywhere",
  };
  if (theme.outlineWidth > 0) {
    // El trazo va DETRÁS del relleno (paint-order) para que no adelgace las letras.
    style.WebkitTextStroke = `${String(theme.outlineWidth * 2)}px ${theme.outlineColor}`;
    style.paintOrder = "stroke fill";
  }
  if (theme.shadowBlur > 0 || theme.shadowOffset > 0) {
    style.textShadow = `0 ${String(theme.shadowOffset)}px ${String(theme.shadowBlur)}px ${theme.shadowColor}`;
  }
  return style;
}

/**
 * Cuánto se sale la TINTA por arriba/abajo de la caja de texto (la caja recorta lo que sobresale): contorno + sombra, menos la holgura que ya da el
 * interlineado (medio interlineado extra a cada lado del renglón). El texto debe dejar esa altura libre para que no se corte el contorno ni la sombra.
 * Lo usan la paginación del panel Y el ajuste automático de la salida (así, al reducir la letra, tampoco se recorta la tinta).
 */
export function inkBleed(theme: TextTheme): number {
  const halfLeading = Math.max(0, ((theme.lineHeight - 1) / 2) * theme.fontSize);
  return Math.max(0, Math.ceil(theme.outlineWidth + theme.shadowBlur + theme.shadowOffset - halfLeading));
}

/** Posición del área de texto: los márgenes del tema dentro del lienzo, y la alineación vertical. */
export function themeBoxStyle(theme: TextTheme, canvasWidth: number, canvasHeight: number): CSSProperties {
  return {
    position: "absolute",
    left: theme.marginLeft,
    top: theme.marginTop,
    width: canvasWidth - theme.marginLeft - theme.marginRight,
    height: canvasHeight - theme.marginTop - theme.marginBottom,
    display: "flex",
    flexDirection: "column",
    justifyContent: theme.verticalAlign === "top" ? "flex-start" : theme.verticalAlign === "bottom" ? "flex-end" : "center",
    overflow: "hidden",
  };
}
