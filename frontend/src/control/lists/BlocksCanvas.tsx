import type { TextTheme } from "@/ipc/protocol";
import { CANVAS_HEIGHT, CANVAS_WIDTH } from "@/output/canvas";
import { themeBoxStyle, themeTextStyle } from "@/output/theme";

/**
 * Un slide (uno o varios bloques) a escala sobre el lienzo REAL de 1920×1080 con el tema real: lo que pintará la salida (un `div` por bloque, el 2.º y siguientes con
 * `margin-top: .5em`, como `ContentSlot`). Solo es una vista previa: no proyecta nada.
 */
export function BlocksCanvas({ theme, blocks, width }: { theme: TextTheme; blocks: readonly string[]; width: number }) {
  const scale = width / CANVAS_WIDTH;
  return (
    <div className="overflow-hidden rounded border border-neutral-700 bg-blue-950" style={{ width, height: CANVAS_HEIGHT * scale }} data-testid="slide-canvas">
      <div style={{ width: CANVAS_WIDTH, height: CANVAS_HEIGHT, transform: `scale(${String(scale)})`, transformOrigin: "0 0", position: "relative" }}>
        <div style={themeBoxStyle(theme, CANVAS_WIDTH, CANVAS_HEIGHT)}>
          <div style={themeTextStyle(theme, theme.fontSize)}>
            {blocks.map((block, index) => (
              <div key={index} style={{ marginTop: index === 0 ? 0 : "0.5em" }}>{block}</div>
            ))}
          </div>
        </div>
      </div>
    </div>
  );
}
