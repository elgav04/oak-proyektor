import { useEffect, useState } from "react";
import { Button } from "@/components/ui/button";
import { ipc } from "@/ipc/instance";
import { MASTER_MODES, type DisplayInfo, type MasterMode } from "@/ipc/protocol";
import { connectControlStore, useControlStore } from "./store";

const MASTER_LABELS: Record<MasterMode, string> = {
  live: "Live",
  clearText: "Clear",
  black: "Black",
  logo: "Logo",
  freeze: "Freeze",
};

function displayLabel(d: DisplayInfo): string {
  const tags = [d.hostsPanel ? "panel" : null, d.isPrimary ? "principal" : null].filter(Boolean).join(", ");
  return `Monitor ${String(d.index + 1)} — ${String(d.width)}×${String(d.height)}${tags ? ` (${tags})` : ""}`;
}

export function App() {
  const connected = useControlStore((s) => s.connected);
  const displays = useControlStore((s) => s.displays);
  const output = useControlStore((s) => s.output);
  // Elección del operador antes de abrir (null = automático). Es estado de la vista, no de negocio.
  const [selected, setSelected] = useState<number | null>(null);

  useEffect(() => connectControlStore(), []);

  const isOpen = output?.window.isOpen ?? false;
  const master = output?.state.master;
  const outputDisplayMissing = isOpen && output?.window.displayIndex != null && !displays.some((d) => d.index === output.window.displayIndex);

  return (
    <main className="mx-auto flex max-w-3xl flex-col gap-8 p-6">
      <header className="flex items-center justify-between">
        <h1 className="text-2xl font-bold">Oak-Proyektor</h1>
        <span className={`rounded-full px-3 py-1 text-sm font-semibold ${isOpen ? "bg-green-600 text-white" : "bg-neutral-200 text-neutral-700"}`}>
          {!connected ? "Conectando…" : isOpen ? "SALIDA ABIERTA" : "Salida cerrada"}
        </span>
      </header>

      <section aria-labelledby="displays-title" className="flex flex-col gap-3">
        <h2 id="displays-title" className="text-lg font-semibold">Monitores</h2>
        {displays.length === 0 && <p className="text-neutral-500">Sin información de monitores.</p>}
        <ul className="flex flex-col gap-2">
          <li>
            <label className="flex cursor-pointer items-center gap-3 rounded-md border p-3">
              <input type="radio" name="display" checked={selected === null} onChange={() => { setSelected(null); }} />
              <span>Automático (primer monitor que no sea el del panel)</span>
            </label>
          </li>
          {displays.map((d) => (
            <li key={d.index}>
              <label className="flex cursor-pointer items-center gap-3 rounded-md border p-3">
                <input type="radio" name="display" checked={selected === d.index} onChange={() => { setSelected(d.index); }} />
                <span>{displayLabel(d)}</span>
                {isOpen && output?.window.displayIndex === d.index && <span className="ml-auto text-sm font-semibold text-green-700">salida aquí</span>}
              </label>
            </li>
          ))}
        </ul>
        {isOpen && output?.window.isWindowed && (
          <p className="text-sm text-amber-700">
            La salida se abrió como ventana normal: solo hay un monitor o el elegido es el del panel, y no se proyecta a pantalla completa sobre el panel.
          </p>
        )}
        {outputDisplayMissing && <p className="text-sm font-semibold text-red-700">El monitor de la salida ya no está conectado.</p>}
        <div className="flex gap-3">
          <Button size="lg" disabled={!connected} onClick={() => { ipc.send("output.open", { displayIndex: selected }); }}>
            {isOpen ? "Mover / reabrir salida" : "Abrir salida"}
          </Button>
          <Button size="lg" variant="outline" disabled={!connected || !isOpen} onClick={() => { ipc.send("output.close", {}); }}>
            Cerrar salida
          </Button>
        </div>
      </section>

      <section aria-labelledby="master-title" className="flex flex-col gap-3">
        <h2 id="master-title" className="text-lg font-semibold">Controles maestros</h2>
        <div className="grid grid-cols-5 gap-3">
          {MASTER_MODES.map((mode) => (
            <Button
              key={mode}
              size="lg"
              className="h-16 text-base"
              variant={master === mode ? "default" : "outline"}
              disabled={!connected}
              aria-pressed={master === mode}
              onClick={() => { ipc.send("output.setMaster", { master: mode }); }}
            >
              {MASTER_LABELS[mode]}
            </Button>
          ))}
        </div>
        {output && (
          <div className="mt-2 flex h-32 items-center justify-center rounded-md border text-2xl font-semibold text-white" style={{ backgroundColor: output.state.backgroundColor }}>
            {output.state.text ?? ""}
          </div>
        )}
      </section>
    </main>
  );
}
