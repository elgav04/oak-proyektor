import { useState } from "react";
import { useStore } from "zustand";
import { Button } from "@/components/ui/button";
import type { SongAtEnd, SongSettingsPayload, TextAlign, TextTheme, VerticalAlign } from "@/ipc/protocol";
import { projectionTheme, safeMinFontSize, themeWithFontSize } from "../bible/slide";
import type { SongController } from "./controller";
import { SONG_DEFAULT_THEME } from "./defaults";
import { SlideCanvas } from "./SlideStrip";

const HEX = /^#[0-9a-fA-F]{6}([0-9a-fA-F]{2})?$/;
const FONTS = ["Arial", "Segoe UI", "Calibri", "Georgia", "Times New Roman", "Verdana", "Tahoma", "Trebuchet MS"];
const SAMPLE = "Primera línea de ejemplo del tema\nSegunda línea, con coma, algo más larga\nTercera línea inventada";

const num = (value: string, fallback: number): number => {
  const n = Number(value);
  return Number.isFinite(n) ? n : fallback;
};

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return <label className="flex flex-col gap-1 text-sm">{label}{children}</label>;
}

/** Validación de la pantalla (la del backend es la que manda; esto evita mandar lo evidentemente inválido). Devuelve el motivo o null. */
export function themeProblem(draft: SongSettingsPayload): string | null {
  const t = draft.theme;
  if (![t.color, t.outlineColor, t.shadowColor].every((c) => HEX.test(c))) return "Los colores deben ser #RRGGBB o #RRGGBBAA.";
  if (!(t.fontSize >= 8 && t.fontSize <= 400)) return "El tamaño de la letra debe estar entre 8 y 400.";
  if (!(t.lineHeight >= 0.8 && t.lineHeight <= 3)) return "El interlineado debe estar entre 0,8 y 3.";
  if (!(draft.transitionMs >= 0 && draft.transitionMs <= 5000)) return "El crossfade debe estar entre 0 y 5000 ms.";
  return null;
}

/** Tema propio de las canciones (fuente, tamaño, color, contorno, sombra, posición y márgenes) y su crossfade. Se guarda en settings.json por el backend, que lo valida. */
export function SongThemeView({ controller, connected }: { controller: SongController; connected: boolean }) {
  const s = useStore(controller.store);
  const [draft, setDraft] = useState<SongSettingsPayload | null>(s.settings);
  if (!draft) {
    return (
      <div className="flex flex-col gap-3">
        <p>La configuración aún no se ha cargado.</p>
        <Button variant="outline" onClick={controller.closeTheme}>Volver</Button>
      </div>
    );
  }
  const theme = draft.theme;
  const setTheme = (changes: Partial<TextTheme>): void => { setDraft({ ...draft, theme: { ...theme, ...changes } }); };
  const problem = themeProblem(draft);
  const effectiveMin = safeMinFontSize(theme.fontSize, theme.minFontSize);

  return (
    <div className="flex flex-col gap-5">
      <div className="flex items-center justify-between">
        <h2 className="text-xl font-bold">Tema de las canciones</h2>
        <Button variant="outline" onClick={controller.closeTheme}>Volver a las canciones</Button>
      </div>
      <p className="text-sm text-neutral-600">Medidas en píxeles del lienzo de 1920×1080. El texto se reparte en diapositivas para que quepa a este tamaño; nunca se recorta.</p>
      <SlideCanvas theme={projectionTheme(theme)} text={SAMPLE} width={480} />

      <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
        <Field label="Fuente">
          <select className="h-10 rounded-md border-2 p-2" aria-label="Fuente" value={FONTS.includes(theme.fontFamily) ? theme.fontFamily : "Arial"} onChange={(e) => { setTheme({ fontFamily: e.target.value }); }}>
            {FONTS.map((f) => <option key={f} value={f}>{f}</option>)}
          </select>
        </Field>
        <Field label="Tamaño (8–400)">
          <input
            className="h-10 rounded-md border-2 p-2" type="number" min={8} max={400} aria-label="Tamaño" value={theme.fontSize}
            onChange={(e) => { setDraft({ ...draft, theme: themeWithFontSize(theme, num(e.target.value, theme.fontSize), SONG_DEFAULT_THEME.minFontSize) }); }}
          />
        </Field>
        <Field label="Interlineado">
          <input className="h-10 rounded-md border-2 p-2" type="number" step={0.05} min={0.8} max={3} value={theme.lineHeight} onChange={(e) => { setTheme({ lineHeight: num(e.target.value, theme.lineHeight) }); }} />
        </Field>
        <Field label="Color del texto">
          <input className="h-10 w-full rounded-md border-2" type="color" value={theme.color.slice(0, 7)} onChange={(e) => { setTheme({ color: e.target.value.toUpperCase() }); }} />
        </Field>
        <Field label="Contorno (grosor)">
          <input className="h-10 rounded-md border-2 p-2" type="number" min={0} max={40} value={theme.outlineWidth} onChange={(e) => { setTheme({ outlineWidth: num(e.target.value, theme.outlineWidth) }); }} />
        </Field>
        <Field label="Color del contorno">
          <input className="h-10 w-full rounded-md border-2" type="color" value={theme.outlineColor.slice(0, 7)} onChange={(e) => { setTheme({ outlineColor: e.target.value.toUpperCase() }); }} />
        </Field>
        <Field label="Sombra (desenfoque)">
          <input className="h-10 rounded-md border-2 p-2" type="number" min={0} max={60} value={theme.shadowBlur} onChange={(e) => { setTheme({ shadowBlur: num(e.target.value, theme.shadowBlur) }); }} />
        </Field>
        <Field label="Sombra (desplazamiento)">
          <input className="h-10 rounded-md border-2 p-2" type="number" min={0} max={40} value={theme.shadowOffset} onChange={(e) => { setTheme({ shadowOffset: num(e.target.value, theme.shadowOffset) }); }} />
        </Field>
        <Field label="Color de la sombra (#RRGGBB o #RRGGBBAA)">
          <input className={`h-10 rounded-md border-2 p-2 font-mono ${HEX.test(theme.shadowColor) ? "" : "border-red-600"}`} value={theme.shadowColor} onChange={(e) => { setTheme({ shadowColor: e.target.value }); }} />
        </Field>
        <Field label="Alineación horizontal">
          <select className="h-10 rounded-md border-2 p-2" value={theme.align} onChange={(e) => { setTheme({ align: e.target.value as TextAlign }); }}>
            <option value="left">Izquierda</option><option value="center">Centro</option><option value="right">Derecha</option>
          </select>
        </Field>
        <Field label="Posición vertical">
          <select className="h-10 rounded-md border-2 p-2" aria-label="Posición vertical" value={theme.verticalAlign} onChange={(e) => { setTheme({ verticalAlign: e.target.value as VerticalAlign }); }}>
            <option value="top">Arriba</option><option value="middle">Centro</option><option value="bottom">Abajo</option>
          </select>
        </Field>
        {(["marginTop", "marginBottom", "marginLeft", "marginRight"] as const).map((key) => (
          <Field key={key} label={{ marginTop: "Margen superior", marginBottom: "Margen inferior", marginLeft: "Margen izquierdo", marginRight: "Margen derecho" }[key]}>
            <input className="h-10 rounded-md border-2 p-2" type="number" min={0} max={900} value={theme[key]} onChange={(e) => { setTheme({ [key]: num(e.target.value, theme[key]) }); }} />
          </Field>
        ))}
      </div>
      <p className="text-sm text-neutral-600" data-testid="min-font">
        Ajuste automático de la salida (red de seguridad): si en otro monitor el texto no cupiera, la letra se reduce hasta <strong>{String(effectiveMin)} px</strong> como mucho (siempre menos que el tamaño, para que pueda reducir).
      </p>

      <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
        <Field label="Crossfade entre diapositivas (ms)">
          <input className="h-10 rounded-md border-2 p-2" type="number" min={0} max={5000} aria-label="Crossfade" value={draft.transitionMs} onChange={(e) => { setDraft({ ...draft, transitionMs: num(e.target.value, draft.transitionMs) }); }} />
        </Field>
        <Field label="Al pasar de la última diapositiva">
          <select className="h-10 rounded-md border-2 p-2" aria-label="Al final" value={draft.atEnd} onChange={(e) => { setDraft({ ...draft, atEnd: e.target.value as SongAtEnd }); }}>
            <option value="stay">Quedarse en la última (recomendado)</option>
            <option value="clear">Quitar el texto de la pantalla</option>
          </select>
        </Field>
      </div>

      <div className="flex items-center gap-3">
        <Button
          size="lg"
          disabled={!connected || problem !== null}
          onClick={() => { void controller.saveSettings({ ...draft, theme: { ...theme, autoFit: true, minFontSize: effectiveMin } }); }}
        >
          Guardar el tema
        </Button>
        <Button variant="outline" onClick={() => { setDraft({ ...draft, theme: { ...SONG_DEFAULT_THEME } }); }}>Restablecer el tema por defecto</Button>
        {problem && <span role="alert" className="text-sm text-red-700">{problem}</span>}
      </div>
    </div>
  );
}
