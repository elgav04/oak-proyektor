import { useCallback, useRef } from "react";
import type { OutputState } from "@/ipc/protocol";
import { OutputErrorBoundary } from "./OutputErrorBoundary";
import { OutputStage } from "./OutputStage";

/** Pinta el snapshot recibido; si no se puede pintar, conserva lo último que sí se pintó. */
export function OutputScreen({ state }: { state: OutputState | null }) {
  const lastPainted = useRef<OutputState | null>(null);
  const remember = useCallback((painted: OutputState | null) => {
    lastPainted.current = painted;
  }, []);

  return (
    <OutputErrorBoundary resetKey={state?.revision ?? null} fallback={() => <OutputStage state={lastPainted.current} />}>
      <OutputStage state={state} onPainted={remember} />
    </OutputErrorBoundary>
  );
}
