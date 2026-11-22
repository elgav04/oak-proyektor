import { useCallback, useRef } from "react";
import type { OutputState } from "@/ipc/protocol";
import type { BackgroundFailure } from "./background/engine";
import { OutputErrorBoundary } from "./OutputErrorBoundary";
import { OutputBackground, OutputForeground, OutputFrame } from "./OutputStage";

/**
 * Pinta el snapshot recibido; si no se puede pintar, conserva lo último que sí se pintó.
 * `seq` es la secuencia GLOBAL del snapshot (cambia con cualquier cambio de estado): el boundary reintenta cuando llega uno distinto del que
 * falló. NO se usa `state.revision`: desde el protocolo v2 solo cuenta cambios de los controles maestros, así que un snapshot con otro
 * texto (misma revisión) nunca provocaría el reintento y la salida se quedaría fija en lo último pintado (hallazgo I-C).
 *
 * El FONDO queda FUERA del boundary (H8): si el contenido falla al pintar, el árbol de contenido se sustituye por el fallback, pero `BackgroundLayer` sigue
 * montado y su motor no se destruye (el video no se reinicia y no se ve el color de base). El fondo sigue al snapshot más reciente.
 */
export function OutputScreen({ state, seq, onBackgroundFailure }: { state: OutputState | null; seq: number | null; onBackgroundFailure?: (failure: BackgroundFailure) => void }) {
  const lastPainted = useRef<OutputState | null>(null);
  const remember = useCallback((painted: OutputState | null) => {
    lastPainted.current = painted;
  }, []);

  return (
    <OutputFrame>
      <OutputBackground state={state} onBackgroundFailure={onBackgroundFailure} />
      <OutputErrorBoundary resetKey={seq} fallback={() => <OutputForeground state={lastPainted.current} />}>
        <OutputForeground state={state} onPainted={remember} />
      </OutputErrorBoundary>
    </OutputFrame>
  );
}
