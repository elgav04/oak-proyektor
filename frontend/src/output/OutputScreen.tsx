import { useCallback, useRef } from "react";
import type { OutputState } from "@/ipc/protocol";
import { OutputErrorBoundary } from "./OutputErrorBoundary";
import { OutputStage } from "./OutputStage";

/**
 * Pinta el snapshot recibido; si no se puede pintar, conserva lo último que sí se pintó.
 * `seq` es la secuencia GLOBAL del snapshot (cambia con cualquier cambio de estado): el boundary reintenta cuando llega uno distinto del que
 * falló. NO se usa `state.revision`: desde el protocolo v2 solo cuenta cambios de los controles maestros, así que un snapshot con otro
 * texto (misma revisión) nunca provocaría el reintento y la salida se quedaría fija en lo último pintado (hallazgo I-C).
 */
export function OutputScreen({ state, seq }: { state: OutputState | null; seq: number | null }) {
  const lastPainted = useRef<OutputState | null>(null);
  const remember = useCallback((painted: OutputState | null) => {
    lastPainted.current = painted;
  }, []);

  return (
    <OutputErrorBoundary resetKey={seq} fallback={() => <OutputStage state={lastPainted.current} />}>
      <OutputStage state={state} onPainted={remember} />
    </OutputErrorBoundary>
  );
}
