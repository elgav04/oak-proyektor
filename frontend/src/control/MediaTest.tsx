import { useState } from "react";
import { Button } from "@/components/ui/button";
import { ipc } from "@/ipc/instance";
import { cleanPath, kindOfPath, THEME_PRESETS } from "./themes";
import { dismissBackgroundFailure, useControlStore } from "./store";

/**
 * VISTA PROVISIONAL de prueba del motor multimedia (NO es la interfaz final): elegir un archivo de fondo (se pega su ruta), escribir un texto,
 * elegir un tema básico y enviarlo. Los controles maestros están en la sección de arriba. No guarda estado de negocio: solo lo que se está escribiendo.
 */
export function MediaTest() {
  const connected = useControlStore((s) => s.connected);
  const output = useControlStore((s) => s.output);
  const backgroundFailure = useControlStore((s) => s.backgroundFailure);
  const [path, setPath] = useState("");
  const [color, setColor] = useState("#1e3a8a");
  const [fadeMs, setFadeMs] = useState(600);
  const [text, setText] = useState("Hola, esta es una prueba\n\nSegundo bloque de texto");
  const [presetKey, setPresetKey] = useState("white");
  const [fontSize, setFontSize] = useState(72);
  const [autoFit, setAutoFit] = useState(true);
  const [textMs, setTextMs] = useState(400);

  const background = output?.state.background;
  const slide = output?.state.content.slide ?? null;

  const applyFile = (): void => {
    const clean = cleanPath(path);
    ipc.send("output.setBackground", { kind: kindOfPath(clean), path: clean, color, muted: true, transitionMs: fadeMs });
  };
  const sendText = (): void => {
    const preset = THEME_PRESETS[presetKey] ?? THEME_PRESETS.white;
    if (!preset) return;
    const blocks = text.split(/\n\s*\n/).map((b) => b.trim()).filter((b) => b.length > 0);
    ipc.send("output.showContent", {
      blocks,
      theme: { ...preset.theme, fontSize, autoFit, minFontSize: Math.min(preset.theme.minFontSize, fontSize) },
      transitionMs: textMs,
    });
  };

  return (
    <section aria-labelledby="media-title" className="flex flex-col gap-4 rounded-md border border-dashed border-neutral-400 p-4">
      <div>
        <h2 id="media-title" className="text-lg font-semibold">Prueba del motor multimedia (provisional)</h2>
        <p className="text-sm text-neutral-600">No es la interfaz final. La salida debe estar abierta y en Live para ver el resultado.</p>
      </div>

      {backgroundFailure && (
        <div role="alert" className="flex items-start justify-between gap-4 rounded-lg border-2 border-red-700 bg-red-600 p-4 text-white">
          <p>
            <span className="font-bold">Fallo del fondo: </span>
            {backgroundFailure.message}
          </p>
          <Button variant="outline" className="shrink-0 text-black" onClick={dismissBackgroundFailure}>Cerrar aviso</Button>
        </div>
      )}
      {background?.warning === "removableDrive" && (
        <div role="alert" className="rounded-lg border-2 border-amber-600 bg-amber-100 p-3 text-sm text-amber-950">
          <span className="font-bold">«{background.fileName}» está en una unidad extraíble.</span> Si se desconecta durante el culto, el fondo cae al color de reserva. Mejor cópialo a un disco fijo.
        </div>
      )}

      <fieldset className="flex flex-col gap-2" disabled={!connected}>
        <legend className="text-sm font-semibold">Fondo</legend>
        <label className="flex flex-col gap-1 text-sm">
          Ruta del archivo (.mp4 .m4v .webm .jpg .jpeg .png .webp); pega la ruta completa, con o sin comillas
          <input className="rounded-md border p-2 font-mono text-sm" value={path} placeholder="C:\Videos\fondo.mp4" onChange={(e) => { setPath(e.target.value); }} />
        </label>
        <div className="flex flex-wrap items-center gap-3 text-sm">
          <label className="flex items-center gap-2">Color (de reserva / fondo de color) <input type="color" value={color} onChange={(e) => { setColor(e.target.value); }} /></label>
          <label className="flex items-center gap-2">Fundido (ms) <input className="w-24 rounded-md border p-1" type="number" min={0} max={5000} value={fadeMs} onChange={(e) => { setFadeMs(Number(e.target.value)); }} /></label>
        </div>
        <div className="flex flex-wrap gap-3">
          <Button onClick={applyFile} disabled={cleanPath(path).length === 0}>Aplicar archivo</Button>
          <Button variant="outline" onClick={() => { ipc.send("output.setBackground", { kind: "color", path: null, color, muted: true, transitionMs: fadeMs }); }}>Aplicar solo el color</Button>
          <Button variant="outline" onClick={() => { ipc.send("output.setBackground", { kind: "none", path: null, color: null, muted: true, transitionMs: fadeMs }); }}>Sin fondo</Button>
        </div>
        <p className="text-xs text-neutral-600">
          Fondo actual: {background ? `${background.kind}${background.fileName ? ` — ${background.fileName}` : ""}` : "—"}
        </p>
      </fieldset>

      <fieldset className="flex flex-col gap-2" disabled={!connected}>
        <legend className="text-sm font-semibold">Texto</legend>
        <label className="flex flex-col gap-1 text-sm">
          Bloques (separados por una línea en blanco)
          <textarea className="min-h-24 rounded-md border p-2" value={text} onChange={(e) => { setText(e.target.value); }} />
        </label>
        <div className="flex flex-wrap items-center gap-3 text-sm">
          <label className="flex items-center gap-2">
            Tema
            <select className="rounded-md border p-1" value={presetKey} onChange={(e) => { setPresetKey(e.target.value); }}>
              {Object.entries(THEME_PRESETS).map(([key, p]) => <option key={key} value={key}>{p.label}</option>)}
            </select>
          </label>
          <label className="flex items-center gap-2">Tamaño <input className="w-20 rounded-md border p-1" type="number" min={8} max={400} value={fontSize} onChange={(e) => { setFontSize(Number(e.target.value)); }} /></label>
          <label className="flex items-center gap-2"><input type="checkbox" checked={autoFit} onChange={(e) => { setAutoFit(e.target.checked); }} /> Ajuste automático</label>
          <label className="flex items-center gap-2">Crossfade (ms) <input className="w-24 rounded-md border p-1" type="number" min={0} max={5000} value={textMs} onChange={(e) => { setTextMs(Number(e.target.value)); }} /></label>
        </div>
        <div className="flex flex-wrap gap-3">
          <Button onClick={sendText}>Enviar texto</Button>
          <Button variant="outline" onClick={() => { ipc.send("output.clearContent", { transitionMs: textMs }); }}>Quitar texto (no es «Clear»)</Button>
        </div>
        <p className="text-xs text-neutral-600">Slide actual: {slide ? `${slide.id} (${String(slide.blocks.length)} bloques)` : "ninguno"}</p>
      </fieldset>
    </section>
  );
}
