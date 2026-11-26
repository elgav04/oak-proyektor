import { useState } from "react";
import { useStore } from "zustand";
import { Button } from "@/components/ui/button";
import type { AlertMode, AlertPosition, AlertSettingsPayload, AlertTheme, TextAlign } from "@/ipc/protocol";
import { alertBandStyle, alertTextStyle } from "@/output/alert";
import { ALERT_SHORTCUT_LABELS, comboOfEvent, type AlertShortcutAction } from "./alertShortcuts";
import type { AlertController } from "./controller";
import { DURATION_CHOICES, HEX, alertMinFontSize, alertSettingsProblem, themeWithAlertFontSize } from "./model";

const FONTS = ["Arial", "Segoe UI", "Calibri", "Georgia", "Times New Roman", "Verdana", "Tahoma", "Trebuchet MS"];
const SAMPLE = "AVISO DE PRUEBA: texto de ejemplo para ver el tema";

const num = (value: string, fallback: number): number => {
  const n = Number(value);
  return Number.isFinite(n) ? n : fallback;
};

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return <label className="flex flex-col gap-1 text-sm">{label}{children}</label>;
}

/** Descompone `#RRGGBB[AA]` en color y opacidad (0–100). */
function splitBand(hex: string): { rgb: string; opacity: number } {
  if (!HEX.test(hex)) return { rgb: "#000000", opacity: 85 };
  return { rgb: hex.slice(0, 7), opacity: hex.length === 9 ? Math.round((parseInt(hex.slice(7, 9), 16) / 255) * 100) : 100 };
}
const joinBand = (rgb: string, opacity: number): string => `${rgb.toUpperCase()}${Math.round((Math.min(100, Math.max(0, opacity)) / 100) * 255).toString(16).padStart(2, "0").toUpperCase()}`;

/** Una combinación de teclas «grabada»: el campo escucha la pulsación y la propone al controlador, que la valida (Ctrl+Mayús+letra, sin chocar con nada). */
function ShortcutField({ action, controller }: { action: AlertShortcutAction; controller: AlertController }) {
  const combo = useStore(controller.store, (s) => s.shortcuts[action]);
  return (
    <Field label={ALERT_SHORTCUT_LABELS[action]}>
      <input
        className="h-10 rounded-md border-2 p-2 font-mono"
        aria-label={`Atajo: ${ALERT_SHORTCUT_LABELS[action]}`}
        readOnly
        value={combo}
        title="Pulsa aquí la combinación nueva (Ctrl+Mayús+letra)"
        onKeyDown={(e) => {
          if (e.key === "Tab") return;
          e.preventDefault();
          e.stopPropagation();
          const pressed = comboOfEvent(e.nativeEvent);
          if (pressed) controller.setShortcut(action, pressed);
        }}
      />
    </Field>
  );
}

/**
 * Ajustes de las alertas: modo y duración por defecto, interruptor del desplazamiento, tema propio (fuente, color, fondo de la cinta, contorno, posición y altura) y atajos.
 * Se guardan en settings.json por el backend, que valida por bloques; aquí solo se evita mandar lo evidentemente inválido y el mínimo del ajuste es SIEMPRE menor que el tamaño.
 */
export function AlertSettingsView({ controller, connected, onClose }: { controller: AlertController; connected: boolean; onClose: () => void }) {
  const s = useStore(controller.store);
  const [draft, setDraft] = useState<AlertSettingsPayload | null>(s.settings);
  if (!draft) {
    return (
      <div className="flex flex-col gap-2">
        <p>Los ajustes de las alertas aún no se han cargado.</p>
        <Button variant="outline" onClick={onClose}>Volver</Button>
      </div>
    );
  }
  const theme = draft.theme;
  const setTheme = (changes: Partial<AlertTheme>): void => { setDraft({ ...draft, theme: { ...theme, ...changes } }); };
  const effectiveMin = alertMinFontSize(theme.fontSize, theme.minFontSize);
  const problem = alertSettingsProblem({ ...draft, theme: { ...theme, minFontSize: effectiveMin } }) ?? s.settingsError;
  const band = splitBand(theme.bandColor);

  return (
    <div className="flex flex-col gap-4" data-testid="alert-settings">
      <div className="flex items-center justify-between">
        <h3 className="text-lg font-bold">Ajustes de las alertas</h3>
        <Button variant="outline" onClick={onClose}>Cerrar los ajustes</Button>
      </div>

      <div className="relative overflow-hidden rounded-md border bg-neutral-700" style={{ width: 480, height: 270 }} aria-hidden="true">
        <div style={{ position: "absolute", left: 0, top: 0, width: 1920, height: 1080, transform: "scale(0.25)", transformOrigin: "0 0" }}>
          <div style={alertBandStyle(theme)}><div style={alertTextStyle(theme, theme.fontSize)}>{SAMPLE}</div></div>
        </div>
      </div>

      <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
        <Field label="Modo por defecto">
          <select className="h-10 rounded-md border-2 p-2" aria-label="Modo por defecto" value={draft.mode} onChange={(e) => { setDraft({ ...draft, mode: e.target.value as AlertMode }); }}>
            <option value="band">Cinta estática</option>
            <option value="ticker" disabled={!draft.tickerEnabled}>Desplazamiento</option>
          </select>
        </Field>
        <Field label="Duración por defecto">
          <select className="h-10 rounded-md border-2 p-2" aria-label="Duración por defecto" value={draft.durationSec} onChange={(e) => { setDraft({ ...draft, durationSec: Number(e.target.value) }); }}>
            {DURATION_CHOICES.map((d) => <option key={d.sec} value={d.sec}>{d.label}</option>)}
          </select>
        </Field>
        <label className="col-span-2 flex cursor-pointer items-center gap-3 text-sm">
          <input
            type="checkbox" aria-label="Permitir el desplazamiento" checked={draft.tickerEnabled}
            onChange={(e) => { setDraft({ ...draft, tickerEnabled: e.target.checked, mode: e.target.checked ? draft.mode : "band" }); }}
          />
          <span>Permitir el desplazamiento (ticker). Si en el proyector real baja la fluidez del video, desactívalo: solo quedará la cinta estática.</span>
        </label>

        <Field label="Fuente">
          <select className="h-10 rounded-md border-2 p-2" aria-label="Fuente" value={FONTS.includes(theme.fontFamily) ? theme.fontFamily : "Arial"} onChange={(e) => { setTheme({ fontFamily: e.target.value }); }}>
            {FONTS.map((f) => <option key={f} value={f}>{f}</option>)}
          </select>
        </Field>
        <Field label="Tamaño (16–200)">
          <input className="h-10 rounded-md border-2 p-2" type="number" min={16} max={200} aria-label="Tamaño" value={theme.fontSize}
            onChange={(e) => { setDraft({ ...draft, theme: themeWithAlertFontSize(theme, num(e.target.value, theme.fontSize)) }); }} />
        </Field>
        <Field label="Color del texto">
          <input className="h-10 w-full rounded-md border-2" type="color" aria-label="Color del texto" value={theme.color.slice(0, 7)} onChange={(e) => { setTheme({ color: e.target.value.toUpperCase() }); }} />
        </Field>
        <Field label="Alineación">
          <select className="h-10 rounded-md border-2 p-2" aria-label="Alineación" value={theme.align} onChange={(e) => { setTheme({ align: e.target.value as TextAlign }); }}>
            <option value="left">Izquierda</option><option value="center">Centro</option><option value="right">Derecha</option>
          </select>
        </Field>
        <Field label="Color de la cinta">
          <input className="h-10 w-full rounded-md border-2" type="color" aria-label="Color de la cinta" value={band.rgb} onChange={(e) => { setTheme({ bandColor: joinBand(e.target.value, band.opacity) }); }} />
        </Field>
        <Field label="Opacidad de la cinta (%)">
          <input className="h-10 rounded-md border-2 p-2" type="number" min={0} max={100} aria-label="Opacidad de la cinta" value={band.opacity} onChange={(e) => { setTheme({ bandColor: joinBand(band.rgb, num(e.target.value, band.opacity)) }); }} />
        </Field>
        <Field label="Contorno (grosor 0–20)">
          <input className="h-10 rounded-md border-2 p-2" type="number" min={0} max={20} aria-label="Contorno" value={theme.outlineWidth} onChange={(e) => { setTheme({ outlineWidth: num(e.target.value, theme.outlineWidth) }); }} />
        </Field>
        <Field label="Color del contorno">
          <input className="h-10 w-full rounded-md border-2" type="color" aria-label="Color del contorno" value={theme.outlineColor.slice(0, 7)} onChange={(e) => { setTheme({ outlineColor: e.target.value.toUpperCase() }); }} />
        </Field>
        <Field label="Posición">
          <select className="h-10 rounded-md border-2 p-2" aria-label="Posición" value={theme.position} onChange={(e) => { setTheme({ position: e.target.value as AlertPosition }); }}>
            <option value="bottom">Abajo</option><option value="top">Arriba</option>
          </select>
        </Field>
        <Field label="Altura: renglones (1–3)">
          <input className="h-10 rounded-md border-2 p-2" type="number" min={1} max={3} aria-label="Renglones" value={theme.maxLines} onChange={(e) => { setTheme({ maxLines: num(e.target.value, theme.maxLines) }); }} />
        </Field>
        <Field label="Altura: relleno vertical (0–120)">
          <input className="h-10 rounded-md border-2 p-2" type="number" min={0} max={120} aria-label="Relleno vertical" value={theme.paddingY} onChange={(e) => { setTheme({ paddingY: num(e.target.value, theme.paddingY) }); }} />
        </Field>
        <Field label="Velocidad del desplazamiento (px/s)">
          <input className="h-10 rounded-md border-2 p-2" type="number" min={60} max={400} aria-label="Velocidad" value={theme.tickerSpeed} onChange={(e) => { setTheme({ tickerSpeed: num(e.target.value, theme.tickerSpeed) }); }} />
        </Field>
        <label className="col-span-2 flex cursor-pointer items-center gap-3 text-sm">
          <input type="checkbox" aria-label="Ajuste automático" checked={theme.autoFit} onChange={(e) => { setTheme({ autoFit: e.target.checked }); }} />
          <span>Ajuste automático de la letra (baja el tamaño antes de crecer la cinta)</span>
        </label>
      </div>
      <p className="text-sm text-neutral-600" data-testid="alert-min-font">
        El ajuste automático baja la letra hasta <strong>{String(effectiveMin)} px</strong> como mucho (siempre menos que el tamaño, para que pueda reducir).
      </p>

      <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
        <ShortcutField action="focus" controller={controller} />
        <ShortcutField action="hide" controller={controller} />
        <div className="col-span-2 flex items-end gap-3">
          <Button variant="outline" onClick={() => { controller.resetShortcuts(); }}>Restablecer atajos</Button>
          {s.shortcutError && <span role="alert" className="text-sm text-red-700">{s.shortcutError}</span>}
        </div>
      </div>

      <div className="flex items-center gap-3">
        <Button size="lg" disabled={!connected || problem !== null} onClick={() => { controller.saveSettings({ ...draft, theme: { ...theme, minFontSize: effectiveMin } }); }}>
          Guardar los ajustes
        </Button>
        {problem && <span role="alert" className="text-sm text-red-700">{problem}</span>}
      </div>
    </div>
  );
}
