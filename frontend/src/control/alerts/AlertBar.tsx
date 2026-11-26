import { useEffect, useRef, useState } from "react";
import { useStore } from "zustand";
import { Button } from "@/components/ui/button";
import type { AlertMode, OutputStateChangedPayload, SavedAlertDto } from "@/ipc/protocol";
import { matchAlertShortcut } from "./alertShortcuts";
import { AlertSettingsView } from "./AlertSettings";
import type { AlertController } from "./controller";
import { ALERT_MAX_CHARS, ALERT_MAX_PINNED, DURATION_CHOICES, ageLabel, alertLiveStatus, fitMessage, formatRemaining, remainingSeconds } from "./model";

/** Reloj de 1 s SOLO mientras hay una alerta en el estado (cuenta atrás cosmética: quien la quita es el backend). Un temporizador de la vista, nada pesado. */
function useNow(active: boolean): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (!active) return;
    setNow(Date.now());
    const timer = setInterval(() => { setNow(Date.now()); }, 1000);
    return () => { clearInterval(timer); };
  }, [active]);
  return now;
}

function Row({ alert, controller, connected, now }: { alert: SavedAlertDto; controller: AlertController; connected: boolean; now: number }) {
  const loaded = useStore(controller.store, (s) => s.loadedFrom === alert.id);
  const pinnedFull = useStore(controller.store, (s) => s.pinned.length >= ALERT_MAX_PINNED);
  return (
    <li className={`flex items-center gap-2 rounded-md border p-2 ${loaded ? "border-blue-600 bg-blue-50" : ""}`} data-testid="alert-row">
      {/* Clic en el texto = CARGAR en el cuadro (no muestra nada). Mostrar exige ▶ o Enter. */}
      <button type="button" className="min-w-0 flex-1 truncate text-left" title="Cargar en el cuadro (no se muestra)" onClick={() => { controller.loadFrom(alert); }}>
        {alert.text}
      </button>
      <span className="shrink-0 text-xs text-neutral-500">{ageLabel(alert.lastUsedAt, now)}</span>
      <Button size="sm" disabled={!connected} aria-label={`Mostrar ahora: ${alert.text}`} title="Mostrar ahora" onClick={() => { controller.showSaved(alert); }}>▶</Button>
      <Button
        size="sm" variant="outline" disabled={!connected || (!alert.pinned && pinnedFull)} aria-pressed={alert.pinned}
        aria-label={alert.pinned ? `Desfijar: ${alert.text}` : `Fijar: ${alert.text}`} title={alert.pinned ? "Desfijar" : pinnedFull ? "Ya hay 30 fijadas" : "Fijar"}
        onClick={() => { controller.pin(alert, !alert.pinned); }}
      >
        {alert.pinned ? "★" : "☆"}
      </Button>
      <Button size="sm" variant="outline" disabled={!connected} aria-label={`Borrar: ${alert.text}`} title="Borrar" onClick={() => { controller.remove(alert); }}>✕</Button>
    </li>
  );
}

/**
 * Barra de alertas FIJA, visible en todas las pestañas. Escribir NO muestra nada: se muestra con ▶ o Enter. Lo que hay en pantalla (y el tiempo que le queda) se DERIVA del estado que
 * confirma el backend, no de lo que esta barra envió; por eso tras recargar el panel (Ctrl+Alt+Shift+R) se recupera sola.
 */
export function AlertBar({ controller, connected, output }: { controller: AlertController; connected: boolean; output: OutputStateChangedPayload | null }) {
  const s = useStore(controller.store);
  const status = alertLiveStatus(output);
  const [open, setOpen] = useState(false);
  const [settingsOpen, setSettingsOpen] = useState(false);
  // El reloj de 1 s SOLO corre si hay una alerta en el estado confirmado (cuenta atrás) o la lista está abierta (antigüedades): con alertas guardadas y nada a la vista no hay re-renders periódicos.
  const now = useNow(status.kind !== "none" || open);
  const inputRef = useRef<HTMLInputElement>(null);

  // Al conectar (y al reconectar tras un silencio) se leen los ajustes y la lista: el backend es la fuente de verdad.
  useEffect(() => { if (connected) void controller.init(); }, [controller, connected]);

  // Atajos configurables (Ctrl+Mayús+K / Ctrl+Mayús+H por defecto): valen en cualquier pestaña y también con el foco en un cuadro de texto.
  useEffect(() => {
    const onKeyDown = (e: KeyboardEvent): void => {
      const action = matchAlertShortcut(e, controller.store.getState().shortcuts);
      if (!action) return;
      e.preventDefault();
      if (e.repeat) return;
      if (action === "focus") controller.requestFocus();
      else controller.hide();
    };
    document.addEventListener("keydown", onKeyDown);
    return () => { document.removeEventListener("keydown", onKeyDown); };
  }, [controller]);

  useEffect(() => {
    if (s.focusTick === 0) return;
    inputRef.current?.focus();
    inputRef.current?.select();
  }, [s.focusTick]);

  const text = s.draft.trim();
  const problem = controller.showProblem();
  const tickerEnabled = s.settings?.tickerEnabled ?? true;
  const theme = s.settings?.theme ?? null;
  const suggestion = s.fit && theme && s.mode === "band" ? fitMessage(s.fit, theme) : null;
  const suggestTicker = s.fit !== null && (s.fit.state === "grows" || s.fit.state === "blocked") && s.mode === "band" && tickerEnabled;
  const remaining = status.kind === "none" ? null : remainingSeconds(status.alert, now);

  const submit = (): void => {
    if (controller.show()) inputRef.current?.blur();   // el foco vuelve a la pestaña: las flechas del servicio siguen funcionando
  };

  return (
    <section aria-label="Alertas rápidas" data-testid="alert-bar" className={`flex flex-col gap-2 rounded-lg border-2 p-3 ${status.kind === "live" ? "border-amber-500 bg-amber-50" : status.kind === "hidden" ? "border-neutral-400 bg-neutral-100" : ""}`}>
      {status.kind !== "none" && (
        <div className="flex flex-wrap items-center gap-3" data-testid="alert-status" role="status">
          <span className={`rounded-full px-3 py-1 text-sm font-bold ${status.kind === "live" ? "bg-amber-500 text-black" : "bg-neutral-500 text-white"}`}>
            {status.kind === "live" ? "ALERTA EN PANTALLA" : `ALERTA OCULTA · ${status.why}`}
          </span>
          <span className="text-sm" data-testid="alert-remaining">
            {status.alert.sticky ? "Hasta quitarla (tope 30 min)" : "Quedan"} {remaining === null ? "" : `· ${formatRemaining(remaining)}`}
          </span>
          <span className="min-w-0 flex-1 truncate text-sm text-neutral-700" data-testid="alert-onscreen-text">«{status.alert.text}»</span>
          <Button size="sm" variant="destructive" disabled={!connected} onClick={() => { controller.hide(); }}>Quitar</Button>
        </div>
      )}

      <div className="flex flex-wrap items-center gap-2">
        <label className="sr-only" htmlFor="alert-input">Aviso</label>
        <input
          id="alert-input" ref={inputRef} type="text" autoComplete="off" spellCheck
          className={`h-11 min-w-64 flex-1 rounded-md border-2 p-2 ${text.length > ALERT_MAX_CHARS ? "border-red-600" : ""}`}
          placeholder="Escribe un aviso y pulsa Enter para mostrarlo"
          value={s.draft}
          disabled={!connected}
          onChange={(e) => { controller.setDraft(e.target.value); }}
          onKeyDown={(e) => {
            if (e.key === "Enter") { e.preventDefault(); e.stopPropagation(); submit(); }
            else if (e.key === "Escape") { e.stopPropagation(); inputRef.current?.blur(); }
          }}
        />
        <span className={`text-sm tabular-nums ${text.length > ALERT_MAX_CHARS ? "font-bold text-red-700" : "text-neutral-600"}`} data-testid="alert-counter">
          {String(text.length)} / {String(ALERT_MAX_CHARS)}
        </span>
        <Button size="lg" disabled={!connected || problem !== null} title={problem ?? "Mostrar el aviso"} onClick={submit}>▶ Mostrar</Button>
        <Button size="lg" variant="outline" disabled={!connected} onClick={() => { controller.hide(); }}>Quitar</Button>
        <Button size="lg" variant="outline" aria-expanded={open} onClick={() => { setOpen(!open); }}>{open ? "▴ Alertas" : "▾ Alertas"}</Button>
      </div>

      <div className="flex flex-wrap items-center gap-4 text-sm">
        <fieldset className="flex items-center gap-3" disabled={!connected}>
          <legend className="sr-only">Modo</legend>
          {(["band", "ticker"] as const satisfies readonly AlertMode[]).map((mode) => (
            <label key={mode} className={`flex items-center gap-1 ${mode === "ticker" && !tickerEnabled ? "text-neutral-400" : "cursor-pointer"}`}>
              <input type="radio" name="alert-mode" checked={s.mode === mode} disabled={mode === "ticker" && !tickerEnabled} onChange={() => { controller.setMode(mode); }} />
              {mode === "band" ? "Cinta" : "Desplazamiento"}
            </label>
          ))}
        </fieldset>
        <label className="flex items-center gap-2">
          Duración
          <select className="h-9 rounded-md border-2 p-1" aria-label="Duración" value={s.durationSec} disabled={!connected} onChange={(e) => { controller.setDuration(Number(e.target.value)); }}>
            {DURATION_CHOICES.map((d) => <option key={d.sec} value={d.sec}>{d.label}</option>)}
          </select>
        </label>
        {!tickerEnabled && <span className="text-xs text-neutral-500">Desplazamiento desactivado en los ajustes</span>}
        {suggestion && (
          <span className={`flex items-center gap-2 ${s.fit?.state === "shrunk" ? "text-neutral-700" : "font-semibold text-amber-800"}`} data-testid="alert-fit" role="status">
            {suggestion}
            {suggestTicker && <Button size="sm" variant="outline" onClick={() => { controller.setMode("ticker"); }}>Usar desplazamiento</Button>}
          </span>
        )}
        {!suggestion && problem && text.length > 0 && <span className="text-red-700" data-testid="alert-problem">{problem}</span>}
        {status.kind === "live" && text.length > 0 && text !== status.alert.text && <span className="text-xs text-neutral-600">Mostrar reemplazará el aviso que está en pantalla.</span>}
      </div>

      {s.savedNotice && (
        <div role="alert" data-testid="alert-saved-notice" className="flex flex-wrap items-center gap-3 rounded-md border-2 border-amber-600 bg-amber-100 p-2 text-sm text-amber-950">
          <span>{s.savedNotice.message}</span>
          {s.savedNotice.canTicker && (
            <Button size="sm" disabled={!connected} onClick={() => {
              const target = [...s.pinned, ...s.recent].find((a) => a.id === s.savedNotice?.alertId);
              if (target) controller.showSavedAsTicker(target);
            }}>Mostrar con desplazamiento</Button>
          )}
          <Button size="sm" variant="outline" onClick={() => { controller.dismissSavedNotice(); }}>Cerrar aviso</Button>
        </div>
      )}

      {open && (
        <div className="flex flex-col gap-3 border-t pt-3" data-testid="alert-drawer">
          {settingsOpen ? (
            <AlertSettingsView controller={controller} connected={connected} onClose={() => { setSettingsOpen(false); }} />
          ) : (
            <>
              <div className="flex flex-wrap items-center gap-2">
                <Button size="sm" variant="outline" disabled={!connected || text.length === 0 || text.length > ALERT_MAX_CHARS} onClick={() => { controller.saveDraftPinned(); }}>☆ Fijar lo escrito</Button>
                <Button size="sm" variant="outline" onClick={() => { setSettingsOpen(true); }}>Ajustes de las alertas</Button>
                {s.listError && <span role="alert" className="text-sm text-red-700">{s.listError}</span>}
              </div>
              <div className="grid gap-4 sm:grid-cols-2">
                <div>
                  <h3 className="mb-1 text-sm font-bold">Fijadas ★ ({String(s.pinned.length)}/{String(ALERT_MAX_PINNED)})</h3>
                  {s.pinned.length === 0 ? <p className="text-sm text-neutral-500">Ninguna. Fija las que se repiten siempre.</p> : (
                    <ul className="flex flex-col gap-1" data-testid="alert-pinned">{s.pinned.map((a) => <Row key={a.id} alert={a} controller={controller} connected={connected} now={now} />)}</ul>
                  )}
                </div>
                <div>
                  <h3 className="mb-1 text-sm font-bold">Recientes (últimos 14 días)</h3>
                  {s.recent.length === 0 ? <p className="text-sm text-neutral-500">Ninguna todavía.</p> : (
                    <ul className="flex flex-col gap-1" data-testid="alert-recent">{s.recent.map((a) => <Row key={a.id} alert={a} controller={controller} connected={connected} now={now} />)}</ul>
                  )}
                </div>
              </div>
              <p className="text-xs text-neutral-500">Un clic en una fila la carga en el cuadro, sin mostrarla. Para mostrarla: ▶ de la fila, o Enter en el cuadro.</p>
            </>
          )}
        </div>
      )}
    </section>
  );
}
