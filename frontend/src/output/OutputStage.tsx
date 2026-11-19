import { useEffect } from "react";
import type { OutputState } from "@/ipc/protocol";

interface Props {
  state: OutputState | null;
  /** Se llama DESPUÉS de que el snapshot se pintó sin errores (efecto posterior al commit). */
  onPainted?: (state: OutputState | null) => void;
}

/** Pinta un snapshot. Contenedores con clave estable: el fondo nunca se remonta al cambiar el texto. */
export function OutputStage({ state, onPainted }: Props) {
  useEffect(() => {
    onPainted?.(state);
  }, [state, onPainted]);

  return (
    <div key="background" className="stage" style={{ backgroundColor: state?.backgroundColor ?? "#000000" }}>
      <div key="content" className="content">{state?.text ?? ""}</div>
    </div>
  );
}
