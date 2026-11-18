import { useEffect, useState } from "react";
import { ipc } from "@/ipc/instance";
import type { OutputState } from "@/ipc/protocol";

/** La salida es "tonta": pinta el último snapshot que recibe y nada más. */
export function OutputView() {
  const [state, setState] = useState<OutputState | null>(null);

  useEffect(() => {
    const apply = (next: OutputState): void => {
      setState((current) => (current && next.revision < current.revision ? current : next));   // ignora snapshots viejos
    };
    const off = ipc.subscribe("output.stateChanged", ({ state: next }) => { apply(next); });
    // Estado inicial: la ventana puede cargarse después de que ocurrió el último evento.
    void (async () => {
      for (let attempt = 0; attempt < 10; attempt++) {
        try {
          apply((await ipc.request("output.getState", {})).state);
          return;
        } catch {
          // reintenta; mientras tanto se queda en negro
        }
      }
    })();
    return off;
  }, []);

  // Contenedores con clave estable: el fondo nunca se remonta al cambiar el texto.
  return (
    <div key="background" className="stage" style={{ backgroundColor: state?.backgroundColor ?? "#000000" }}>
      <div key="content" className="content">{state?.text ?? ""}</div>
    </div>
  );
}
