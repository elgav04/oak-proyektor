import { DndContext, KeyboardSensor, PointerSensor, closestCenter, useSensor, useSensors, type DragEndEvent } from "@dnd-kit/core";
import { SortableContext, sortableKeyboardCoordinates, useSortable, verticalListSortingStrategy } from "@dnd-kit/sortable";
import { CSS } from "@dnd-kit/utilities";
import { Button } from "@/components/ui/button";
import { occurrences, unusedSections, type EditorSection, type OrderEntry } from "./textModel";

export interface OrderListProps {
  order: readonly OrderEntry[];
  sections: readonly EditorSection[];
  selected: number;
  disabled: boolean;
  onSelect: (index: number) => void;
  onMove: (from: number, to: number) => void;
  onRemove: (index: number) => void;
  onRepeat: (index: number) => void;
  onInsert: (uid: string) => void;
}

/** Identificador de una fila: la sección más cuántas veces apareció ya (el mismo coro puede estar varias veces en el orden). */
function rowIds(order: readonly OrderEntry[]): string[] {
  const seen = new Map<string, number>();
  return order.map((e) => {
    const n = seen.get(e.uid) ?? 0;
    seen.set(e.uid, n + 1);
    return `${e.uid}#${String(n)}`;
  });
}

function Row({
  id, index, entry, section, total, selected, disabled, count, onSelect, onMove, onRemove, onRepeat,
}: {
  id: string; index: number; entry: OrderEntry; section: EditorSection | undefined; total: number; selected: boolean; disabled: boolean; count: number;
  onSelect: (index: number) => void; onMove: (from: number, to: number) => void; onRemove: (index: number) => void; onRepeat: (index: number) => void;
}) {
  const { attributes, listeners, setNodeRef, transform, transition, isDragging } = useSortable({ id, disabled });
  const missing = section === undefined;
  return (
    <li
      ref={setNodeRef}
      style={{ transform: CSS.Transform.toString(transform), transition }}
      data-order-row={String(index)}
      tabIndex={0}
      aria-current={selected ? "true" : undefined}
      aria-label={`${String(index + 1)}. ${entry.label}${missing ? " (la sección ya no existe)" : ""}`}
      className={`flex items-center gap-2 rounded border p-2 outline-none focus:ring-2 focus:ring-blue-600 ${missing ? "border-red-600 bg-red-50 text-red-900" : selected ? "border-blue-600 bg-blue-50" : "bg-white"} ${isDragging ? "opacity-60" : ""}`}
      onFocus={() => { onSelect(index); }}
      onClick={() => { onSelect(index); }}
    >
      <button type="button" aria-label={`Arrastrar ${entry.label}`} className="cursor-grab rounded px-1 text-lg leading-none text-neutral-500 hover:bg-neutral-100" {...attributes} {...listeners}>⠿</button>
      <span className="w-6 text-right text-sm tabular-nums text-neutral-500">{index + 1}</span>
      <span className="flex-1 font-semibold">{entry.label}{missing && <span className="ml-2 text-xs font-normal">(la sección ya no existe: quítalo o vuelve a escribir su etiqueta)</span>}</span>
      {count > 1 && <span className="rounded-full bg-neutral-200 px-2 text-xs font-bold" title="Veces que aparece en el orden: el texto es uno solo">×{count}</span>}
      <Button type="button" variant="outline" size="sm" disabled={disabled || index === 0} aria-label="Subir" onClick={() => { onMove(index, index - 1); }}>↑</Button>
      <Button type="button" variant="outline" size="sm" disabled={disabled || index === total - 1} aria-label="Bajar" onClick={() => { onMove(index, index + 1); }}>↓</Button>
      <Button type="button" variant="outline" size="sm" disabled={disabled || missing} aria-label={`Repetir ${entry.label}`} title="Repetir la sección justo después (no duplica el texto)" onClick={() => { onRepeat(index); }}>Repetir</Button>
      <Button type="button" variant="outline" size="sm" disabled={disabled} aria-label={`Quitar ${entry.label} del orden`} title="Quitar del orden (la sección y su texto se conservan)" onClick={() => { onRemove(index); }}>✕</Button>
    </li>
  );
}

/**
 * Orden de reproducción: lista de secciones que se reordena arrastrando (`@dnd-kit`, con teclado: foco en el asa, Espacio y flechas) o con los botones y los atajos
 * de la fila (Alt+↑/↓ mover, Supr quitar, Ctrl+D repetir). Repetir una sección NO duplica su texto: es una referencia más a la misma sección.
 */
export function OrderList(props: OrderListProps) {
  const { order, sections, selected, disabled } = props;
  const sensors = useSensors(useSensor(PointerSensor, { activationConstraint: { distance: 6 } }), useSensor(KeyboardSensor, { coordinateGetter: sortableKeyboardCoordinates }));
  const ids = rowIds(order);
  const unused = unusedSections(order, sections);
  const onDragEnd = (event: DragEndEvent): void => {
    const { active, over } = event;
    if (!over || active.id === over.id) return;
    const from = ids.indexOf(String(active.id));
    const to = ids.indexOf(String(over.id));
    if (from >= 0 && to >= 0) props.onMove(from, to);
  };
  return (
    <div className="flex flex-col gap-2">
      {order.length === 0 ? (
        <p className="text-sm text-neutral-500">El orden está vacío. Añade secciones con los botones de abajo.</p>
      ) : (
        <DndContext sensors={sensors} collisionDetection={closestCenter} onDragEnd={onDragEnd}>
          <SortableContext items={ids} strategy={verticalListSortingStrategy}>
            <ul className="flex flex-col gap-1" aria-label="Orden de reproducción">
              {order.map((entry, index) => (
                <Row
                  key={ids[index]}
                  id={ids[index] ?? String(index)}
                  index={index}
                  entry={entry}
                  section={sections.find((s) => s.uid === entry.uid)}
                  total={order.length}
                  selected={index === selected}
                  disabled={disabled}
                  count={occurrences(entry.uid, order)}
                  onSelect={props.onSelect}
                  onMove={props.onMove}
                  onRemove={props.onRemove}
                  onRepeat={props.onRepeat}
                />
              ))}
            </ul>
          </SortableContext>
        </DndContext>
      )}
      <div className="flex flex-wrap items-center gap-2" aria-label="Añadir una sección al orden">
        <span className="text-sm text-neutral-600">Añadir al orden:</span>
        {sections.length === 0 && <span className="text-sm text-neutral-500">(todavía no hay secciones)</span>}
        {sections.map((s) => (
          <Button key={s.uid} type="button" variant="outline" size="sm" disabled={disabled} onClick={() => { props.onInsert(s.uid); }}>+ {s.label}{unused.includes(s) ? " (sin usar)" : ""}</Button>
        ))}
      </div>
    </div>
  );
}
