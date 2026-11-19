import { useEffect, useState } from "react";
import { ipc } from "@/ipc/instance";
import type { OutputState, OutputStateChangedPayload } from "@/ipc/protocol";
import { createRetrier } from "@/ipc/retry";
import { isNewer } from "@/ipc/sequence";
import { isEmergencyKey } from "./emergency";
import { OutputScreen } from "./OutputScreen";

interface Applied {
  seq: number;
  state: OutputState;
}

/** La salida es "tonta": recibe snapshots por IPC y los pinta; nada más. */
export function OutputView() {
  const [applied, setApplied] = useState<Applied | null>(null);

  useEffect(() => {
    // Un snapshot se aplica solo si su secuencia es estrictamente mayor que la última (descarta viejos y repetidos).
    const apply = (next: OutputStateChangedPayload): void => {
      setApplied((current) => (isNewer(current?.seq ?? null, next.seq) ? { seq: next.seq, state: next.state } : current));
    };
    const off = ipc.subscribe("output.stateChanged", apply);

    // Estado inicial: la ventana puede cargarse después de que ocurrió el último evento. Reintenta sin rendirse
    // (si el backend tarda, la salida se queda en negro hasta que responda; el siguiente evento también la actualiza).
    const initial = createRetrier(async () => {
      apply(await ipc.request("output.getState", {}));
    });
    initial.start();

    // Respaldo del atajo global: pide a C# minimizar la salida. La salida no decide nada; solo avisa.
    const onKeyDown = (e: KeyboardEvent): void => {
      if (!isEmergencyKey(e)) return;
      e.preventDefault();
      ipc.send("output.recover", {});
    };
    window.addEventListener("keydown", onKeyDown);
    return () => {
      initial.stop();
      off();
      window.removeEventListener("keydown", onKeyDown);
    };
  }, []);

  return <OutputScreen state={applied?.state ?? null} />;
}