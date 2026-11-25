import { useStore } from "zustand";
import { Button } from "@/components/ui/button";
import { cleanPath, kindOfPath } from "../themes";
import { isDirty, saveBlocker, saveLabel, type SongController } from "./controller";
import { SONG_DEFAULT_SETTINGS, TEXT_PLACEHOLDER } from "./defaults";
import { OrderList } from "./OrderList";
import { SlideStrip } from "./SlideStrip";

const REVISION_REASON: Record<string, string> = { save: "Guardado", import: "Importación", restore: "Restauración" };

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return <label className="flex flex-col gap-1 text-sm">{label}{children}</label>;
}

const FORMAT_LABEL: Record<string, string> = {
  brackets: "etiquetas entre corchetes",
  aliasLines: "líneas con nombre de sección",
  blocks: "sin etiquetas: cada bloque separado por una línea en blanco es una sección",
  empty: "sin texto",
};

/**
 * Editor de una canción: a un lado el texto único con etiquetas, al otro el orden de reproducción y las diapositivas derivadas (con el tema real de canciones sobre el
 * lienzo de 1920×1080). NO proyecta nada ni toca la salida: es edición y vista previa. Guardar es explícito (Ctrl+S) y deja una copia previa de la canción.
 */
export function SongEditor({ controller, connected, onDropFile }: { controller: SongController; connected: boolean; onDropFile: (file: File) => void }) {
  const s = useStore(controller.store);
  const e = s.editor;
  if (!e) return null;
  const theme = (s.settings ?? SONG_DEFAULT_SETTINGS).theme;
  const dirty = isDirty(e);
  const blocker = saveBlocker(e);
  const isNew = e.songId === null;
  const canSave = connected && !s.saving && (dirty || isNew) && blocker === null;

  return (
    <div
      className="flex flex-col gap-4"
      onDragOver={(ev) => { if (ev.dataTransfer.types.includes("Files")) ev.preventDefault(); }}
      onDrop={(ev) => {
        const file = ev.dataTransfer.files[0];
        if (!file) return;
        ev.preventDefault();
        onDropFile(file);
      }}
    >
      <div className="flex flex-wrap items-center gap-3">
        <Button variant="outline" onClick={controller.closeEditor}>← Canciones (Esc)</Button>
        <input
          className="h-12 min-w-64 flex-1 rounded-md border-2 p-3 text-lg font-semibold"
          aria-label="Título de la canción"
          placeholder="Título de la canción"
          maxLength={200}
          value={e.title}
          disabled={!connected}
          onChange={(ev) => { controller.setField("title", ev.target.value); }}
        />
        <span
          data-testid="dirty-badge"
          role="status"
          className={`rounded-full px-3 py-1 text-sm font-bold ${dirty || isNew ? "bg-amber-300 text-amber-950" : "bg-green-100 text-green-900"}`}
        >
          {isNew ? "NUEVA, SIN GUARDAR" : dirty ? "SIN GUARDAR" : "Guardada"}
        </span>
        <Button size="lg" className="h-12" disabled={!canSave} onClick={() => { void controller.save(); }} title={blocker ?? undefined}>
          {s.saving ? "Guardando…" : `${saveLabel(e)} · Ctrl+S`}
        </Button>
      </div>

      {s.message && (
        <div role="status" className="flex items-start justify-between gap-3 rounded-md border border-amber-600 bg-amber-100 p-3 text-sm text-amber-950">
          <span>{s.message}</span>
          <button type="button" className="shrink-0 underline" onClick={controller.dismissMessage}>Cerrar</button>
        </div>
      )}

      {s.conflict && (
        <div role="alert" data-testid="conflict" className="flex flex-col gap-2 rounded-md border-2 border-red-700 bg-red-50 p-3 text-red-950">
          <p className="font-bold">Otra ventana o una orden cambió esta canción desde que la abriste.</p>
          <p className="text-sm">No se guardó nada. Elige: ver la versión actual (pierdes tus cambios sin guardar) o guardar lo tuyo como una canción NUEVA.</p>
          <div className="flex gap-3">
            <Button variant="outline" onClick={() => { void controller.resolveConflict("reload"); }}>Ver la versión actual</Button>
            <Button variant="outline" onClick={() => { void controller.resolveConflict("saveCopy"); }}>Guardar mis cambios como canción nueva</Button>
          </div>
        </div>
      )}

      {s.confirmLeave && (
        <div role="alertdialog" data-testid="leave-bar" aria-label="Cambios sin guardar" className="flex flex-col gap-2 rounded-md border-2 border-amber-600 bg-amber-100 p-3 text-amber-950">
          <p className="font-bold">Hay cambios sin guardar.</p>
          <div className="flex flex-wrap gap-3">
            <Button disabled={!connected || blocker !== null} onClick={() => { void controller.resolveLeave("save"); }}>Guardar y salir</Button>
            <Button variant="outline" onClick={() => { void controller.resolveLeave("discard"); }}>Descartar los cambios</Button>
            <Button variant="outline" onClick={() => { void controller.resolveLeave("stay"); }}>Seguir editando</Button>
          </div>
          {blocker && <p className="text-sm">Para guardar: {blocker}</p>}
        </div>
      )}

      <details className="rounded-md border p-3" open={isNew}>
        <summary className="cursor-pointer font-semibold">Ficha de la canción (autor, derechos, CCLI, notas y fondo)</summary>
        <p className="mt-2 text-sm text-neutral-600">El autor, los derechos y el número CCLI solo se GUARDAN en la ficha: no se proyectan.</p>
        <div className="mt-3 grid grid-cols-1 gap-3 sm:grid-cols-3">
          <Field label="Autor"><input className="h-10 rounded-md border-2 p-2" maxLength={200} value={e.author} onChange={(ev) => { controller.setField("author", ev.target.value); }} /></Field>
          <Field label="Derechos"><input className="h-10 rounded-md border-2 p-2" maxLength={500} value={e.copyright} onChange={(ev) => { controller.setField("copyright", ev.target.value); }} /></Field>
          <Field label="Número CCLI"><input className="h-10 rounded-md border-2 p-2" maxLength={40} value={e.ccli} onChange={(ev) => { controller.setField("ccli", ev.target.value); }} /></Field>
        </div>
        <Field label="Notas (no se proyectan)">
          <textarea className="mt-3 min-h-16 rounded-md border-2 p-2" maxLength={4000} value={e.notes} onChange={(ev) => { controller.setField("notes", ev.target.value); }} />
        </Field>
        <fieldset className="mt-3 flex flex-col gap-2 rounded-md border p-3">
          <legend className="px-1 text-sm font-semibold">Fondo de esta canción (opcional)</legend>
          <p className="text-xs text-neutral-600">Sin fondo propio, la canción no toca el fondo que ya tenga la salida.</p>
          <div className="flex flex-wrap items-end gap-3">
            <Field label="Tipo">
              <select
                className="h-10 rounded-md border-2 p-2"
                aria-label="Tipo de fondo"
                value={e.background?.kind ?? "none"}
                onChange={(ev) => {
                  const kind = ev.target.value;
                  controller.setBackground(kind === "none" ? null : { kind, path: kind === "color" ? null : (e.background?.path ?? ""), color: kind === "color" ? (e.background?.color ?? "#1e3a8a") : (e.background?.color ?? null), muted: e.background?.muted ?? true });
                }}
              >
                <option value="none">Ninguno (no tocar el fondo actual)</option>
                <option value="image">Imagen</option>
                <option value="video">Video</option>
                <option value="color">Color</option>
              </select>
            </Field>
            {e.background && e.background.kind !== "color" && (
              <Field label="Ruta completa del archivo (jpg, png, webp, mp4, webm)">
                <input
                  className="h-10 w-96 rounded-md border-2 p-2 font-mono text-sm"
                  aria-label="Ruta del fondo"
                  value={e.background.path ?? ""}
                  placeholder="C:\Fondos\fondo.mp4"
                  onChange={(ev) => {
                    const path = cleanPath(ev.target.value);
                    controller.setBackground({ ...e.background, kind: path === "" ? e.background?.kind ?? "video" : kindOfPath(path), path, color: e.background?.color ?? null, muted: e.background?.muted ?? true });
                  }}
                />
              </Field>
            )}
            {e.background?.kind === "color" && (
              <Field label="Color">
                <input className="h-10 w-24 rounded-md border-2" type="color" value={(e.background.color ?? "#1e3a8a").slice(0, 7)} onChange={(ev) => { controller.setBackground({ ...e.background, kind: "color", path: null, color: ev.target.value, muted: true }); }} />
              </Field>
            )}
            {e.background?.kind === "video" && (
              <label className="flex items-center gap-2 text-sm">
                <input type="checkbox" checked={e.background.muted} onChange={(ev) => { controller.setBackground({ ...e.background, kind: "video", path: e.background?.path ?? null, color: e.background?.color ?? null, muted: ev.target.checked }); }} />
                Silenciado
              </label>
            )}
          </div>
        </fieldset>
      </details>

      <div className="grid grid-cols-1 gap-4 lg:grid-cols-2">
        <section aria-label="Texto" className="flex flex-col gap-2">
          <div className="flex items-center justify-between">
            <h2 className="text-lg font-semibold">Texto</h2>
            <Button variant="outline" size="sm" disabled={!connected} onClick={() => { controller.openImport(e.sections.length === 0 ? e.text : ""); }}>Importar / pegar un .txt…</Button>
          </div>
          <textarea
            className="min-h-[24rem] rounded-md border-2 p-3 font-mono text-sm"
            aria-label="Texto de la canción"
            placeholder={TEXT_PLACEHOLDER}
            spellCheck={false}
            value={e.text}
            disabled={!connected}
            onChange={(ev) => { controller.setText(ev.target.value); }}
          />
          <p className="text-xs text-neutral-600">
            Cada sección UNA vez: <code>[Verso 1]</code>, <code>[Coro]</code>… Una línea en blanco dentro de una sección es un corte de diapositiva. Las repeticiones se hacen en el orden de la derecha.
          </p>
          <div aria-label="Interpretación del texto" data-testid="parse-info" className="flex flex-col gap-1 text-sm">
            <p className="text-neutral-600">{e.parse.stale ? "Interpretando…" : `Formato: ${FORMAT_LABEL[e.parse.detected] ?? e.parse.detected} · ${String(e.sections.length)} sección(es)`}</p>
            {e.parse.errors.map((m) => <p key={m} role="alert" className="rounded border border-red-600 bg-red-100 p-2 text-red-900">{m}</p>)}
            {e.parse.warnings.map((m) => <p key={m} role="status" className="rounded border border-amber-600 bg-amber-100 p-2 text-amber-950">{m}</p>)}
            {e.parse.orderIgnored && <p role="status" className="rounded border border-amber-600 bg-amber-100 p-2 text-amber-950">El texto repite etiquetas o trae una línea «Orden:»: aquí el texto solo define las secciones; el orden se edita en la lista de la derecha.</p>}
          </div>
        </section>

        <section aria-label="Orden y diapositivas" className="flex flex-col gap-3">
          <h2 className="text-lg font-semibold">Orden de reproducción</h2>
          <OrderList
            order={e.order}
            sections={e.sections}
            selected={e.selectedOrder}
            disabled={!connected}
            onSelect={controller.selectOrder}
            onMove={controller.moveOrder}
            onRemove={controller.removeOrder}
            onRepeat={controller.repeatOrder}
            onInsert={controller.insertSection}
          />
          <h2 className="text-lg font-semibold">Diapositivas (vista previa con el tema de canciones)</h2>
          <SlideStrip slides={e.slides} theme={theme} busy={e.slidesBusy} selected={e.selectedSlide} onSelect={controller.selectSlide} />
        </section>
      </div>

      <section aria-label="Versiones" className="flex flex-col gap-2 rounded-md border p-3">
        <div className="flex flex-wrap items-center gap-3">
          <h2 className="text-lg font-semibold">Versiones anteriores</h2>
          <Button variant="outline" disabled={!connected || s.saving || e.songId === null || (e.history?.length ?? 0) === 0} onClick={() => { void controller.undoLastSave(); }}>Deshacer último guardado</Button>
          <Button variant="outline" disabled={!connected || e.songId === null} onClick={() => { if (e.historyOpen) controller.closeHistory(); else void controller.openHistory(); }}>
            {e.historyOpen ? "Ocultar versiones" : "Restaurar versión…"}
          </Button>
          <Button variant="outline" disabled={!connected || e.songId === null} onClick={() => { controller.askDelete(e.songId ?? undefined); }}>Borrar la canción</Button>
          {e.songId !== null && <span className="text-sm text-neutral-600">{e.usedInServices > 0 ? `Está en ${String(e.usedInServices)} servicio(s).` : "No está en ningún servicio."}</span>}
        </div>
        {dirty && e.songId !== null && <p className="text-xs text-amber-900">Deshacer o restaurar descarta los cambios sin guardar de esta pantalla.</p>}
        {e.historyOpen && (
          <ul aria-label="Copias previas" className="flex flex-col gap-1">
            {(e.history ?? []).length === 0 && <li className="text-sm text-neutral-500">Todavía no hay copias previas: se crea una cada vez que guardas un cambio.</li>}
            {(e.history ?? []).map((r) => (
              <li key={r.id} className="flex items-center justify-between gap-3 rounded border p-2 text-sm">
                <span>{new Date(r.createdAt).toLocaleString("es")} · {REVISION_REASON[r.reason] ?? r.reason} · {String(r.sectionCount)} sección(es)</span>
                <Button size="sm" variant="outline" disabled={!connected || s.saving} onClick={() => { void controller.restoreVersion(r.id); }}>Restaurar esta versión</Button>
              </li>
            ))}
          </ul>
        )}
      </section>
    </div>
  );
}
