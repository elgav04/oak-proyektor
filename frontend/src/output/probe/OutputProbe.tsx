import { useState } from "react";
import type { BackgroundLayer, OutputState, Slide, TextTheme } from "@/ipc/protocol";
import "../output.css";
import { OutputScreen } from "../OutputScreen";

/**
 * Componente de prueba MANUAL del ErrorBoundary de la salida (M13). Solo existe en `npm run dev`:
 * abre http://localhost:5173/output-probe.html. No forma parte de `npm run build` (no es una entrada de Vite).
 *
 * Esperado: «Snapshot que ROMPE» NO deja el recuadro en negro; conserva lo último pintado.
 * Un snapshot bueno posterior lo recupera.
 */
const THEME: TextTheme = {
  fontFamily: "Arial, sans-serif", fontSize: 72, color: "#FFFFFF", outlineWidth: 0, outlineColor: "#000000", shadowBlur: 8, shadowOffset: 3, shadowColor: "#000000",
  align: "center", verticalAlign: "middle", lineHeight: 1.25, marginTop: 80, marginRight: 120, marginBottom: 80, marginLeft: 120, autoFit: true, minFontSize: 28,
};
const bg = (color: string): BackgroundLayer => ({ kind: "color", color, mediaUrl: null, fileName: null, muted: true, transitionMs: 300, warning: null, retry: 0 });
const slide = (id: string, text: unknown): Slide => ({ id, blocks: [{ text: text as string }], theme: THEME, origin: null });

function state(master: OutputState["master"], color: string, s: Slide | null, revision: number): OutputState {
  return { schemaVersion: 3, revision, master, background: bg(color), content: { slide: s, transitionMs: 400 }, overlays: { alert: null } };
}

export function OutputProbe() {
  const [revision, setRevision] = useState(0);
  const [current, setCurrent] = useState<OutputState | null>(null);

  const push = (next: (revision: number) => OutputState): void => {
    const nextRevision = revision + 1;
    setRevision(nextRevision);
    setCurrent(next(nextRevision));
  };

  return (
    <main className="mx-auto flex max-w-3xl flex-col gap-4 p-6" style={{ fontFamily: "system-ui, sans-serif" }}>
      <h1>Prueba del ErrorBoundary de la salida</h1>
      <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
        <button onClick={() => { push((r) => state("live", "#1e3a8a", slide(`s${String(r)}`, "Texto de prueba"), r)); }}>Snapshot bueno 1 (Live)</button>
        <button onClick={() => { push((r) => state("logo", "#111827", null, r)); }}>Snapshot bueno 2 (Logo)</button>
        {/* El texto de un bloque llega como objeto: React lanza «Objects are not valid as a React child» al renderizarlo. */}
        <button data-testid="break" onClick={() => { push((r) => state("live", "#7f1d1d", slide(`s${String(r)}`, { boom: true }), r)); }}>Snapshot que ROMPE el render</button>
      </div>
      <p>Revisión actual: {revision}. Esperado: tras «ROMPE» el recuadro conserva lo último pintado (no negro).</p>
      <div style={{ width: "100%", aspectRatio: "16 / 9", border: "2px solid #444", position: "relative", overflow: "hidden" }}>
        <div style={{ position: "absolute", inset: 0 }}>
          {/* Igual que la salida real: el boundary se reinicia con la secuencia (aquí, el contador) y NO con `state.revision`. */}
          <OutputScreen state={current} seq={current === null ? null : revision} />
        </div>
      </div>
    </main>
  );
}
