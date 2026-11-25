import type { TextTheme } from "@/ipc/protocol";
import { CANVAS_HEIGHT, CANVAS_WIDTH } from "@/output/canvas";
import { themeBoxStyle, themeTextStyle } from "@/output/theme";
import type { Boundary, SongSlide } from "./slides";

/** Una diapositiva a escala sobre el lienzo REAL de 1920×1080 con el tema real (lo que pintará la salida, no una aproximación). */
export function SlideCanvas({ theme, text, width }: { theme: TextTheme; text: string; width: number }) {
  const scale = width / CANVAS_WIDTH;
  return (
    <div className="overflow-hidden rounded border border-neutral-700 bg-blue-950" style={{ width, height: CANVAS_HEIGHT * scale }} data-testid="slide-canvas">
      <div style={{ width: CANVAS_WIDTH, height: CANVAS_HEIGHT, transform: `scale(${String(scale)})`, transformOrigin: "0 0", position: "relative" }}>
        <div style={themeBoxStyle(theme, CANVAS_WIDTH, CANVAS_HEIGHT)}>
          <div style={themeTextStyle(theme, theme.fontSize)}>{text}</div>
        </div>
      </div>
    </div>
  );
}

const BOUNDARY_LABEL: Record<Boundary, string> = { section: "Inicio de sección", manual: "Corte manual", auto: "Corte automático" };
const BOUNDARY_CLASS: Record<Boundary, string> = {
  section: "border-l-neutral-400 bg-neutral-100 text-neutral-700",
  manual: "border-l-blue-600 bg-blue-50 text-blue-900",
  auto: "border-l-amber-500 bg-amber-50 text-amber-900",
};

/** Lo máximo que se pinta de golpe (cada una es un lienzo escalado): una canción normal tiene 10–40; el orden permite hasta 200 elementos. */
export const MAX_RENDERED_SLIDES = 240;

/**
 * Las diapositivas derivadas, en el orden de reproducción. Se distingue el corte AUTOMÁTICO (la medición decidió partir) del MANUAL (hay una línea en blanco en el texto):
 * el manual lo controla el operador añadiendo o quitando una línea en blanco. Es solo una vista previa: no proyecta nada.
 */
export function SlideStrip({
  slides, theme, busy, selected, onSelect, live = null,
}: { slides: readonly SongSlide[] | null; theme: TextTheme; busy: boolean; selected: number; onSelect: (index: number) => void; live?: number | null }) {
  if (slides === null) return <p className="text-sm text-neutral-500">{busy ? "Calculando las diapositivas…" : "Sin diapositivas todavía."}</p>;
  if (slides.length === 0) return <p className="text-sm text-neutral-500">No hay diapositivas: escribe el texto de alguna sección y añádela al orden.</p>;
  const shown = slides.slice(0, MAX_RENDERED_SLIDES);
  return (
    <div className="flex flex-col gap-2">
      <p className="flex flex-wrap items-center gap-3 text-xs text-neutral-600">
        <span>{String(slides.length)} diapositiva(s){busy ? " · actualizando…" : ""}</span>
        <span className="flex items-center gap-1"><i className="inline-block h-3 w-1 bg-blue-600" /> corte manual (línea en blanco)</span>
        <span className="flex items-center gap-1"><i className="inline-block h-3 w-1 bg-amber-500" /> corte automático (no cabía)</span>
      </p>
      <ol className="grid grid-cols-1 gap-3 sm:grid-cols-2 xl:grid-cols-3" aria-label="Diapositivas derivadas">
        {shown.map((slide, index) => (
          <li
            key={slide.key}
            data-boundary={slide.boundary}
            aria-current={index === selected ? "true" : undefined}
            data-live={index === live ? "true" : undefined}
            // `content-visibility: auto`: el navegador no pinta las que están fuera de la vista (la lista puede ser larga).
            style={{ contentVisibility: "auto", containIntrinsicSize: "auto 190px" }}
            className={`flex cursor-pointer flex-col gap-1 rounded border-l-4 p-1 ${BOUNDARY_CLASS[slide.boundary]} ${index === live ? "ring-4 ring-red-600" : index === selected ? "ring-2 ring-blue-700" : ""}`}
            onClick={() => { onSelect(index); }}
          >
            <span className="flex justify-between text-xs font-semibold">
              <span>{String(slide.orderIndex + 1)}. {slide.sectionLabel}</span>
              <span>{index === live ? <b className="text-red-700">EN PANTALLA · </b> : null}{BOUNDARY_LABEL[slide.boundary]}</span>
            </span>
            <SlideCanvas theme={theme} text={slide.text} width={260} />
          </li>
        ))}
      </ol>
      {slides.length > shown.length && <p className="text-xs text-neutral-600">Se muestran las primeras {String(shown.length)} de {String(slides.length)} diapositivas.</p>}
    </div>
  );
}
