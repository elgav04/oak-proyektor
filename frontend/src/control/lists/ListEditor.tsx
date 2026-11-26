import { DndContext, KeyboardSensor, PointerSensor, closestCenter, useSensor, useSensors, type DragEndEvent } from "@dnd-kit/core";
import { SortableContext, sortableKeyboardCoordinates, useSortable, verticalListSortingStrategy } from "@dnd-kit/sortable";
import { CSS } from "@dnd-kit/utilities";
import { useEffect, type RefObject } from "react";
import { useStore } from "zustand";
import { Button } from "@/components/ui/button";
import type { BibleDto, PlaylistItemDto } from "@/ipc/protocol";
import { matchListSearchKey } from "../shortcuts";
import type { ListController } from "./controller";
import type { EditForm, ListEditor } from "./editor";
import { orderedItems } from "./editModel";

const TYPE_LABEL: Record<string, string> = { song: "Canción", passage: "Pasaje", note: "Nota" };
const MESSAGE_CLASS = { info: "border-green-700 bg-green-50 text-green-950", error: "border-amber-600 bg-amber-100 text-amber-950", conflict: "border-red-700 bg-red-50 text-red-950" } as const;

function formatDate(date: string | null): string {
  if (!date) return "plantilla (sin fecha)";
  const [y, m, d] = date.split("-");
  return `${d ?? ""}/${m ?? ""}/${y ?? ""}`;
}

interface RowProps {
  item: PlaylistItemDto;
  index: number;
  total: number;
  selected: boolean;
  disabled: boolean;
  bibles: readonly BibleDto[];
  editor: ListEditor;
}

/** Por qué no se puede proyectar (faltante, Biblia no instalada, pasaje no encontrado), o null. */
function problemOf(item: PlaylistItemDto): string | null {
  if (item.isMissing) return "La canción ya no existe (se borró).";
  if (item.itemType === "passage" && item.resolution === "bibleNotInstalled") return `La Biblia ${item.bibleAbbreviation ?? ""} no está instalada.`;
  if (item.itemType === "passage" && item.resolution === "passageNotFound") return `La Biblia ${item.bibleAbbreviation ?? ""} no tiene este pasaje.`;
  return null;
}

function Row({ item, index, total, selected, disabled, bibles, editor }: RowProps) {
  const { attributes, listeners, setNodeRef, transform, transition, isDragging } = useSortable({ id: item.id, disabled });
  const problem = problemOf(item);
  return (
    <li
      ref={setNodeRef}
      style={{ transform: CSS.Transform.toString(transform), transition }}
      data-edit-row={String(item.id)}
      data-selected={selected ? "true" : undefined}
      data-problem={problem ? "true" : undefined}
      aria-current={selected ? "true" : undefined}
      aria-label={`${String(index + 1)}. ${item.title}`}
      className={`flex flex-col gap-1 rounded border p-2 ${problem ? "border-red-600 bg-red-50" : selected ? "border-blue-600 bg-blue-50" : "bg-white"} ${isDragging ? "opacity-60" : ""}`}
      onClick={() => { editor.select(item.id); }}
    >
      <div className="flex items-center gap-2">
        <button type="button" aria-label={`Arrastrar ${item.title}`} className="cursor-grab rounded px-1 text-lg leading-none text-neutral-500 hover:bg-neutral-100" {...attributes} {...listeners}>⠿</button>
        <span className="w-6 text-right text-sm tabular-nums text-neutral-500">{index + 1}</span>
        <span className="w-16 text-xs font-semibold uppercase text-neutral-600">{TYPE_LABEL[item.itemType] ?? item.itemType}</span>
        <span className={`min-w-0 flex-1 truncate text-lg ${item.itemType === "note" ? "italic" : "font-semibold"} ${item.isMissing ? "text-red-700 line-through" : ""}`}>{item.title}</span>
        {item.itemType === "note" && <Button type="button" variant="outline" size="sm" disabled={disabled} onClick={() => { editor.openForm("note", item.id); }}>Editar</Button>}
        <Button type="button" variant="outline" size="sm" disabled={disabled || index === 0} aria-label={`Subir ${item.title}`} onClick={() => { editor.moveBy(item.id, -1); }}>↑</Button>
        <Button type="button" variant="outline" size="sm" disabled={disabled || index === total - 1} aria-label={`Bajar ${item.title}`} onClick={() => { editor.moveBy(item.id, 1); }}>↓</Button>
        <Button type="button" variant="outline" size="sm" disabled={disabled || problem !== null && item.isMissing} aria-label={`Duplicar ${item.title}`} title="Duplicar justo después (Ctrl+D)" onClick={() => { editor.duplicateItem(item.id); }}>Duplicar</Button>
        <Button type="button" variant="outline" size="sm" disabled={disabled} aria-label={`Quitar ${item.title}`} title="Quitar de la lista (Supr; Ctrl+Z lo recupera)" onClick={() => { editor.removeItem(item.id); }}>✕ Quitar</Button>
      </div>
      {item.itemType === "note" && item.note && <p className="pl-9 text-sm text-neutral-700">{item.note}{item.clearOnActivate ? "  ·  limpia la pantalla al activarla" : ""}</p>}
      {item.itemType === "note" && !item.note && item.clearOnActivate && <p className="pl-9 text-sm text-neutral-700">Limpia la pantalla al activarla.</p>}
      {problem && (
        <div className="flex flex-col gap-1 pl-9" data-testid="problem">
          <p className="text-sm font-semibold text-red-700">{problem} No se puede proyectar; en EN VIVO se salta.</p>
          <div className="flex flex-wrap items-center gap-2">
            {item.isMissing && <Button type="button" size="sm" disabled={disabled} onClick={() => { editor.startReplace(item.id); }}>Reemplazar por otra canción…</Button>}
            {item.itemType === "passage" && item.offeredBible && (
              <Button type="button" size="sm" disabled={disabled} onClick={() => { editor.setPassageBible(item.id, item.offeredBible ?? ""); }}>Usar la Biblia {item.offeredBible}</Button>
            )}
            {item.itemType === "passage" && bibles.length > 0 && (
              <select
                aria-label={`Cambiar la Biblia de ${item.title}`}
                className="rounded border px-2 py-1 text-sm"
                value=""
                disabled={disabled}
                onChange={(e) => { editor.setPassageBible(item.id, e.target.value); e.currentTarget.blur(); }}
              >
                <option value="">Cambiar de Biblia…</option>
                {bibles.map((b) => <option key={b.abbreviation} value={b.abbreviation}>{b.abbreviation} — {b.name}</option>)}
              </select>
            )}
            <span className="text-xs text-neutral-600">o quítalo con «✕ Quitar».</span>
          </div>
        </div>
      )}
    </li>
  );
}

function FormView({ form, editor, disabled, count }: { form: EditForm; editor: ListEditor; disabled: boolean; count: number }) {
  const onKey = (e: React.KeyboardEvent): void => {
    if (e.key === "Enter") { e.preventDefault(); editor.submitForm(); }
    else if (e.key === "Escape") { e.preventDefault(); editor.closeForm(); }
  };
  if (form.kind === "delete") {
    return (
      <div role="alertdialog" aria-label="Borrar la lista" data-testid="form-delete" className="flex flex-col gap-2 rounded-md border-2 border-red-700 bg-red-50 p-3 text-red-950">
        <p className="font-bold">¿Borrar esta lista con sus {count} elemento(s)?</p>
        <p className="text-sm">No hay papelera (las canciones y las Biblias no se tocan). El respaldo diario de la base de datos la conserva unos días.</p>
        <div className="flex gap-3">
          <Button variant="destructive" disabled={disabled} onClick={() => { editor.submitForm(); }}>Borrar la lista</Button>
          <Button variant="outline" onClick={() => { editor.closeForm(); }}>Cancelar</Button>
        </div>
      </div>
    );
  }
  if (form.kind === "note") return null;
  const title = form.kind === "new" ? "Nueva lista" : form.kind === "meta" ? "Renombrar o cambiar la fecha" : "Duplicar la lista";
  const action = form.kind === "new" ? "Crear" : form.kind === "meta" ? "Guardar" : "Duplicar";
  return (
    <div role="group" aria-label={title} data-testid={`form-${form.kind}`} className="flex flex-col gap-2 rounded-md border-2 border-blue-700 bg-blue-50 p-3">
      <p className="font-bold">{title}</p>
      <div className="flex flex-wrap items-end gap-3">
        <label className="flex flex-col text-sm">Nombre
          <input className="h-10 w-72 rounded border-2 px-2 text-base" aria-label="Nombre de la lista" value={form.name} maxLength={120} autoFocus onChange={(e) => { editor.setField("name", e.target.value); }} onKeyDown={onKey} />
        </label>
        <label className="flex flex-col text-sm">Fecha (opcional)
          <input type="date" className="h-10 rounded border-2 px-2 text-base" aria-label="Fecha de la lista" value={form.date} onChange={(e) => { editor.setField("date", e.target.value); }} onKeyDown={onKey} />
        </label>
        <Button type="button" variant="outline" size="sm" onClick={() => { editor.setField("date", ""); }}>Sin fecha (plantilla)</Button>
        <Button disabled={disabled} onClick={() => { editor.submitForm(); }}>{action}</Button>
        <Button variant="outline" onClick={() => { editor.closeForm(); }}>Cancelar</Button>
      </div>
      {form.kind === "duplicate" && <p className="text-xs text-neutral-700">Los elementos cuya canción se borró NO se copian: se te dirá cuáles.</p>}
    </div>
  );
}

/**
 * Modo EDITAR de la pestaña «Lista»: crear, renombrar, duplicar y borrar listas; añadir canciones (búsqueda), notas y —desde la Biblia— pasajes; reordenar arrastrando
 * (`@dnd-kit`, también con teclado) o con Alt+↑/↓; quitar con Supr (Ctrl+Z lo recupera); y resolver los faltantes. Toda la lógica está en el editor; aquí solo se pinta.
 * Nada de esto proyecta ni toca la salida.
 */
export function ListEditorView({ controller, editor, connected, searchRef }: { controller: ListController; editor: ListEditor; connected: boolean; searchRef: RefObject<HTMLInputElement | null> }) {
  const view = useStore(controller.store);
  const e = useStore(editor.store);
  const sensors = useSensors(useSensor(PointerSensor, { activationConstraint: { distance: 6 } }), useSensor(KeyboardSensor, { coordinateGetter: sortableKeyboardCoordinates }));

  useEffect(() => {
    void editor.enter();
  }, [editor]);

  const list = view.list;
  const items = orderedItems(list?.items ?? [], e.localOrder, e.hidden);
  const ids = items.map((i) => i.id);
  const off = !connected || list === null;
  const replacing = e.replaceTarget === null ? null : items.find((i) => i.id === e.replaceTarget) ?? null;
  const onDragEnd = (event: DragEndEvent): void => {
    const { active, over } = event;
    if (over && active.id !== over.id) editor.dropItem(Number(active.id), Number(over.id));
  };
  const onSearchKey = (event: React.KeyboardEvent<HTMLInputElement>): void => {
    const action = matchListSearchKey(event.nativeEvent);
    if (!action) return;
    event.preventDefault();
    event.stopPropagation();
    if (action === "moveDown") editor.moveResult(1);
    else if (action === "moveUp") editor.moveResult(-1);
    else if (action === "addEnd") editor.addResult("end");
    else if (action === "addAfter") editor.addResult("after");
    else if (e.query !== "") editor.setQuery("");
    else { event.currentTarget.blur(); }
  };
  const note = e.form?.kind === "note" ? e.form : null;

  return (
    <div className="flex flex-col gap-4" data-testid="list-editor" data-busy={e.busy > 0 ? "true" : "false"}>
      <div className="flex flex-wrap items-center gap-2">
        <Button type="button" disabled={!connected} onClick={() => { editor.openForm("new"); }}>Nueva lista</Button>
        <Button type="button" variant="outline" disabled={off} onClick={() => { editor.openForm("meta"); }}>Renombrar / fecha</Button>
        <Button type="button" variant="outline" disabled={off} onClick={() => { editor.openForm("duplicate"); }}>Duplicar</Button>
        <Button type="button" variant="outline" disabled={off} onClick={() => { editor.openForm("delete"); }}>Borrar</Button>
        {list && <span className="text-sm text-neutral-600">{list.name} · {formatDate(list.serviceDate)} · {items.length} elemento(s)</span>}
        {e.busy > 0 && <span data-testid="saving" className="rounded bg-neutral-800 px-2 py-0.5 text-xs font-semibold text-white">Guardando…</span>}
      </div>

      {view.leaveWarning && (
        <div role="alert" data-testid="leave-warning" className="flex flex-col gap-2 rounded-md border-2 border-red-700 bg-red-50 p-3 text-red-950">
          <p className="font-bold">No se pasó a EN VIVO: hay una edición pendiente.</p>
          <p className="text-sm">{view.leaveWarning}</p>
          <div className="flex gap-3">
            <Button variant="outline" onClick={() => { controller.dismissLeaveWarning(); }}>Seguir editando</Button>
            <Button variant="destructive" onClick={() => { controller.confirmLeave(); }}>Descartar y pasar a EN VIVO</Button>
          </div>
        </div>
      )}

      {e.message && (
        <div role="status" data-testid="edit-message" data-kind={e.message.kind} className={`flex items-start justify-between gap-3 rounded-md border-2 p-3 text-sm ${MESSAGE_CLASS[e.message.kind]}`}>
          <span>{e.message.text}</span>
          <span className="flex shrink-0 gap-3">
            {e.lastRemoved && <button type="button" className="underline" onClick={() => { editor.undo(); }}>Deshacer</button>}
            <button type="button" className="underline" onClick={() => { editor.dismissMessage(); }}>Cerrar</button>
          </span>
        </div>
      )}

      {e.form && e.form.kind !== "note" && <FormView form={e.form} editor={editor} disabled={!connected || e.busy > 0} count={items.length} />}

      <div className="grid grid-cols-1 gap-4 lg:grid-cols-[1fr_24rem]">
        <section aria-label="Elementos de la lista" className="flex flex-col gap-2">
          {!list ? (
            <p className="text-sm text-neutral-600">{view.lists.length === 0 ? "Todavía no hay listas: crea la primera con «Nueva lista»." : "Elige una lista arriba."}</p>
          ) : items.length === 0 ? (
            <p className="text-sm text-neutral-600">La lista está vacía. Busca una canción a la derecha, añade una nota o, desde la pestaña Biblia, un pasaje (Ctrl+Mayús+A).</p>
          ) : (
            <DndContext sensors={sensors} collisionDetection={closestCenter} onDragEnd={onDragEnd}>
              <SortableContext items={ids} strategy={verticalListSortingStrategy}>
                <ul className="flex flex-col gap-1" aria-label="Elementos (reordena arrastrando o con Alt+↑/↓)" data-testid="edit-items">
                  {items.map((item, index) => (
                    <Row key={item.id} item={item} index={index} total={items.length} selected={item.id === e.selectedId} disabled={!connected} bibles={e.bibles} editor={editor} />
                  ))}
                </ul>
              </SortableContext>
            </DndContext>
          )}
          <p className="text-xs text-neutral-500">↑/↓ selecciona · Alt+↑/↓ mueve · Supr quita · Ctrl+Z recupera · Ctrl+D duplica · / busca canciones · Ctrl+E vuelve a EN VIVO</p>
        </section>

        <aside className="flex flex-col gap-3" aria-label="Añadir a la lista">
          <section className="flex flex-col gap-2 rounded-md border p-3" data-testid="song-search">
            <h2 className="text-sm font-semibold">{replacing ? `Reemplazar «${replacing.title}»: elige una canción` : "Añadir una canción"}</h2>
            {replacing && <Button type="button" variant="outline" size="sm" className="w-fit" onClick={() => { editor.startReplace(null); }}>Cancelar el reemplazo</Button>}
            <input
              ref={searchRef}
              type="search"
              aria-label="Buscar una canción para añadir"
              placeholder="Título o palabras de la letra    ( / )"
              className="h-10 rounded-md border-2 px-2"
              value={e.query}
              disabled={off && !replacing}
              autoComplete="off"
              spellCheck={false}
              onChange={(ev) => { editor.setQuery(ev.target.value); }}
              onKeyDown={onSearchKey}
            />
            <p className="text-xs text-neutral-500">↑/↓ elige · Enter añade al final · Ctrl+Enter, después de la selección</p>
            <ul role="listbox" aria-label="Canciones" className="flex max-h-72 flex-col overflow-y-auto rounded border">
              {e.results.map((song, i) => (
                <li key={song.id} role="option" aria-selected={i === e.resultIndex} data-song-id={song.id} className={`flex items-center gap-2 border-b px-2 py-1 ${i === e.resultIndex ? "bg-blue-100" : ""}`}>
                  <span className="min-w-0 flex-1 truncate">{song.title}</span>
                  {replacing ? (
                    <Button type="button" size="sm" disabled={!connected} onClick={() => { editor.addSong(song.id, "end"); }}>Usar esta</Button>
                  ) : (
                    <>
                      <Button type="button" size="sm" variant="outline" disabled={off} aria-label={`Añadir ${song.title} al final`} onClick={() => { editor.addSong(song.id, "end"); }}>+ al final</Button>
                      <Button type="button" size="sm" variant="outline" disabled={off} aria-label={`Añadir ${song.title} después de la selección`} onClick={() => { editor.addSong(song.id, "after"); }}>+ después</Button>
                    </>
                  )}
                </li>
              ))}
              {e.results.length === 0 && <li className="p-2 text-sm text-neutral-500">{e.searching ? "Buscando…" : "No hay canciones."}</li>}
            </ul>
            {e.resultsTruncated && <p className="text-xs text-neutral-500">Hay más: escribe para afinar la búsqueda.</p>}
          </section>

          <section className="flex flex-col gap-2 rounded-md border p-3" data-testid="note-form">
            <h2 className="text-sm font-semibold">{note?.itemId != null ? "Editar la nota" : "Añadir una nota o separador"}</h2>
            {note ? (
              <>
                <input className="h-10 rounded border-2 px-2" aria-label="Título de la nota" placeholder="Ofrendas, Anuncios…" value={note.title} maxLength={200} onChange={(ev) => { editor.setField("title", ev.target.value); }} />
                <textarea className="min-h-16 rounded border-2 px-2 py-1 text-sm" aria-label="Texto de la nota (opcional)" placeholder="Recordatorio para quien opera (no se proyecta)" value={note.text} maxLength={500} onChange={(ev) => { editor.setField("text", ev.target.value); }} />
                <label className="flex cursor-pointer items-center gap-2 text-sm">
                  <input type="checkbox" aria-label="Limpiar la pantalla al activar" checked={note.clear} onChange={(ev) => { editor.setClear(ev.target.checked); }} />
                  Limpiar la pantalla al activar (por defecto NO)
                </label>
                <div className="flex gap-2">
                  <Button disabled={off || e.busy > 0} onClick={() => { editor.submitForm(); }}>{note.itemId !== null ? "Guardar la nota" : "Añadir después de la selección"}</Button>
                  <Button variant="outline" onClick={() => { editor.closeForm(); }}>Cancelar</Button>
                </div>
              </>
            ) : (
              <Button type="button" variant="outline" disabled={off} onClick={() => { editor.openForm("note"); }}>+ Nota o separador</Button>
            )}
            <p className="text-xs text-neutral-500">Una nota no se proyecta; en EN VIVO se salta. Solo si marcas «limpiar» quita el texto de la pantalla al activarla.</p>
          </section>
        </aside>
      </div>
    </div>
  );
}
