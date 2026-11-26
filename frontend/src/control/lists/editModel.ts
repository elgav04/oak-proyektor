import type { BibleVerseDto, PlaylistItemDto, PlaylistNewItemDto } from "@/ipc/protocol";
import { formatReference } from "../bible/slide";

/** Funciones PURAS del modo EDITAR: orden mostrado, destino de un arrastre, fechas por defecto, y el pasaje de la Biblia que se añade. */

/** El orden que se MUESTRA: el de la lista, o el optimista (`localOrder`) mientras hay movimientos sin confirmar. Los ids que ya no están se omiten; los nuevos van al final. */
export function orderedItems<T extends Pick<PlaylistItemDto, "id">>(items: readonly T[], localOrder: readonly number[] | null, hidden: readonly number[] = []): T[] {
  const visible = hidden.length === 0 ? items : items.filter((i) => !hidden.includes(i.id));
  if (!localOrder) return [...visible];
  const byId = new Map(visible.map((i) => [i.id, i]));
  const out: T[] = [];
  for (const id of localOrder) {
    const item = byId.get(id);
    if (item) { out.push(item); byId.delete(id); }
  }
  for (const item of visible) if (byId.has(item.id)) out.push(item);
  return out;
}

/** Mueve el id a `toIndex` (acotado) y devuelve el orden nuevo; el mismo arreglo si no cambia. */
export function movedOrder(ids: readonly number[], itemId: number, toIndex: number): number[] {
  const from = ids.indexOf(itemId);
  if (from < 0) return [...ids];
  const to = Math.max(0, Math.min(ids.length - 1, toIndex));
  const next = ids.filter((id) => id !== itemId);
  next.splice(to, 0, itemId);
  return next;
}

/** Destino (posición) de un arrastre de `activeId` sobre `overId`, o null si no hay cambio. Es lo que se manda en `playlist.moveItem.toIndex`. */
export function dropIndex(ids: readonly number[], activeId: number, overId: number): number | null {
  const from = ids.indexOf(activeId);
  const to = ids.indexOf(overId);
  return from < 0 || to < 0 || from === to ? null : to;
}

const pad = (n: number): string => String(n).padStart(2, "0");

/** `AAAA-MM-DD` + `days` (aritmética en UTC: el horario de verano no la mueve). */
export function addDays(date: string, days: number): string {
  const [y, m, d] = date.split("-").map(Number);
  const t = new Date(Date.UTC(y ?? 1970, (m ?? 1) - 1, d ?? 1) + days * 86_400_000);
  return `${String(t.getUTCFullYear())}-${pad(t.getUTCMonth() + 1)}-${pad(t.getUTCDate())}`;
}

/** El domingo SIGUIENTE a `date` (si `date` es domingo, el de la semana que viene). */
export function nextSunday(date: string): string {
  const [y, m, d] = date.split("-").map(Number);
  const dow = new Date(Date.UTC(y ?? 1970, (m ?? 1) - 1, d ?? 1)).getUTCDay();
  return addDays(date, dow === 0 ? 7 : 7 - dow);
}

/** Fecha propuesta para la copia: una semana después de la original; sin fecha (plantilla), el próximo domingo desde hoy. */
export function suggestedCopyDate(sourceDate: string | null, today: string): string {
  return sourceDate ? addDays(sourceDate, 7) : nextSunday(today);
}

export type PassageForList = { ok: true; item: PlaylistNewItemDto; label: string } | { ok: false; message: string };

/**
 * El pasaje seleccionado en la pestaña de Biblia como elemento de la lista: referencia CANÓNICA (libro 1 a 66, capítulo, versículos) + la Biblia preferida. Un pasaje que
 * cruza de capítulo no se puede guardar como un solo elemento (el backend guarda un capítulo por elemento): se avisa y no se añade nada. El título lleva solo la referencia.
 */
export function passageForList(
  passage: { bible: string; verses: readonly BibleVerseDto[] } | null, anchor: number, active: number, shortName: string,
): PassageForList {
  if (!passage || passage.verses.length === 0) return { ok: false, message: "Elige primero un pasaje en la Biblia." };
  const verses = passage.verses.slice(Math.min(anchor, active), Math.max(anchor, active) + 1);
  const first = verses[0];
  if (!first) return { ok: false, message: "Elige primero un pasaje en la Biblia." };
  if (verses.some((v) => v.bookNumber !== first.bookNumber || v.chapter !== first.chapter)) {
    return { ok: false, message: "La selección cruza de capítulo: añade cada capítulo por separado." };
  }
  const numbers = verses.map((v) => v.verseNumber);
  const start = Math.min(...numbers);
  const end = Math.max(...numbers);
  const label = `${formatReference(verses)} · ${shortName}`;
  return {
    ok: true,
    label,
    item: {
      itemType: "passage", songId: null, bookNumber: first.bookNumber, chapter: first.chapter, verseStart: start, verseEnd: end > start ? end : null,
      bibleAbbreviation: passage.bible, title: label, note: null, clearOnActivate: false,
    },
  };
}

/** Un elemento que se quitó, como el elemento nuevo que lo vuelve a poner («Deshacer»). Null si no se puede reconstruir (una canción faltante no se puede volver a añadir). */
export function itemAsNew(item: PlaylistItemDto): PlaylistNewItemDto | null {
  if (item.itemType === "song") return item.songId === null || item.isMissing ? null : { itemType: "song", songId: item.songId, bookNumber: null, chapter: null, verseStart: null, verseEnd: null, bibleAbbreviation: null, title: null, note: null, clearOnActivate: false };
  if (item.itemType === "passage") return { itemType: "passage", songId: null, bookNumber: item.bookNumber, chapter: item.chapter, verseStart: item.verseStart, verseEnd: item.verseEnd, bibleAbbreviation: item.bibleAbbreviation, title: item.title, note: null, clearOnActivate: false };
  if (item.itemType === "note") return { itemType: "note", songId: null, bookNumber: null, chapter: null, verseStart: null, verseEnd: null, bibleAbbreviation: null, title: item.title, note: item.note, clearOnActivate: item.clearOnActivate };
  return null;
}

/** Mensaje en español para un error del backend de listas (el panel nunca muestra un código crudo). */
export function describeEditError(code: string, message: string): { kind: "conflict" | "error"; text: string } {
  switch (code) {
    case "playlist.conflict":
      return { kind: "conflict", text: "La lista cambió desde que la abriste (otra ventana o una edición anterior), así que esa acción NO se aplicó. Ya se volvió a leer la lista; lo que estabas escribiendo sigue aquí. Repite la acción si todavía la quieres." };
    case "playlist.itemNotFound":
      return { kind: "conflict", text: "Ese elemento ya no está en la lista (otra ventana lo quitó). Se volvió a leer la lista." };
    case "playlist.notFound":
      return { kind: "conflict", text: "Esa lista ya no existe (otra ventana la borró). Se volvió a leer." };
    case "playlist.busy":
      return { kind: "error", text: "La base de datos está ocupada (por ejemplo, importando una Biblia). No se cambió nada; inténtalo de nuevo en unos segundos." };
    case "playlist.full":
      return { kind: "error", text: message };
    case "timeout":
      return { kind: "error", text: "El programa no confirmó el cambio a tiempo. Se volvió a leer la lista: comprueba si se aplicó antes de repetirlo." };
    default:
      return { kind: "error", text: message || "No se pudo aplicar el cambio." };
  }
}
