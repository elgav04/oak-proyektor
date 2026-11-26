import { useEffect, useState, type ReactNode } from "react";
import type { BackgroundLayer as BackgroundState, ContentLayer as ContentState, OutputState, OverlayLayer as OverlayState } from "@/ipc/protocol";
import type { BackgroundFailure } from "./background/engine";
import { CANVAS_HEIGHT, CANVAS_WIDTH, computeCanvasTransform, type CanvasTransform } from "./canvas";
import { BackgroundLayer } from "./layers/BackgroundLayer";
import { ContentLayer } from "./layers/ContentLayer";
import { OverlayLayer } from "./layers/OverlayLayer";
import { visibilityFor } from "./visibility";

interface Props {
  state: OutputState | null;
  /** Se llama DESPUÉS de que el snapshot se pintó sin errores (efecto posterior al commit). */
  onPainted?: (state: OutputState | null) => void;
  onBackgroundFailure?: (failure: BackgroundFailure) => void;
}

function useCanvasTransform(): CanvasTransform {
  const [transform, setTransform] = useState(() => computeCanvasTransform(window.innerWidth, window.innerHeight));
  useEffect(() => {
    const update = (): void => {
      setTransform(computeCanvasTransform(window.innerWidth, window.innerHeight));
    };
    window.addEventListener("resize", update);
    return () => {
      window.removeEventListener("resize", update);
    };
  }, []);
  return transform;
}

const BLACK_BACKGROUND: BackgroundState = { kind: "none", color: "#000000", mediaUrl: null, fileName: null, muted: true, transitionMs: 0, warning: null, retry: 0 };
const NO_CONTENT: ContentState = { slide: null, transitionMs: 0 };
const NO_OVERLAYS: OverlayState = { alert: null };

/** Viewport + lienzo virtual de 1920×1080 escalado a la ventana. Solo geometría: las capas van dentro. */
export function OutputFrame({ children }: { children: ReactNode }) {
  const transform = useCanvasTransform();
  return (
    <div className="viewport">
      <div
        className="canvas"
        style={{ width: CANVAS_WIDTH, height: CANVAS_HEIGHT, transform: `translate(${String(transform.x)}px, ${String(transform.y)}px) scale(${String(transform.scale)})` }}
      >
        {children}
      </div>
    </div>
  );
}

/** Capa 1. Se pinta FUERA del `OutputErrorBoundary` (H8): un error de render del contenido no puede desmontarla ni reiniciar el video de fondo. */
export function OutputBackground({ state, onBackgroundFailure }: { state: OutputState | null; onBackgroundFailure?: (failure: BackgroundFailure) => void }) {
  return <BackgroundLayer key="background" background={state?.background ?? BLACK_BACKGROUND} onFailure={onBackgroundFailure} />;
}

/** Capas 2 y 3 y las cortinas de Black y Logo: lo que SÍ puede fallar al pintar un snapshot y por eso va dentro del boundary. */
export function OutputForeground({ state, onPainted }: { state: OutputState | null; onPainted?: (state: OutputState | null) => void }) {
  useEffect(() => {
    onPainted?.(state);
  }, [state, onPainted]);

  const visible = visibilityFor(state?.master ?? "black");
  return (
    <>
      <ContentLayer key="content" content={state?.content ?? NO_CONTENT} visible={visible.content} />
      <OverlayLayer key="overlays" overlays={state?.overlays ?? NO_OVERLAYS} visible={visible.overlays} />
      {/* El fondo no se desmonta con Black/Logo: se cubre (el video sigue reproduciéndose por debajo y reaparece sin reiniciarse). */}
      <div className="curtain curtain-black" data-layer="black" style={{ display: visible.black || state === null ? "block" : "none" }} />
      <div className="curtain curtain-logo" data-layer="logo" style={{ display: visible.logo ? "flex" : "none" }}>
        LOGO
      </div>
    </>
  );
}

/**
 * Tres capas apiladas dentro del lienzo virtual de 1920×1080 (escalado a la ventana):
 * 1 fondo, 2 contenido, 3 superposiciones; encima, las cortinas de Black y Logo. Los contenedores tienen clave estable: ninguno se remonta al cambiar el texto.
 * Versión SIN boundary (sondeo y tests); la salida real usa `OutputScreen`, que deja el fondo fuera del boundary.
 */
export function OutputStage({ state, onPainted, onBackgroundFailure }: Props) {
  return (
    <OutputFrame>
      <OutputBackground state={state} onBackgroundFailure={onBackgroundFailure} />
      <OutputForeground state={state} onPainted={onPainted} />
    </OutputFrame>
  );
}
