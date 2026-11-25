import { useEffect, useRef } from "react";
import { useStore } from "zustand";
import { Button } from "@/components/ui/button";
import type { OutputStateChangedPayload } from "@/ipc/protocol";
import { SONG_SHORTCUTS, matchSongShortcut } from "../shortcuts";
import type { SongController } from "./controller";
import { ImportPanel } from "./ImportPanel";
import { ProjectionPanel } from "./ProjectionPanel";
import { SongEditor } from "./SongEditor";
import { SongList } from "./SongList";
import { SongThemeView } from "./SongTheme";

/**
 * Pestaña de Canciones: lista y búsqueda, editor (texto, orden, diapositivas derivadas), importación y tema. Todo el estado y la lógica están en el controlador (que habla
 * con el backend); aquí solo se pinta y se conectan las teclas del mapa único (`../shortcuts.ts`). NO proyecta ni toca la salida.
 */
export function SongsTab({ controller, connected, output = null }: { controller: SongController; connected: boolean; output?: OutputStateChangedPayload | null }) {
  const s = useStore(controller.store);
  const searchRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    if (!s.loaded) void controller.init();
  }, [controller, s.loaded]);

  // Teclas de la pestaña (mapa único). Una tecla mantenida no repite guardados ni borrados.
  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent): void => {
      // El destino puede ser la propia ventana o el documento (no tienen `closest`): solo un elemento puede estar en una fila del orden.
      const target = event.target instanceof Element ? (event.target as HTMLElement) : null;
      const state = controller.store.getState();
      const orderFocused = state.mode === "editor" && target?.closest("[data-order-row]") != null;
      const action = matchSongShortcut(event, target, { mode: state.mode, orderFocused });
      if (!action) return;
      // Las flechas de diapositiva SÍ se repiten al mantenerlas (el envío ya agrupa la ráfaga); Enter y Esc no.
      if (event.repeat && (action === "slideProject" || action === "slideClear")) { event.preventDefault(); return; }
      if (event.repeat && (action === "save" || action === "saveAndClose" || action === "listDelete" || action === "orderRemove" || action === "closeEditor" || action === "listOpen")) {
        event.preventDefault();
        return;
      }
      event.preventDefault();
      const index = Number(target?.closest<HTMLElement>("[data-order-row]")?.dataset.orderRow ?? "-1");
      const rowFocus = (to: number): void => {
        controller.selectOrder(to);
        requestAnimationFrame(() => { document.querySelector<HTMLElement>(`[data-order-row="${String(to)}"]`)?.focus(); });
      };
      switch (action) {
        case "save": void controller.save(); break;
        case "saveAndClose": void controller.saveAndClose(); break;
        case "closeEditor": controller.closeEditor(); break;
        case "newSong": controller.newSong(); break;
        case "focusSearch": searchRef.current?.focus(); searchRef.current?.select(); break;
        case "listPrev": controller.moveList(-1); break;
        case "listNext": controller.moveList(1); break;
        case "listOpen": controller.openSelected(); break;
        case "slideProject": controller.projectCursor(); break;
        case "slideNext": controller.nextSlide(); break;
        case "slidePrev": controller.prevSlide(); break;
        case "slideClear": controller.clearProjection(); break;
        case "listDelete": controller.askDelete(); break;
        case "orderPrev": rowFocus(Math.max(0, index - 1)); break;
        case "orderNext": rowFocus(Math.min((state.editor?.order.length ?? 1) - 1, index + 1)); break;
        case "orderMoveUp": if (index > 0) { controller.moveOrder(index, index - 1); rowFocus(index - 1); } break;
        case "orderMoveDown": if (index >= 0 && index < (state.editor?.order.length ?? 0) - 1) { controller.moveOrder(index, index + 1); rowFocus(index + 1); } break;
        case "orderRemove": if (index >= 0) { controller.removeOrder(index); rowFocus(Math.max(0, index - 1)); } break;
        case "orderRepeat": if (index >= 0) { controller.repeatOrder(index); rowFocus(index + 1); } break;
      }
    };
    window.addEventListener("keydown", onKeyDown);
    return () => { window.removeEventListener("keydown", onKeyDown); };
  }, [controller]);

  const dropFile = (file: File): void => {
    if (!controller.store.getState().importDraft) controller.openImport();
    void controller.importFile(file);
  };

  if (s.screen === "theme") return <SongThemeView controller={controller} connected={connected} />;

  return (
    <div className="flex flex-col gap-4">
      {s.recovered && (
        <div role="alert" data-testid="recovered" className="flex flex-wrap items-center justify-between gap-3 rounded-md border-2 border-amber-600 bg-amber-100 p-3 text-amber-950">
          <p>
            <span className="font-bold">Hay un borrador sin guardar{s.recovered.title ? ` de «${s.recovered.title}»` : ""}</span> de una sesión anterior.
          </p>
          <div className="flex gap-3">
            <Button disabled={!connected} onClick={() => { void controller.openRecovered(); }}>Recuperarlo</Button>
            <Button variant="outline" onClick={controller.discardRecovered}>Descartarlo</Button>
          </div>
        </div>
      )}

      {s.deleteAsk && (
        <div role="alertdialog" data-testid="delete-ask" aria-label="Borrar la canción" className="flex flex-col gap-2 rounded-md border-2 border-red-700 bg-red-50 p-3 text-red-950">
          <p className="font-bold">¿Borrar «{s.deleteAsk.title}»?</p>
          {s.deleteAsk.used > 0 ? (
            <p>Esta canción está usada en {s.deleteAsk.used} servicio(s). No se borran los servicios: cada elemento queda con su título y marcado «Falta» hasta que lo cambies.</p>
          ) : (
            <p>No está en ningún servicio.</p>
          )}
          <p className="text-sm">No hay papelera: se borra la canción con su texto y sus copias anteriores. (El respaldo diario de la base de datos la conserva unos días.)</p>
          <div className="flex gap-3">
            <Button variant="destructive" disabled={!connected} onClick={() => { void controller.confirmDelete(); }}>Borrar la canción</Button>
            <Button variant="outline" onClick={controller.cancelDelete}>Cancelar</Button>
          </div>
        </div>
      )}

      {s.message && s.mode === "list" && (
        <div role="status" className="flex items-start justify-between gap-3 rounded-md border border-amber-600 bg-amber-100 p-3 text-sm text-amber-950">
          <span>{s.message}</span>
          <button type="button" className="shrink-0 underline" onClick={controller.dismissMessage}>Cerrar</button>
        </div>
      )}

      {s.importDraft && <ImportPanel controller={controller} connected={connected} hasSong={s.mode === "editor" && (s.editor?.sections.length ?? 0) > 0} />}

      {s.mode === "list" ? (
        <div onDragOver={(e) => { if (e.dataTransfer.types.includes("Files")) e.preventDefault(); }} onDrop={(e) => { const f = e.dataTransfer.files[0]; if (f) { e.preventDefault(); dropFile(f); } }}>
          <SongList controller={controller} connected={connected} searchRef={searchRef} />
          <ProjectionPanel controller={controller} connected={connected} output={output} />
          <p className="mt-2 text-xs text-neutral-500">Puedes soltar un archivo .txt sobre esta pantalla para importarlo.</p>
        </div>
      ) : (
        <SongEditor controller={controller} connected={connected} onDropFile={dropFile} />
      )}

      <details className="text-sm text-neutral-700">
        <summary className="cursor-pointer font-semibold">Atajos de teclado de la pestaña</summary>
        <ul className="mt-2 grid grid-cols-1 gap-1 sm:grid-cols-2">
          {SONG_SHORTCUTS.map((k) => (
            <li key={`${k.action}-${k.key}-${String(k.alt)}-${String(k.ctrl)}`}><kbd className="rounded border bg-neutral-100 px-1.5 font-mono">{k.label}</kbd> {k.description}</li>
          ))}
        </ul>
        <p className="mt-2 text-xs text-neutral-500">Los atajos globales (Ctrl+Alt+Shift+M/B/C/L/R) no cambian y funcionan siempre. Los controles maestros (Negro, Limpiar, Logo, Congelar) funcionan igual con canciones.</p>
      </details>
    </div>
  );
}
