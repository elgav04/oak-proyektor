import type { TextTheme } from "@/ipc/protocol";
import { CANVAS_HEIGHT, CANVAS_WIDTH } from "@/output/canvas";
import { inkBleed, themeBoxStyle, themeTextStyle } from "@/output/theme";

export { inkBleed };

/**
 * Medición REAL de texto: reproduce, fuera de pantalla, la estructura exacta con que la salida pinta un slide (`ContentSlot`: caja con los márgenes del tema
 * sobre el lienzo virtual de 1920×1080 > texto con el tema > un `div` por bloque, el 2.º y siguientes con `margin-top: .5em`) y pregunta al motor de layout
 * del navegador si el texto cabe. Mide a `theme.fontSize` (la paginación hace que quepa a ESE tamaño; el ajuste automático de la salida queda solo como red
 * de seguridad). Una medición es una lectura de `offsetHeight` sobre un nodo pequeño (≈ 0,2–1 ms); la paginación hace pocas por página (búsqueda binaria).
 */

const UNITLESS = new Set(["lineHeight", "opacity", "zIndex"]);

/** Aplica un `CSSProperties` de React a un elemento (React añade «px» a los números; aquí hay que hacerlo a mano). */
export function applyStyle(el: HTMLElement, style: Record<string, unknown>): void {
  const target = el.style as unknown as Record<string, string>;
  for (const [key, value] of Object.entries(style)) {
    if (value === undefined || value === null) continue;
    target[key] = typeof value === "number" ? (UNITLESS.has(key) ? String(value) : `${String(value)}px`) : typeof value === "string" ? value : "";
  }
}

/** Renglones de holgura que se reservan al paginar (H3). */
export const SAFETY_LINES = 0.25;

/**
 * Holgura de seguridad (≈ 0,25 renglones): el panel y la salida pueden estar en monitores con otro escalado de Windows (p. ej. 125 % y 100 %) y el motor de
 * layout puede repartir los renglones distinto (otro salto de línea cambia el alto en un renglón entero). Con esta reserva, una diferencia pequeña no llega a
 * recortar; y si llegara, el ajuste automático de la salida reduce la letra (ver `projectionTheme`).
 */
export function safetyMargin(theme: TextTheme): number {
  return Math.ceil(SAFETY_LINES * theme.lineHeight * theme.fontSize);
}

/** Alto disponible para el texto al paginar: el de la caja (lienzo menos márgenes) menos el sangrado de la tinta y la holgura de seguridad. */
export function availableHeight(theme: TextTheme): number {
  return CANVAS_HEIGHT - theme.marginTop - theme.marginBottom - inkBleed(theme) - safetyMargin(theme);
}

export interface BlocksMeasurer {
  /** ¿Caben estos bloques (en este orden) en el área de texto del tema? */
  fits(blocks: readonly string[]): boolean;
  /** Alto (px del lienzo) que ocupa el texto de estos bloques; 0 si no hay layout (p. ej. happy-dom). */
  height(blocks: readonly string[]): number;
  dispose(): void;
}

export function createBlocksMeasurer(theme: TextTheme, doc: Document = document): BlocksMeasurer {
  const host = doc.createElement("div");
  // `all: initial` aísla la medición de los estilos de la página (Tailwind, herencia): la salida no los tiene.
  host.setAttribute("aria-hidden", "true");
  host.style.cssText = `all:initial;position:fixed;left:-20000px;top:0;width:${String(CANVAS_WIDTH)}px;height:${String(CANVAS_HEIGHT)}px;visibility:hidden;pointer-events:none;contain:layout style;`;
  const box = doc.createElement("div");
  applyStyle(box, themeBoxStyle(theme, CANVAS_WIDTH, CANVAS_HEIGHT) as Record<string, unknown>);
  const text = doc.createElement("div");
  applyStyle(text, themeTextStyle(theme, theme.fontSize) as Record<string, unknown>);
  box.appendChild(text);
  host.appendChild(box);
  doc.body.appendChild(host);

  const limit = availableHeight(theme);
  const setBlocks = (blocks: readonly string[]): void => {
    while (text.childElementCount > blocks.length) text.lastElementChild?.remove();
    blocks.forEach((block, index) => {
      let node = text.children[index] as HTMLElement | undefined;
      if (!node) {
        node = doc.createElement("div");
        text.appendChild(node);
      }
      node.style.marginTop = index === 0 ? "0" : "0.5em";
      node.textContent = block;
    });
  };
  const height = (blocks: readonly string[]): number => {
    setBlocks(blocks);
    return text.offsetHeight;
  };
  return {
    height,
    fits: (blocks) => height(blocks) <= limit,
    dispose: () => {
      host.remove();
    },
  };
}
