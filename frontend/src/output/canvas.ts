export const CANVAS_WIDTH = 1920;
export const CANVAS_HEIGHT = 1080;

export interface CanvasTransform {
  scale: number;
  x: number;
  y: number;
}

/**
 * La salida se renderiza en un lienzo virtual fijo de 1920×1080 que se escala (conservando la proporción y centrado) a la ventana:
 * el diseño es idéntico en cualquier resolución o escalado de Windows. Con otra proporción quedan barras negras.
 */
export function computeCanvasTransform(width: number, height: number): CanvasTransform {
  if (!(width > 0) || !(height > 0)) return { scale: 1, x: 0, y: 0 };
  const scale = Math.min(width / CANVAS_WIDTH, height / CANVAS_HEIGHT);
  return { scale, x: (width - CANVAS_WIDTH * scale) / 2, y: (height - CANVAS_HEIGHT * scale) / 2 };
}
