import { create } from "zustand";
import { ipc } from "@/ipc/instance";
import type { DisplayInfo, OutputStateChangedPayload } from "@/ipc/protocol";

/** Réplica del estado que manda C#. No contiene estado de negocio propio. */
interface ControlState {
  connected: boolean;
  displays: DisplayInfo[];
  output: OutputStateChangedPayload | null;
}

export const useControlStore = create<ControlState>(() => ({ connected: false, displays: [], output: null }));

function applyOutput(payload: OutputStateChangedPayload): void {
  // Descarta snapshots más viejos que el que ya tenemos (la revisión solo crece).
  const current = useControlStore.getState().output;
  if (current && payload.state.revision < current.state.revision) return;
  useControlStore.setState({ output: payload, connected: true });
}

/** Se suscribe a los eventos y pide el estado inicial. Devuelve la función para cancelar. */
export function connectControlStore(): () => void {
  const offs = [
    ipc.subscribe("display.changed", ({ displays }) => {
      useControlStore.setState({ displays });
    }),
    ipc.subscribe("output.stateChanged", applyOutput),
  ];

  const load = async (): Promise<void> => {
    const [displays, output] = await Promise.all([ipc.request("display.list", {}), ipc.request("output.getState", {})]);
    useControlStore.setState({ displays: displays.displays });
    applyOutput(output);
  };
  // El backend puede tardar un instante en estar listo: reintenta unas veces antes de rendirse.
  void (async () => {
    for (let attempt = 0; attempt < 5; attempt++) {
      try {
        await load();
        return;
      } catch (error) {
        console.warn("[control] no se pudo cargar el estado inicial; reintentando", error);
      }
    }
  })();

  return () => {
    offs.forEach((off) => {
      off();
    });
  };
}
