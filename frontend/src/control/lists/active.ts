import type { OutputStateChangedPayload, PlaylistItemDto, PlaylistSummaryDto } from "@/ipc/protocol";
import { visibilityStatus, type LiveStatus } from "../bible/liveStatus";

/**
 * Funciones PURAS de la pestaña «Lista»: qué elemento está en pantalla (derivado del `origin` que confirma el backend, NUNCA de la igualdad de texto: la misma canción
 * dos veces o un panel recargado no lo confunden), qué dice la insignia y cómo se salta entre elementos.
 */

/** El elemento de la lista abierta que está en pantalla y la diapositiva (o página) dentro de él, desde 0. */
export interface ActiveItem {
  itemId: number;
  position: number;
}

export interface OpenListLike {
  id: number;
  items: readonly Pick<PlaylistItemDto, "id" | "itemType">[];
}

/**
 * El elemento activo, a partir del `origin` del slide del estado CONFIRMADO. Nulo si no hay slide, si no viene de una lista, si es de OTRA lista o si el elemento ya no
 * está en la lista abierta (origen de un elemento inexistente o quitado): en esos casos nada de esta pestaña está «en vivo».
 */
export function activeFromOutput(output: OutputStateChangedPayload | null, list: OpenListLike | null): ActiveItem | null {
  const origin = output?.state.content.slide?.origin;
  if (!origin || !list || origin.playlistId !== list.id) return null;
  const item = list.items.find((i) => i.id === origin.itemId);
  if (!item || item.itemType !== origin.itemType) return null;
  return { itemId: item.id, position: origin.position };
}

/** EN VIVO si el elemento activo se ve (ventana abierta, sin pausa, modo Live); «OCULTO · …» con el motivo si no; LISTO si nada de la lista está en pantalla. */
export function listLiveStatus(output: OutputStateChangedPayload | null, active: ActiveItem | null): LiveStatus {
  if (!output || !active) return { kind: "ready" };
  return visibilityStatus(output);
}

/** Texto de la insignia. */
export function badgeText(status: LiveStatus): string {
  return status.kind === "live" ? "EN VIVO" : status.kind === "hidden" ? `OCULTO · ${status.why}` : "LISTO";
}

type Steppable = Pick<PlaylistItemDto, "id" | "itemType" | "title" | "isMissing" | "projectable">;

/** Por qué un elemento NO se proyecta, o null si se proyecta. Es lo que se avisa al saltarlo. */
export function whyNotProjectable(item: Steppable): string | null {
  if (item.projectable) return null;
  if (item.isMissing) return `Falta la canción «${item.title}»`;
  if (item.itemType === "passage") return `No se puede proyectar el pasaje «${item.title}» (Biblia no instalada o sin ese pasaje)`;
  return null;   // una nota no se proyecta: se pasa de largo sin avisar
}

export interface StepResult {
  /** Posición del elemento al que se llega, o null si no hay más en esa dirección. */
  index: number | null;
  /** Los elementos que se saltaron y merecen un aviso (faltantes y pasajes sin resolver; las notas se saltan sin avisar). */
  skipped: string[];
}

/** El siguiente (`direction` = 1) o anterior (−1) elemento PROYECTABLE a partir de `from` (nulo = antes del primero / después del último). Espejo de `PlaylistNavigation.Step` del backend. */
export function stepItem(items: readonly Steppable[], from: number | null, direction: 1 | -1): StepResult {
  const skipped: string[] = [];
  let i = from ?? (direction > 0 ? -1 : items.length);
  for (i += direction; i >= 0 && i < items.length; i += direction) {
    const item = items[i];
    if (!item) break;
    if (item.projectable) return { index: i, skipped };
    const why = whyNotProjectable(item);
    if (why) skipped.push(why);
  }
  return { index: null, skipped };
}

/**
 * La lista que se abre sola: la recordada (si sigue existiendo); si no, la de HOY; si no, la próxima (fecha futura más cercana); si no, la más reciente ya pasada; y
 * si solo hay plantillas, la primera. `today` es `AAAA-MM-DD` en hora local.
 */
export function pickDefaultList(lists: readonly PlaylistSummaryDto[], today: string, remembered: number | null): number | null {
  if (remembered !== null && lists.some((l) => l.id === remembered)) return remembered;
  const dated = lists.filter((l) => l.serviceDate !== null);
  const todays = dated.find((l) => l.serviceDate === today);
  if (todays) return todays.id;
  const upcoming = dated.filter((l) => (l.serviceDate ?? "") > today).sort((a, b) => (a.serviceDate ?? "").localeCompare(b.serviceDate ?? ""))[0];
  if (upcoming) return upcoming.id;
  const past = dated.sort((a, b) => (b.serviceDate ?? "").localeCompare(a.serviceDate ?? ""))[0];
  if (past) return past.id;
  return lists[0]?.id ?? null;
}

/** Fecha local `AAAA-MM-DD` (no UTC: el culto es a la hora de la iglesia). */
export function localDate(now: Date): string {
  const pad = (n: number): string => String(n).padStart(2, "0");
  return `${String(now.getFullYear())}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`;
}
