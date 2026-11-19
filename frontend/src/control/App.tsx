import { useEffect, useState } from "react";
import { Button } from "@/components/ui/button";
import { ipc } from "@/ipc/instance";
import { MASTER_MODES, type DisplayInfo, type MasterMode, type OutputPauseReason } from "@/ipc/protocol";
import { connectControlStore, dismissFailure, reconnectControl, useControlStore } from "./store";

const MASTER_LABELS: Record<MasterMode, string> = {
  live: "Live",
  clearText: "Clear",
  black: "Black",
  logo: "Logo",
  freeze: "Freeze",
};

const PAUSE_MESSAGES: Record<OutputPauseReason, string> = {
  displayLost: "Proyector desconectado: salida en pausa",
  panelCrossed: "La salida invadió el monitor del panel: salida en pausa",
  manual: "Salida en pausa (recuperación manual)",
  emergency: "Salida en pausa (atajo de emergencia)",
};

/** El número es solo una etiqueta para el operador (posición en la lista); NUNCA se envía: un monitor se identifica por su `id`. */
function displayLabel(d: DisplayInfo, position: number): string {
  const tags = [d.hostsPanel ? "panel" : null, d.isPrimary ? "principal" : null].filter(Boolean).join(", ");
  return `Monitor ${String(position + 1)} — ${String(d.width)}×${String(d.height)}${tags ? ` (${tags})` : ""}`;
}

export function App() {
  const connected = useControlStore((s) => s.connected);
  const displays = useControlStore((s) => s.displays);
  const output = useControlStore((s) => s.output);
  const failure = useControlStore((s) => s.failure);
  const database = useControlStore((s) => s.database);
  // Elección del operador antes de abrir: ID del monitor, o null = automático. Es estado de la vista, no de negocio.
  const [selected, setSelected] = useState<string | null>(null);

  useEffect(() => {
    const connection = connectControlStore();
    return () => {
      connection.dispose();
    };
  }, []);

  const isOpen = output?.window.isOpen ?? false;
  const isPaused = output?.window.isPaused ?? false;
  const pauseReason = output?.window.pauseReason ?? null;
  const outputDisplayId = output?.window.displayId ?? null;
  const master = output?.state.master;

  return (
    <main className="mx-auto flex max-w-3xl flex-col gap-8 p-6">
      <header className="flex items-center justify-between gap-3">
        <h1 className="text-2xl font-bold">Oak-Proyektor</h1>
        <div className="flex items-center gap-3">
          {!connected && (
            <Button variant="outline" onClick={reconnectControl}>
              Reconectar
            </Button>
          )}
          {connected && database?.state === "initializing" && <span className="text-sm text-neutral-500">Preparando la base de datos…</span>}
          <span
            className={`rounded-full px-3 py-1 text-sm font-semibold ${isPaused ? "bg-red-600 text-white" : isOpen ? "bg-green-600 text-white" : "bg-neutral-200 text-neutral-700"}`}
          >
            {!connected ? "Conectando…" : isPaused ? "SALIDA EN PAUSA" : isOpen ? "SALIDA ABIERTA" : "Salida cerrada"}
          </span>
        </div>
      </header>

      {database?.state === "failed" && (
        <div role="alert" className="flex flex-col gap-2 rounded-lg border-2 border-red-700 bg-red-600 p-5 text-white">
          <p className="text-xl font-bold">No se pudo abrir la base de datos</p>
          <p>{database.message ?? "Error desconocido."}</p>
          <p>Las canciones, biblias y servicios no estarán disponibles. La proyección de la salida no depende de la base de datos.</p>
          {database.backupPath && <p className="text-sm">Tus datos originales están a salvo en la copia de seguridad: {database.backupPath}</p>}
        </div>
      )}

      {failure && (
        <div role="alert" className="flex items-start justify-between gap-4 rounded-lg border-2 border-amber-600 bg-amber-100 p-4 text-amber-950">
          <p>
            <span className="font-bold">No se pudo completar «{failure.commandType}»: </span>
            {failure.message}
          </p>
          <Button variant="outline" className="shrink-0" onClick={dismissFailure}>
            Cerrar aviso
          </Button>
        </div>
      )}

      {isPaused && (
        <div role="alert" className="flex items-center justify-between gap-4 rounded-lg border-2 border-red-700 bg-red-600 p-5 text-white">
          <p className="text-xl font-bold">{pauseReason ? PAUSE_MESSAGES[pauseReason] : "Salida en pausa"}</p>
          <Button size="lg" variant="outline" className="h-12 shrink-0 text-black" onClick={() => { ipc.send("output.open", { displayId: outputDisplayId }); }}>
            Reanudar salida
          </Button>
        </div>
      )}

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
          {displays.map((d, position) => (
            <li key={d.id}>
              <label className="flex cursor-pointer items-center gap-3 rounded-md border p-3">
                <input type="radio" name="display" checked={selected === d.id} onChange={() => { setSelected(d.id); }} />
                <span>{displayLabel(d, position)}</span>
                {isOpen && outputDisplayId === d.id && <span className="ml-auto text-sm font-semibold text-green-700">salida aquí</span>}
              </label>
            </li>
          ))}
        </ul>
        {isOpen && output?.window.isWindowed && (
          <p className="text-sm text-amber-700">
            La salida se abrió como ventana normal: solo hay un monitor o el elegido es el del panel, y no se proyecta a pantalla completa sobre el panel.
          </p>
        )}
        <div className="flex flex-wrap gap-3">
          <Button size="lg" disabled={!connected} onClick={() => { ipc.send("output.open", { displayId: selected }); }}>
            {isPaused ? "Reanudar salida" : isOpen ? "Mover / reabrir salida" : "Abrir salida"}
          </Button>
          <Button size="lg" variant="outline" disabled={!connected || !isOpen} onClick={() => { ipc.send("output.close", {}); }}>
            Cerrar salida
          </Button>
          <Button size="lg" variant="destructive" disabled={!connected || !isOpen || isPaused} onClick={() => { ipc.send("output.recover", {}); }}>
            Recuperar control
          </Button>
        </div>
        <label className="flex cursor-pointer items-center gap-3 text-sm">
          <input
            type="checkbox"
            checked={output?.window.autoRestore ?? true}
            disabled={!connected}
            onChange={(e) => { ipc.send("settings.setAutoRestore", { enabled: e.target.checked }); }}
          />
          <span>Restaurar la salida automáticamente cuando el proyector vuelva a conectarse</span>
        </label>
        <p className="text-sm text-neutral-600">
          {output?.window.emergencyHotkey
            ? `Atajo de emergencia (minimiza la salida desde cualquier ventana): ${output.window.emergencyHotkey}`
            : connected && "Atajo de emergencia global no disponible (otra aplicación usa esa combinación). Dentro de la ventana de salida: Ctrl+Alt+Shift+M."}
        </p>
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