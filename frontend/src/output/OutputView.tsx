import { useEffect, useState } from "react";
import { ipc } from "@/ipc/instance";
import type { OutputState } from "@/ipc/protocol";
import { isEmergencyKey } from "./emergency";
import { OutputScreen } from "./OutputScreen";

/** La salida es "tonta": recibe snapshots por IPC y los pinta; nada más. */
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
    // Respaldo del atajo global: pide a C# minimizar la salida. La salida no decide nada; solo avisa.
    const onKeyDown = (e: KeyboardEvent): void => {
      if (!isEmergencyKey(e)) return;
      e.preventDefault();
      ipc.send("output.recover", {});
    };
    window.addEventListener("keydown", onKeyDown);
    return () => {
      off();
      window.removeEventListener("keydown", onKeyDown);
    };
  }, []);

  return <OutputScreen state={state} />;
}
