import type { SongBackgroundDto, SongSavePayload, SongSectionDto } from "@/ipc/protocol";

/**
 * Modelo de EDICIÓN de una canción (solo estado de VISTA: el contenido manda el backend). Funciones puras, sin DOM ni IPC.
 *
 * El texto del editor define las SECCIONES (cada una una vez, `[Etiqueta]` + cuerpo); el ORDEN de reproducción es una lista aparte de referencias a secciones
 * (así repetir el coro no duplica su texto). Cada sección lleva una identidad de la vista (`uid`) que sobrevive a las ediciones del texto: al volver a interpretarlo se
 * reconcilian las secciones nuevas con las anteriores (por etiqueta y, si se renombró una, por posición) para que el orden no se rompa.
 */

export interface EditorSection {
  /** Identidad estable DE LA VISTA (no la del backend). */
  uid: string;
  /** Id de la sección en la BD (null = todavía no guardada). */
  serverId: number | null;
  kind: string;
  label: string;
  text: string;
}

/** Un elemento del orden. `label` es la última etiqueta conocida (para mostrar una referencia huérfana). */
export interface OrderEntry {
  uid: string;
  label: string;
}

/** Misma clave de comparación que `SectionAliases.Key` del backend: minúsculas, sin tildes y solo letras y dígitos. */
export function labelKey(label: string): string {
  return label.normalize("NFD").replace(/\p{M}/gu, "").toLowerCase().replace(/[^\p{L}\p{N}]/gu, "");
}

/** El texto del editor: cada sección UNA vez como `[Etiqueta]` + cuerpo, separadas por una línea en blanco. Sin línea «Orden:» (el orden se edita aparte). */
export function formatSectionsText(sections: readonly { label: string; text: string }[]): string {
  return sections.map((s) => `[${s.label}]\n${s.text}`).join("\n\n");
}

export function isResolved(entry: OrderEntry, sections: readonly EditorSection[]): boolean {
  return sections.some((s) => s.uid === entry.uid);
}

/** ¿El orden es «cada sección una vez, en su posición» (y todas existen)? */
export function isNaturalOrder(order: readonly OrderEntry[], sections: readonly EditorSection[]): boolean {
  return order.length === sections.length && order.every((e, i) => e.uid === sections[i]?.uid);
}

export function naturalOrder(sections: readonly EditorSection[]): OrderEntry[] {
  return sections.map((s) => ({ uid: s.uid, label: s.label }));
}

/**
 * Asigna a las secciones recién interpretadas la identidad (y el id del servidor) de las anteriores. 1.º por etiqueta (clave); 2.º si quedan sin pareja el MISMO número
 * de nuevas y de antiguas (se renombró alguna), por posición. Lo que no se empareja es una sección nueva.
 */
export function reconcileSections(parsed: readonly SongSectionDto[], previous: readonly EditorSection[], newUid: () => string): EditorSection[] {
  const result: (EditorSection | null)[] = parsed.map(() => null);
  const used = new Set<number>();
  const prevByKey = new Map<string, number>();
  previous.forEach((p, i) => { if (!prevByKey.has(labelKey(p.label))) prevByKey.set(labelKey(p.label), i); });

  parsed.forEach((p, i) => {
    const j = prevByKey.get(labelKey(p.label));
    if (j === undefined || used.has(j)) return;
    used.add(j);
    const prev = previous[j];
    if (prev) result[i] = { uid: prev.uid, serverId: prev.serverId, kind: p.kind, label: p.label, text: p.text };
  });

  const freeParsed = parsed.map((_, i) => i).filter((i) => result[i] === null);
  const freePrevious = previous.map((_, j) => j).filter((j) => !used.has(j));
  if (freeParsed.length > 0 && freeParsed.length === freePrevious.length) {
    freeParsed.forEach((i, n) => {
      const p = parsed[i];
      const prev = previous[freePrevious[n] ?? -1];
      if (p && prev) result[i] = { uid: prev.uid, serverId: prev.serverId, kind: p.kind, label: p.label, text: p.text };
    });
  }
  return result.map((r, i) => {
    if (r) return r;
    const p = parsed[i];
    return { uid: newUid(), serverId: null, kind: p?.kind ?? "other", label: p?.label ?? "", text: p?.text ?? "" };
  });
}

/**
 * El orden tras volver a interpretar el texto. Si era el natural, lo sigue siendo (una sección nueva entra donde está en el texto). Si no, cada elemento
 * conserva su sección (por identidad) o, si su sección desapareció, se reengancha a la que tenga ahora la misma etiqueta; lo que no se puede reenganchar queda como
 * referencia SIN RESOLVER (se ve en rojo y impide guardar).
 */
export function reconcileOrder(order: readonly OrderEntry[], before: readonly EditorSection[], after: readonly EditorSection[]): OrderEntry[] {
  if (isNaturalOrder(order, before)) return naturalOrder(after);
  const byUid = new Map(after.map((s) => [s.uid, s]));
  const byKey = new Map(after.map((s) => [labelKey(s.label), s]));
  return order.map((entry) => {
    const found = byUid.get(entry.uid) ?? byKey.get(labelKey(entry.label));
    return found ? { uid: found.uid, label: found.label } : entry;
  });
}

/** Índices del orden en la lista de secciones; null si alguna referencia no se puede resolver. */
export function orderIndexes(order: readonly OrderEntry[], sections: readonly EditorSection[]): number[] | null {
  const out: number[] = [];
  for (const entry of order) {
    const i = sections.findIndex((s) => s.uid === entry.uid);
    if (i < 0) return null;
    out.push(i);
  }
  return out;
}

export function unresolvedOrder(order: readonly OrderEntry[], sections: readonly EditorSection[]): number[] {
  return order.map((e, i) => (isResolved(e, sections) ? -1 : i)).filter((i) => i >= 0);
}

/** Secciones que ningún elemento del orden usa. */
export function unusedSections(order: readonly OrderEntry[], sections: readonly EditorSection[]): EditorSection[] {
  const used = new Set(order.map((e) => e.uid));
  return sections.filter((s) => !used.has(s.uid));
}

/** Cuántas veces aparece una sección en el orden. */
export function occurrences(uid: string, order: readonly OrderEntry[]): number {
  return order.filter((e) => e.uid === uid).length;
}

// ───────────── lo editable, «sin guardar» y el botón de guardar ─────────────

export interface EditableSong {
  title: string;
  author: string;
  copyright: string;
  ccli: string;
  notes: string;
  background: SongBackgroundDto | null;
  sections: readonly EditorSection[];
  order: readonly OrderEntry[];
}

/** Huella de lo que se guardaría: dos canciones con la misma huella son iguales para el backend. Sirve para el estado «sin guardar». */
export function fingerprint(song: EditableSong): string {
  const indexes = orderIndexes(song.order, song.sections);
  return JSON.stringify([
    song.title, song.author, song.copyright, song.ccli, song.notes, song.background,
    song.sections.map((s) => [s.label, s.text]),
    indexes ?? song.order.map((e) => `?${e.label}`),
  ]);
}

export interface RepeatedEdit {
  label: string;
  count: number;
}

/**
 * Las secciones EDITADAS (texto o etiqueta cambiados, o nuevas) que el orden repite: guardar las cambia en todas sus repeticiones («Coro ×3»). Se compara con lo que
 * había al abrir/guardar por última vez (por id de servidor).
 */
export function repeatedEdits(current: readonly EditorSection[], order: readonly OrderEntry[], baseline: readonly EditorSection[]): RepeatedEdit[] {
  const before = new Map(baseline.filter((s) => s.serverId !== null).map((s) => [s.serverId as number, s]));
  const edited: RepeatedEdit[] = [];
  for (const s of current) {
    const old = s.serverId === null ? undefined : before.get(s.serverId);
    const changed = !old || old.text !== s.text || old.label !== s.label;
    const count = occurrences(s.uid, order);
    if (changed && count > 1) edited.push({ label: s.label, count });
  }
  return edited;
}

/** «Guardar en la canción» / «Guardar en la canción (Coro ×3)» / «Crear la canción». */
export function saveButtonLabel(isNew: boolean, repeated: readonly RepeatedEdit[]): string {
  if (isNew) return "Crear la canción";
  if (repeated.length === 0) return "Guardar en la canción";
  const shown = repeated.slice(0, 3).map((r) => `${r.label} ×${String(r.count)}`).join(", ");
  return `Guardar en la canción (${shown}${repeated.length > 3 ? ", …" : ""})`;
}

/** Cuerpo de `song.save` (el panel manda las secciones con su id del servidor, o null si son nuevas, y el orden como posiciones). */
export function buildSavePayload(
  songId: number | null, expectedRevision: number | null, song: EditableSong, reason: "save" | "import",
): SongSavePayload | null {
  const indexes = orderIndexes(song.order, song.sections);
  if (!indexes) return null;
  return {
    id: songId,
    expectedRevision: songId === null ? null : expectedRevision,
    title: song.title,
    author: song.author.trim() === "" ? null : song.author,
    copyright: song.copyright.trim() === "" ? null : song.copyright,
    ccliNumber: song.ccli.trim() === "" ? null : song.ccli,
    notes: song.notes.trim() === "" ? null : song.notes,
    background: song.background,
    sections: song.sections.map((s): SongSectionDto => ({ id: s.serverId, kind: s.kind, label: s.label, text: s.text })),
    arrangement: indexes,
    reason,
  };
}
