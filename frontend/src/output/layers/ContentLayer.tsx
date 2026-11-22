import { useEffect, useLayoutEffect, useReducer, useRef, useState } from "react";
import type { ContentLayer as ContentState, Slide } from "@/ipc/protocol";
import { CANVAS_HEIGHT, CANVAS_WIDTH } from "../canvas";
import { assertRenderableSlide, fadeReducer, initFade } from "../crossfade";
import { fitFontSize } from "../fit";
import { inkBleed, themeBoxStyle, themeTextStyle } from "../theme";

/** Un búfer del crossfade: pinta un slide con su tema y ajusta el tamaño del texto al área (ajuste automático, sobre el lienzo de 1920×1080). */
function ContentSlot({ slide, active, ms }: { slide: Slide | null; active: boolean; ms: number }) {
  const boxRef = useRef<HTMLDivElement>(null);
  const textRef = useRef<HTMLDivElement>(null);
  const theme = slide?.theme;
  const autoFit = theme?.autoFit === true;

  // Antes de pintar: el slide nuevo entra ya con su tamaño final (nunca se ve «saltar»). Una búsqueda binaria de ≤ 9 mediciones.
  useLayoutEffect(() => {
    const box = boxRef.current;
    const text = textRef.current;
    if (!slide || !box || !text) return;
    const { theme: t } = slide;
    if (!t.autoFit) {
      text.style.fontSize = `${String(t.fontSize)}px`;
      return;
    }
    // El mismo criterio que la paginación del panel: el texto deja libre el sangrado de la tinta (contorno + sombra), que la caja recorta.
    const bleed = inkBleed(t);
    const size = fitFontSize(t.minFontSize, t.fontSize, (candidate) => {
      text.style.fontSize = `${String(candidate)}px`;
      return text.offsetHeight + bleed <= box.clientHeight;
    });
    text.style.fontSize = `${String(size)}px`;
  }, [slide, autoFit]);

  return (
    <div
      ref={boxRef}
      className="content-slot"
      data-slot-active={active ? "true" : "false"}
      style={{
        ...(theme ? themeBoxStyle(theme, CANVAS_WIDTH, CANVAS_HEIGHT) : { position: "absolute", inset: 0 }),
        opacity: active && slide ? 1 : 0,
        transition: `opacity ${String(ms)}ms linear`,
      }}
    >
      {slide && theme && (
        <div ref={textRef} className="content-text" style={themeTextStyle(theme, theme.fontSize)}>
          {slide.blocks.map((block, index) => (
            <div key={index} className="content-block" style={{ marginTop: index === 0 ? 0 : "0.5em" }}>
              {block.text}
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

/**
 * Capa 2: crossfade con dos búferes (ver `crossfade.ts`). `visible` lo decide el control maestro (Clear/Black/Logo la ocultan al instante, sin fundido;
 * el slide se conserva y reaparece al volver a Live).
 */
export function ContentLayer({ content, visible }: { content: ContentState; visible: boolean }) {
  // Un slide defectuoso falla AQUÍ, en el render del snapshot que lo trae (ver assertRenderableSlide), no en un render posterior.
  assertRenderableSlide(content.slide);
  const [fade, dispatch] = useReducer(fadeReducer, content.slide, initFade);
  const slideId = content.slide?.id ?? null;
  // El pedido se despacha DURANTE el render (patrón «estado derivado de props»): el búfer recibe el slide en el mismo commit, sin un render de retraso.
  const [requestedId, setRequestedId] = useState(slideId);
  if (requestedId !== slideId) {
    setRequestedId(slideId);
    dispatch({ type: "request", slide: content.slide, ms: content.transitionMs });
  }

  useEffect(() => {
    if (!fade.fading) return;
    const timer = setTimeout(() => {
      dispatch({ type: "done", fadeId: fade.fadeId });
    }, fade.ms + 60);
    return () => {
      clearTimeout(timer);
    };
  }, [fade.fading, fade.fadeId, fade.ms]);

  return (
    <div className="layer layer-content" data-layer="content" style={{ visibility: visible ? "visible" : "hidden" }}>
      <ContentSlot slide={fade.slots[0]} active={fade.active === 0} ms={fade.ms} />
      <ContentSlot slide={fade.slots[1]} active={fade.active === 1} ms={fade.ms} />
    </div>
  );
}
