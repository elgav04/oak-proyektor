import { useRef } from "react";
import { useStore } from "zustand";
import { Button } from "@/components/ui/button";
import type { ForcedEncoding } from "./fileDecoder";
import type { SongController } from "./controller";

const FORMAT_LABEL: Record<string, string> = {
  brackets: "etiquetas entre corchetes",
  aliasLines: "líneas con nombre de sección (Verso, Coro…)",
  blocks: "bloques separados por líneas en blanco (sin etiquetas)",
  empty: "sin texto",
};

const ENCODING_LABEL: Record<string, string> = {
  "utf-8": "UTF-8",
  "utf-8-bom": "UTF-8 con marca (BOM)",
  "windows-1252": "Windows-1252 (el archivo no era UTF-8)",
  "utf-16le": "UTF-16 (little endian)",
  "utf-16be": "UTF-16 (big endian)",
};

/**
 * Importar: pegar texto o soltar/elegir un .txt. Vista previa ANTES de guardar: codificación detectada (con selector por si no acierta), formato reconocido, secciones y
 * orden resultantes, avisos y errores. No guarda nada: «Usar en el editor» lo lleva al editor, donde se guarda como cualquier otra edición. El texto se interpreta en el
 * backend (`song.parseText`); aquí solo se pinta.
 */
export function ImportPanel({ controller, connected, hasSong }: { controller: SongController; connected: boolean; hasSong: boolean }) {
  const d = useStore(controller.store, (s) => s.importDraft);
  const fileRef = useRef<HTMLInputElement>(null);
  if (!d) return null;
  const parsed = d.parsed;
  const blocked = !parsed || parsed.errors.length > 0 || parsed.sections.length === 0 || d.parsing;
  const orderCount = (index: number): number => parsed?.arrangement.filter((i) => i === index).length ?? 0;

  return (
    <section aria-label="Importar texto" className="flex flex-col gap-3 rounded-md border-2 border-dashed border-blue-500 bg-blue-50 p-4">
      <div className="flex items-center justify-between">
        <h2 className="text-lg font-semibold">Importar la letra</h2>
        <Button variant="outline" onClick={controller.closeImport}>Cancelar</Button>
      </div>
      <p className="text-sm text-neutral-700">
        Pega el texto o suelta aquí un archivo <code>.txt</code>. Con etiquetas entre corchetes (<code>[Verso 1]</code>, <code>[Coro]</code>…); sin etiquetas, cada bloque separado por una
        línea en blanco es una sección. Una línea en blanco dentro de una sección es un corte de diapositiva. Opcional: una última línea <code>Orden: V1 C V2 C</code>.
      </p>

      <div className="flex flex-wrap items-center gap-3">
        <label className="flex flex-col gap-1 text-sm">
          Título de la canción
          <input className="h-10 w-72 rounded-md border-2 p-2" value={d.title} maxLength={200} onChange={(e) => { controller.importSetTitle(e.target.value); }} aria-label="Título" />
        </label>
        <input
          ref={fileRef}
          type="file"
          accept=".txt,text/plain"
          className="hidden"
          aria-label="Elegir un archivo .txt"
          onChange={(e) => {
            const file = e.target.files?.[0];
            if (file) void controller.importFile(file);
            e.target.value = "";
          }}
        />
        <Button variant="outline" className="self-end" disabled={!connected} onClick={() => { fileRef.current?.click(); }}>Elegir un archivo .txt…</Button>
        <label className="flex cursor-pointer items-center gap-2 self-end text-sm">
          <input type="checkbox" checked={d.merge} onChange={(e) => { controller.importSetMerge(e.target.checked); }} />
          Sin etiquetas: fusionar los bloques idénticos como repetición
        </label>
      </div>

      {d.fileName && (
        <div className="flex flex-wrap items-center gap-3 text-sm" data-testid="file-info">
          <span className="font-semibold">{d.fileName}</span>
          {d.encoding && <span>Codificación detectada: <strong>{ENCODING_LABEL[d.encoding] ?? d.encoding}</strong></span>}
          <label className="flex items-center gap-2">
            Leer como
            <select
              className="h-9 rounded-md border-2 p-1"
              value={d.forced}
              aria-label="Codificación"
              onChange={(e) => { controller.importForceEncoding(e.target.value as ForcedEncoding); }}
            >
              <option value="auto">Automática</option>
              <option value="utf-8">UTF-8</option>
              <option value="windows-1252">Windows-1252</option>
              <option value="utf-16le">UTF-16 LE</option>
              <option value="utf-16be">UTF-16 BE</option>
            </select>
          </label>
        </div>
      )}
      {d.fileError && <p role="alert" className="rounded border border-red-600 bg-red-100 p-2 text-sm text-red-900">{d.fileError}</p>}
      {d.decodeWarnings.map((w) => <p key={w} role="status" className="rounded border border-amber-600 bg-amber-100 p-2 text-sm text-amber-950">{w}</p>)}

      <textarea
        className="min-h-48 rounded-md border-2 p-3 font-mono text-sm"
        aria-label="Texto de la canción"
        placeholder="Pega aquí la letra…"
        spellCheck={false}
        value={d.text}
        onChange={(e) => { controller.importSetText(e.target.value); }}
      />

      <div aria-label="Vista previa" className="flex flex-col gap-2" data-testid="import-preview">
        {d.parseError && <p role="alert" className="rounded border border-red-600 bg-red-100 p-2 text-sm text-red-900">{d.parseError}</p>}
        {d.parsing && <p className="text-sm text-neutral-600">Interpretando el texto…</p>}
        {parsed && (
          <>
            <p className="text-sm">
              Formato reconocido: <strong>{FORMAT_LABEL[parsed.detected] ?? parsed.detected}</strong> · {String(parsed.sections.length)} sección(es) · orden de {String(parsed.arrangement.length)} elemento(s)
            </p>
            {parsed.errors.map((e) => <p key={e} role="alert" className="rounded border border-red-600 bg-red-100 p-2 text-sm text-red-900">{e}</p>)}
            {parsed.warnings.map((w) => <p key={w} role="status" className="rounded border border-amber-600 bg-amber-100 p-2 text-sm text-amber-950">{w}</p>)}
            {parsed.sections.length > 0 && (
              <ul className="grid grid-cols-1 gap-1 sm:grid-cols-2" aria-label="Secciones reconocidas">
                {parsed.sections.map((s, i) => (
                  <li key={`${s.label}-${String(i)}`} className="flex items-center justify-between gap-2 rounded border bg-white p-2 text-sm">
                    <span><strong>{s.label}</strong> <span className="text-neutral-500">({s.kind})</span></span>
                    <span className="text-neutral-600">{String(s.text.split("\n").filter((l) => l !== "").length)} línea(s) · {String(s.text.split("\n\n").length)} diapositiva(s) manual(es) · en el orden ×{String(orderCount(i))}</span>
                  </li>
                ))}
              </ul>
            )}
            {parsed.arrangement.length > 0 && (
              <p className="text-sm text-neutral-700">Orden: {parsed.arrangement.map((i) => parsed.sections[i]?.label ?? "?").join(" → ")}</p>
            )}
          </>
        )}
      </div>

      <div className="flex items-center gap-3">
        <Button size="lg" disabled={!connected || blocked || d.title.trim() === "" && !hasSong} onClick={controller.applyImport}>
          {hasSong ? "Reemplazar el texto de esta canción" : "Usar en el editor"}
        </Button>
        {!hasSong && d.title.trim() === "" && <span className="text-sm text-red-700">Falta el título.</span>}
        <span className="text-sm text-neutral-600">No se guarda nada todavía: lo revisas en el editor y guardas.</span>
      </div>
    </section>
  );
}
