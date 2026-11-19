import { useState } from "react";
import type { OutputState } from "@/ipc/protocol";
import "../output.css";
import { OutputScreen } from "../OutputScreen";

/**
 * Componente de prueba MANUAL del ErrorBoundary de la salida (M13). Solo existe en `npm run dev`:
 * abre http://localhost:5173/output-probe.html. No forma parte de `npm run build` (no es una entrada de Vite).
 *
 * Esperado: «Snapshot que ROMPE» NO deja el recuadro en negro; conserva lo último pintado.
 * Un snapshot bueno posterior lo recupera.
 */
const GOOD: Record<string, Omit<OutputState, "revision">> = {
  live: { master: "live", backgroundColor: "#1e3a8a", text: "Texto de prueba" },
  logo: { master: "logo", backgroundColor: "#111827", text: "LOGO" },
};

export function OutputProbe() {
  const [revision, setRevision] = useState(0);
  const [state, setState] = useState<OutputState | null>(null);

  const push = (next: Omit<OutputState, "revision"> | { master: "live"; backgroundColor: string; text: unknown }): void => {
    const nextRevision = revision + 1;
    setRevision(nextRevision);
    // El snapshot que rompe lleva `text` como objeto: React lanza «Objects are not valid as a React child» al renderizarlo.
    setState({ ...next, revision: nextRevision } as unknown as OutputState);
  };

  return (
    <main className="mx-auto flex max-w-3xl flex-col gap-4 p-6" style={{ fontFamily: "system-ui, sans-serif" }}>
      <h1>Prueba del ErrorBoundary de la salida</h1>
      <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
        <button onClick={() => { push(GOOD.live ?? { master: "live", backgroundColor: "#000", text: null }); }}>Snapshot bueno 1 (Live)</button>
        <button onClick={() => { push(GOOD.logo ?? { master: "live", backgroundColor: "#000", text: null }); }}>Snapshot bueno 2 (Logo)</button>
        <button data-testid="break" onClick={() => { push({ master: "live", backgroundColor: "#7f1d1d", text: { boom: true } }); }}>Snapshot que ROMPE el render</button>
      </div>
      <p>Revisión actual: {revision}. Esperado: tras «ROMPE» el recuadro conserva lo último pintado (no negro).</p>
      <div style={{ width: "100%", aspectRatio: "16 / 9", border: "2px solid #444", position: "relative", overflow: "hidden" }}>
        <div style={{ position: "absolute", inset: 0 }}>
          <OutputScreen state={state} />
        </div>
      </div>
    </main>
  );
}
