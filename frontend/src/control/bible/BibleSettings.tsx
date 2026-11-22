import { useRef, useState } from "react";
import { useStore } from "zustand";
import { Button } from "@/components/ui/button";
import type { BibleSettingsPayload, TextAlign, TextTheme, VerticalAlign } from "@/ipc/protocol";
import { CANVAS_HEIGHT, CANVAS_WIDTH } from "@/output/canvas";
import { themeBoxStyle, themeTextStyle } from "@/output/theme";
import type { BibleController } from "./controller";
import { BibleImportSection } from "./BibleImport";
import { BIBLE_DEFAULT_THEME } from "./defaults";
import type { ImportController } from "./importController";
import { mergeSettings } from "./settingsMerge";
import { themeWithFontSize } from "./slide";
import { toSuperscript } from "./superscript";

const HEX = /^#[0-9a-fA-F]{6}([0-9a-fA-F]{2})?$/;
const FONTS = ["Arial", "Segoe UI", "Calibri", "Georgia", "Times New Roman", "Verdana", "Tahoma", "Trebuchet MS"];

const num = (value: string, fallback: number): number => {
  const n = Number(value);
  return Number.isFinite(n) ? n : fallback;
};

/** Vista previa a 1/5 del lienzo de 1920×1080 con el tema editado (texto inventado; no es texto bíblico). */
function ThemePreview({ theme, superscript }: { theme: TextTheme; superscript: boolean }) {
  const scale = 0.2;
  const sample = `${superscript ? `${toSuperscript(16)} ` : ""}Texto de ejemplo para ver el tema: palabras inventadas que ocupan varias líneas del lienzo, con coma, y punto final.`;
  return (
    <div className="overflow-hidden rounded border" style={{ width: CANVAS_WIDTH * scale, height: CANVAS_HEIGHT * scale }} aria-label="Vista previa del tema">
      <div style={{ width: CANVAS_WIDTH, height: CANVAS_HEIGHT, transform: `scale(${String(scale)})`, transformOrigin: "0 0", background: "#1e3a8a", position: "relative" }}>
        <div style={themeBoxStyle(theme, CANVAS_WIDTH, CANVAS_HEIGHT)}>
          <div style={themeTextStyle(theme, theme.fontSize)}>
            <div>{sample}</div>
            <div style={{ marginTop: "0.5em" }}>Libro 1:16 · RVR</div>
          </div>
        </div>
      </div>
    </div>
  );
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return <label className="flex flex-col gap-1 text-sm">{label}{children}</label>;
}

/** Configuración de la Biblia: Biblia por defecto, nombre corto y aviso de derechos por Biblia, opciones y tema propio. Se guarda en el backend (settings.json / BD). */
export function BibleSettingsView({ controller, importer, connected, onClose }: { controller: BibleController; importer?: ImportController; connected: boolean; onClose: () => void }) {
  const s = useStore(controller.store);
  const [draft, setDraft] = useState<BibleSettingsPayload | null>(s.settings);
  // Lo que había al abrir (o tras el último guardado): al guardar solo se aplica lo que se cambió AQUÍ y lo demás conserva el valor actual (H11).
  const initialRef = useRef<BibleSettingsPayload | null>(s.settings);
  const [attributions, setAttributions] = useState<Record<string, string>>(() => Object.fromEntries(s.bibles.map((b) => [b.abbreviation, b.attribution])));

  if (!draft) {
    return (
      <div className="flex flex-col gap-3">
        <p>La configuración aún no se ha cargado.</p>
        <Button variant="outline" onClick={onClose}>Volver</Button>
      </div>
    );
  }

  const theme = draft.theme;
  const setTheme = (patch: Partial<TextTheme>): void => { setDraft({ ...draft, theme: { ...theme, ...patch } }); };
  const shortNameOf = (abbr: string): string => draft.shortNames.find((n) => n.bible === abbr)?.shortName ?? "";
  const setShortName = (abbr: string, value: string): void => {
    const others = draft.shortNames.filter((n) => n.bible !== abbr);
    setDraft({ ...draft, shortNames: value.trim().length > 0 ? [...others, { bible: abbr, shortName: value }] : others });
  };
  const save = (): void => {
    const current = controller.store.getState().settings ?? draft;   // el valor ACTUAL, no el de cuando se abrió la pantalla
    const next = mergeSettings(initialRef.current ?? current, draft, current);
    initialRef.current = next;
    setDraft(next);                                                  // el formulario refleja lo que realmente se guardó (incluidos cambios hechos fuera)
    void controller.saveSettings(next);
  };
  const colorsOk = HEX.test(theme.color) && HEX.test(theme.outlineColor) && HEX.test(theme.shadowColor);
  const valid = colorsOk && theme.fontSize >= 8 && theme.fontSize <= 400 && draft.transitionMs >= 0 && draft.transitionMs <= 5000;

  return (
    <div className="flex flex-col gap-6">
      <div className="flex items-center justify-between">
        <h2 className="text-xl font-bold">Configuración de la Biblia</h2>
        <Button variant="outline" onClick={onClose}>Volver a la Biblia</Button>
      </div>

      <section className="flex flex-col gap-3" aria-label="Biblias instaladas">
        <h3 className="text-lg font-semibold">Biblias</h3>
        <Field label="Biblia por defecto">
          <select
            className="h-10 w-fit rounded-md border-2 p-2"
            value={draft.defaultBible ?? ""}
            onChange={(e) => { setDraft({ ...draft, defaultBible: e.target.value === "" ? null : e.target.value }); }}
          >
            <option value="">Automática (la primera instalada)</option>
            {s.bibles.map((b) => <option key={b.abbreviation} value={b.abbreviation}>{b.abbreviation} — {b.name}</option>)}
          </select>
        </Field>
        {s.bibles.length === 0 && <p className="text-sm text-neutral-500">No hay ninguna Biblia instalada.</p>}
        {s.bibles.map((b) => (
          <div key={b.abbreviation} className="flex flex-col gap-2 rounded-md border p-3">
            <p className="font-semibold">{b.abbreviation} <span className="font-normal text-neutral-600">— {b.name}</span></p>
            <Field label={`Nombre corto con que se proyecta (vacío = «${b.abbreviation}»)`}>
              <input className="h-10 w-48 rounded-md border-2 p-2" maxLength={40} value={shortNameOf(b.abbreviation)} onChange={(e) => { setShortName(b.abbreviation, e.target.value); }} />
            </Field>
            <Field label="Aviso de derechos (el texto que tú escribas; vacío = no se muestra nada)">
              <textarea
                className="min-h-16 rounded-md border-2 p-2"
                value={attributions[b.abbreviation] ?? ""}
                onChange={(e) => { setAttributions({ ...attributions, [b.abbreviation]: e.target.value }); }}
              />
            </Field>
            <Button
              variant="outline"
              className="w-fit"
              disabled={!connected || (attributions[b.abbreviation] ?? "") === b.attribution}
              onClick={() => { void controller.saveAttribution(b.abbreviation, attributions[b.abbreviation] ?? ""); }}
            >
              Guardar aviso de {b.abbreviation}
            </Button>
          </div>
        ))}
      </section>

      {importer && <BibleImportSection importer={importer} bibles={controller} connected={connected} />}

      <section className="flex flex-col gap-3" aria-label="Opciones">
        <h3 className="text-lg font-semibold">Proyección</h3>
        <label className="flex cursor-pointer items-center gap-2 text-sm">
          <input type="checkbox" checked={draft.superscriptNumbers} onChange={(e) => { setDraft({ ...draft, superscriptNumbers: e.target.checked }); }} />
          Números de versículo en superíndice (¹⁶)
        </label>
        <label className="flex cursor-pointer items-center gap-2 text-sm">
          <input type="checkbox" checked={draft.showAttribution} onChange={(e) => { setDraft({ ...draft, showAttribution: e.target.checked }); }} />
          Mostrar el aviso de derechos de la Biblia (si está vacío, no se muestra nada)
        </label>
        <label className="flex cursor-pointer items-center gap-2 text-sm">
          <input type="checkbox" checked={draft.projectOnNavigate} onChange={(e) => { setDraft({ ...draft, projectOnNavigate: e.target.checked }); }} />
          Proyectar al navegar con las flechas
        </label>
        <Field label="Duración del crossfade al cambiar de versículo (ms; unos 250–300 es ágil)">
          <input className="h-10 w-28 rounded-md border-2 p-2" type="number" min={0} max={5000} value={draft.transitionMs} onChange={(e) => { setDraft({ ...draft, transitionMs: num(e.target.value, draft.transitionMs) }); }} />
        </Field>
      </section>

      <section className="flex flex-col gap-3" aria-label="Tema de la Biblia">
        <h3 className="text-lg font-semibold">Tema de la Biblia</h3>
        <p className="text-sm text-neutral-600">Medidas en píxeles del lienzo de 1920×1080. El texto se reparte en páginas para que quepa a este tamaño; nunca se recorta.</p>
        <ThemePreview theme={theme} superscript={draft.superscriptNumbers} />
        <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
          <Field label="Fuente">
            <select className="h-10 rounded-md border-2 p-2" value={FONTS.includes(theme.fontFamily) ? theme.fontFamily : "Arial"} onChange={(e) => { setTheme({ fontFamily: e.target.value }); }}>
              {FONTS.map((f) => <option key={f} value={f}>{f}</option>)}
            </select>
          </Field>
          <Field label="Tamaño (8–400)">
            <input className="h-10 rounded-md border-2 p-2" type="number" min={8} max={400} value={theme.fontSize} onChange={(e) => { setTheme(themeWithFontSize(theme, num(e.target.value, theme.fontSize), BIBLE_DEFAULT_THEME.minFontSize)); }} />
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
            <select className="h-10 rounded-md border-2 p-2" value={theme.verticalAlign} onChange={(e) => { setTheme({ verticalAlign: e.target.value as VerticalAlign }); }}>
              <option value="top">Arriba</option><option value="middle">Centro</option><option value="bottom">Abajo</option>
            </select>
          </Field>
          {(["marginTop", "marginBottom", "marginLeft", "marginRight"] as const).map((key) => (
            <Field key={key} label={{ marginTop: "Margen superior", marginBottom: "Margen inferior", marginLeft: "Margen izquierdo", marginRight: "Margen derecho" }[key]}>
              <input className="h-10 rounded-md border-2 p-2" type="number" min={0} max={900} value={theme[key]} onChange={(e) => { setTheme({ [key]: num(e.target.value, theme[key]) }); }} />
            </Field>
          ))}
        </div>
      </section>

      <div className="flex gap-3">
        <Button size="lg" disabled={!connected || !valid} onClick={() => { save(); }}>Guardar configuración</Button>
        {!valid && <p className="self-center text-sm text-red-700">Hay un valor inválido (colores #RRGGBB o #RRGGBBAA, tamaño 8–400, crossfade 0–5000).</p>}
      </div>
    </div>
  );
}
