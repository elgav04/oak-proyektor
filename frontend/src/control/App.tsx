import { useEffect, useState } from "react";
import { Button } from "@/components/ui/button";
import { ipc } from "@/ipc/instance";
import { MASTER_MODES, type DisplayInfo, type MasterMode, type OutputPauseReason } from "@/ipc/protocol";
import { describeOutput, type OutputNotice } from "./outputNotice";
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
  duplicateMode: "Modo Duplicar: cambia a Extender con Win+P para proyectar",
  unstable: "Salida en pausa: los monitores cambiaban sin parar",
};

function noticeTitle(notice: OutputNotice): string {
  switch (notice.kind) {
    case "paused":
      return PAUSE_MESSAGES[notice.reason];
    case "displayLost":
      return PAUSE_MESSAGES.displayLost;
    case "newDisplay":
      return "Apareció un monitor distinto al de la salida";
    case "displayBack":
      return "El proyector volvió a conectarse, pero la salida sigue en pausa";
    case "panelOnOutput":
      return "El panel está sobre el monitor de la salida. Muévelo para reanudar";
    case "duplicate":
      return PAUSE_MESSAGES.duplicateMode;
    case "none":
      return "";
  }
}

/** El número es solo una etiqueta para el operador (posición en la lista); NUNCA se envía: un monitor se identifica por su `id`. */
function displayLabel(d: DisplayInfo, position: number): string {
  const tags = [d.hostsPanel ? "panel" : null, d.isPrimary ? "principal" : null].filter(Boolean).join(", ");
  return `Monitor ${String(position + 1)} — ${String(d.width)}×${String(d.height)}${tags ? ` (${tags})` : ""}`;
}

export function App() {
  const connected = useControlStore((s) => s.connected);
  const everConnected = useControlStore((s) => s.everConnected);
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
  const outputDisplayId = output?.window.displayId ?? null;
  const autoRestore = output?.window.autoRestore ?? true;
  const displayLostPolicy = output?.window.onDisplayLost ?? "warnAndWait";
  const preferredDisplayId = output?.window.preferredDisplayId ?? null;
  // Qué aviso mostrar y si «Reanudar salida» puede funcionar (el monitor asignado sigue conectado). Solo presentación de lo que manda C#.
  const { notice, canResumeInPlace } = describeOutput(displays, output?.window ?? null);
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
            {!connected ? (everConnected ? "SIN RESPUESTA" : "Conectando…") : isPaused ? "SALIDA EN PAUSA" : isOpen ? "SALIDA ABIERTA" : "Salida cerrada"}
          </span>
        </div>
      </header>

      {!connected && everConnected && (
        <div role="alert" className="rounded-lg border-2 border-red-700 bg-red-600 p-5 text-white">
          <p className="text-xl font-bold">El panel dejó de recibir respuesta del programa</p>
          <p>
            Lo que ves puede estar desactualizado y los botones están desactivados. Reintentando; si no se recupera, pulsa «Reconectar». La ventana de salida no depende del
            panel: su estado real es el que se ve en el proyector.
          </p>
        </div>
      )}

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

      {isPaused && notice.kind !== "none" && (
        <div role="alert" className="flex flex-col gap-3 rounded-lg border-2 border-red-700 bg-red-600 p-5 text-white">
          <p className="text-xl font-bold">{noticeTitle(notice)}</p>

          {notice.kind === "displayLost" && (
            <p>{autoRestore ? "Conéctalo de nuevo: la salida se restaurará sola." : "Conéctalo de nuevo y pulsa «Reanudar salida»."}</p>
          )}

          {notice.kind === "newDisplay" && (
            <>
              <p>
                La salida está en pausa porque el monitor donde proyectaba ya no está conectado. NO se abre sola en otro monitor: si quieres
                proyectar en uno de estos, elígelo.
                {autoRestore ? " Si vuelve el mismo monitor, la salida se restaurará sola." : ""}
              </p>
              <div className="flex flex-wrap gap-3">
                {notice.alternatives.map((d) => (
                  <Button
                    key={d.id}
                    size="lg"
                    variant="outline"
                    className="h-12 text-black"
                    disabled={!connected}
                    onClick={() => { ipc.send("output.open", { displayId: d.id }); }}
                  >
                    Abrir la salida en {displayLabel(d, displays.indexOf(d))}
                  </Button>
                ))}
              </div>
            </>
          )}

          {notice.kind === "displayBack" && <p>Pulsa «Reanudar salida» para volver a proyectar en él.</p>}

          {notice.kind === "panelOnOutput" && (
            <p>
              {autoRestore
                ? "La salida se reanudará sola, a pantalla completa, en cuanto el panel salga de ese monitor. No necesitas pulsar nada."
                : "Cuando el panel salga de ese monitor, pulsa «Reanudar salida» (la reanudación automática está desactivada)."}
            </p>
          )}

          {notice.kind === "duplicate" && (
            <p>
              Con «Duplicar» hay un solo monitor y la salida taparía el panel. Pulsa Win+P y elige «Extender»: la salida se reanudará sola.
            </p>
          )}

          {notice.kind === "paused" && notice.reason === "unstable" && (
            <p>
              La salida pasaba de pausa a reanudación una y otra vez (cable flojo, proyector que se reinicia o un monitor que parpadea). Se quedó en pausa por seguridad:
              revisa los monitores y pulsa «Reanudar salida».
            </p>
          )}

          {canResumeInPlace && (
            <Button
              size="lg"
              variant="outline"
              className="h-12 w-fit text-black"
              disabled={!connected}
              onClick={() => { ipc.send("output.open", { displayId: outputDisplayId }); }}
            >
              Reanudar salida
            </Button>
          )}
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
                {preferredDisplayId === d.id && <span className="ml-auto text-sm text-neutral-600">preferido</span>}
                {isOpen && outputDisplayId === d.id && <span className={`${preferredDisplayId === d.id ? "" : "ml-auto "}text-sm font-semibold text-green-700`}>salida aquí</span>}
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
          {isPaused ? (
            <>
              {/* «Reanudar» vuelve al monitor que ya tenía la salida (nunca a otro); abrir en otro monitor es una acción distinta y explícita. */}
              <Button size="lg" disabled={!connected || !canResumeInPlace} onClick={() => { ipc.send("output.open", { displayId: outputDisplayId }); }}>
                Reanudar salida
              </Button>
              <Button size="lg" variant="outline" disabled={!connected} onClick={() => { ipc.send("output.open", { displayId: selected }); }}>
                Abrir en el monitor elegido
              </Button>
            </>
          ) : (
            <Button size="lg" disabled={!connected} onClick={() => { ipc.send("output.open", { displayId: selected }); }}>
              {isOpen ? "Mover / reabrir salida" : "Abrir salida"}
            </Button>
          )}
          <Button size="lg" variant="outline" disabled={!connected || !isOpen} onClick={() => { ipc.send("output.close", {}); }}>
            Cerrar salida
          </Button>
          <Button size="lg" variant="destructive" disabled={!connected || !isOpen || isPaused} onClick={() => { ipc.send("output.recover", { reason: "manual" }); }}>
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
          <span>Reanudar la salida automáticamente (cuando el proyector vuelva a conectarse, el panel salga de su monitor o se vuelva a «Extender»)</span>
        </label>
        <fieldset className="flex flex-col gap-2 rounded-md border p-3" disabled={!connected}>
          <legend className="px-1 text-sm font-semibold">Si el monitor de la salida desaparece</legend>
          <label className="flex cursor-pointer items-center gap-3 text-sm">
            <input
              type="radio"
              name="display-lost-policy"
              checked={displayLostPolicy === "warnAndWait"}
              onChange={() => { ipc.send("settings.setDisplayLostPolicy", { policy: "warnAndWait" }); }}
            />
            <span>Avisar y esperar (por defecto): la salida se pausa y te ofrece un botón por cada monitor disponible</span>
          </label>
          <label className="flex cursor-pointer items-center gap-3 text-sm">
            <input
              type="radio"
              name="display-lost-policy"
              checked={displayLostPolicy === "moveToBest"}
              onChange={() => { ipc.send("settings.setDisplayLostPolicy", { policy: "moveToBest" }); }}
            />
            <span>Mover automáticamente al mejor monitor sin el panel (el último usado; si no, uno no principal; si hay empate, el más a la derecha)</span>
          </label>
          <p className="text-xs text-neutral-600">
            El monitor preferido es el último que elegiste al abrir la salida; la salida siempre intenta volver a él.
          </p>
        </fieldset>
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