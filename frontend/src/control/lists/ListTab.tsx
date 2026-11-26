import { useEffect } from "react";
import { useStore } from "zustand";
import { Button } from "@/components/ui/button";
import type { OutputStateChangedPayload, PlaylistItemDto } from "@/ipc/protocol";
import { LIST_SHORTCUTS, matchListShortcut } from "../shortcuts";
import { activeFromOutput, badgeText, listLiveStatus, stepItem } from "./active";
import { BlocksCanvas } from "./BlocksCanvas";
import type { ListController } from "./controller";

const BADGE_CLASS = { live: "bg-red-600 text-white", hidden: "bg-amber-500 text-black", ready: "bg-neutral-300 text-neutral-900" } as const;
const TYPE_LABEL: Record<string, string> = { song: "Canción", passage: "Pasaje", note: "Nota" };
/** Lo máximo que se pinta de golpe de la tira del elemento (cada una es un lienzo escalado). */
const MAX_RENDERED_SLIDES = 120;

function formatDate(date: string | null): string {
  if (!date) return "plantilla";
  const [y, m, d] = date.split("-");
  return `${d ?? ""}/${m ?? ""}/${y ?? ""}`;
}

/** Por qué una fila no se puede proyectar (para marcarla), o null. */
function rowProblem(item: PlaylistItemDto): string | null {
  if (item.isMissing) return `Falta: ${item.title}`;
  if (item.itemType === "passage" && item.resolution === "bibleNotInstalled") return `Biblia no instalada: ${item.bibleAbbreviation ?? ""}${item.offeredBible ? ` (la predeterminada, ${item.offeredBible}, sí tiene el pasaje: cámbiala en el modo EDITAR)` : ""}`;
  if (item.itemType === "passage" && item.resolution === "passageNotFound") return `Pasaje no encontrado en ${item.bibleAbbreviation ?? ""}`;
  return null;
}

/**
 * Pestaña «Lista», modo EN VIVO: la lista del culto con el elemento ACTIVO (derivado del `origin` que confirma el backend) y el cursor, las teclas del mapa único
 * (`../shortcuts.ts`) y la miniatura del siguiente elemento. El modo en vivo NO dibuja ningún control de edición (nada que mueva la lista por error); Ctrl+E alterna.
 */
export function ListTab({ controller, connected, output = null }: { controller: ListController; connected: boolean; output?: OutputStateChangedPayload | null }) {
  const s = useStore(controller.store);

  useEffect(() => {
    void controller.onShow();
  }, [controller]);

  // Teclas de la pestaña (mapa único). En ráfaga: las flechas de diapositiva se repiten (el envío agrupa y gana la última); el resto no repite.
  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent): void => {
      const target = event.target instanceof Element ? (event.target as HTMLElement) : null;
      const action = matchListShortcut(event, target, { mode: controller.store.getState().mode });
      if (!action) return;
      event.preventDefault();
      if (event.repeat && action !== "slideNext" && action !== "slidePrev" && action !== "cursorNext" && action !== "cursorPrev") return;
      switch (action) {
        case "cursorPrev": controller.moveCursor(-1); break;
        case "cursorNext": controller.moveCursor(1); break;
        case "activate": controller.activate(); break;
        case "slideNext": controller.nextSlide(); break;
        case "slidePrev": controller.prevSlide(); break;
        case "itemNext": controller.nextItem(); break;
        case "itemPrev": controller.prevItem(); break;
        case "clear": controller.clear(); break;
        case "toggleMode": controller.toggleMode(); break;
      }
    };
    window.addEventListener("keydown", onKeyDown);
    return () => { window.removeEventListener("keydown", onKeyDown); };
  }, [controller]);

  const list = s.list;
  const items = list?.items ?? [];
  const active = activeFromOutput(output, list);
  const status = listLiveStatus(output, active);
  const badge = badgeText(status);
  const activePosition = active?.position;
  const cursorItem = items[s.cursor] ?? null;
  const activeIndex = active ? items.findIndex((i) => i.id === active.itemId) : -1;
  const focusItem = (activeIndex >= 0 ? items[activeIndex] : null) ?? cursorItem;
  const focusState = focusItem ? s.contents[focusItem.id] : undefined;
  const focusContent = focusState?.status === "ready" ? focusState.content : null;
  const upcomingIndex = stepItem(items, activeIndex >= 0 ? activeIndex : s.cursor - 1, 1).index;
  const upcoming = upcomingIndex === null ? null : items[upcomingIndex];
  const upcomingState = upcoming ? s.contents[upcoming.id] : undefined;
  const upcomingContent = upcomingState?.status === "ready" ? upcomingState.content : null;
  const onLastSlide = active !== null && focusContent !== null && focusItem?.id === active.itemId && active.position >= focusContent.slides.length - 1;
  const showEnd = s.atEnd && onLastSlide;
  const missing = items.filter((i) => i.isMissing);
  const modeChip = s.mode === "live" ? "Modo EN VIVO · Ctrl+E para editar" : "Modo EDITAR · Ctrl+E para volver a EN VIVO";

  if (s.error && !list && s.lists.length === 0) {
    return (
      <div className="flex flex-col gap-3">
        <p role="alert" className="rounded-md border-2 border-red-700 bg-red-50 p-3 text-red-950">{s.error}</p>
        <Button className="w-fit" disabled={!connected} onClick={() => { void controller.onShow(); }}>Reintentar</Button>
      </div>
    );
  }

  return (
    <div className="flex flex-col gap-4" data-testid="list-tab" data-mode={s.mode}>
      <div className="flex flex-wrap items-center gap-3">
        <label className="flex items-center gap-2 text-sm font-semibold">
          Lista
          <select
            className="rounded border px-2 py-1 text-base font-normal"
            value={s.openId ?? ""}
            disabled={!connected || s.lists.length === 0}
            aria-label="Lista del culto"
            onChange={(e) => { const id = Number(e.target.value); if (id > 0) void controller.openList(id); e.currentTarget.blur(); }}
          >
            {s.openId === null && <option value="">{s.lists.length === 0 ? "No hay listas" : "Elige una lista…"}</option>}
            {s.lists.map((l) => (
              <option key={l.id} value={l.id}>{l.name} · {formatDate(l.serviceDate)}{l.missingCount > 0 ? ` · ${String(l.missingCount)} faltan` : ""}</option>
            ))}
          </select>
        </label>
        <span data-testid="mode-chip" className={`rounded px-2 py-1 text-xs font-semibold ${s.mode === "live" ? "bg-neutral-800 text-white" : "bg-blue-700 text-white"}`}>{modeChip}</span>
        <span className="flex-1" />
        <span data-testid="live-badge" data-status={status.kind} className={`rounded px-3 py-1 text-sm font-bold ${BADGE_CLASS[status.kind]}`}>{badge}</span>
        {s.mode === "live" && (
          <Button variant="outline" disabled={!connected} onClick={() => { controller.clear(); }}>Quitar texto (Esc)</Button>
        )}
      </div>

      {s.error && list && <p role="alert" className="text-sm text-red-700">{s.error}</p>}

      {s.notice && (
        <div role="status" data-testid="notice" className="flex items-start justify-between gap-3 rounded-md border border-amber-600 bg-amber-100 p-3 text-sm text-amber-950">
          <span>{s.notice}</span>
          <button type="button" className="shrink-0 underline" onClick={() => { controller.dismissNotice(); }}>Cerrar</button>
        </div>
      )}

      {s.lists.length === 0 && s.loaded && (
        <p className="text-sm text-neutral-600">Todavía no hay listas de culto. Se crean y arman en el modo EDITAR (próxima fase).</p>
      )}

      {missing.length > 0 && (
        <div role="alert" data-testid="missing-banner" className="rounded-md border-2 border-red-700 bg-red-50 p-3 text-sm text-red-950">
          <p className="font-bold">{missing.length === 1 ? "Falta 1 elemento de esta lista" : `Faltan ${String(missing.length)} elementos de esta lista`} (la canción se borró):</p>
          <ul className="list-disc pl-5">{missing.map((m) => <li key={m.id}>{m.title}</li>)}</ul>
          <p className="mt-1">«Siguiente» los salta; siguen en la lista hasta que los quites en el modo EDITAR.</p>
        </div>
      )}

      {s.mode === "edit" ? (
        <div data-testid="edit-placeholder" className="rounded-md border-2 border-blue-700 bg-blue-50 p-4 text-blue-950">
          <p className="font-bold">Modo EDITAR</p>
          <p className="text-sm">Aquí se armará la lista (añadir, mover, quitar, duplicar). Esa edición llega en la siguiente fase; mientras tanto, desde aquí NO se proyecta nada y la lista no se puede modificar. Pulsa Ctrl+E para volver al modo EN VIVO.</p>
          <Button className="mt-3" variant="outline" onClick={() => { controller.setMode("live"); }}>Volver al modo EN VIVO</Button>
        </div>
      ) : (
        <>
          {list ? (
            <ol aria-label="Elementos de la lista" role="listbox" className="flex flex-col gap-1" data-testid="items">
              {items.map((item, index) => {
                const problem = rowProblem(item);
                const isActive = active?.itemId === item.id;
                const isCursor = index === s.cursor;
                return (
                  <li
                    key={item.id}
                    role="option"
                    aria-selected={isCursor}
                    aria-current={isActive ? "true" : undefined}
                    data-item-id={item.id}
                    data-active={isActive ? "true" : undefined}
                    data-cursor={isCursor ? "true" : undefined}
                    data-problem={problem ? "true" : undefined}
                    className={`flex cursor-pointer flex-col gap-0.5 rounded-md border-l-8 px-3 py-2 ${isActive ? "border-l-red-600" : isCursor ? "border-l-blue-700" : "border-l-transparent"} ${isCursor ? "bg-blue-50" : "bg-white"} ${problem ? "opacity-70" : ""} border`}
                    onClick={() => { controller.setCursor(index); }}
                    onDoubleClick={() => { controller.setCursor(index); controller.activate(); }}
                  >
                    <span className="flex items-center gap-3">
                      <span className="w-6 text-right text-sm text-neutral-500">{index + 1}</span>
                      <span className="w-16 text-xs font-semibold uppercase text-neutral-600">{TYPE_LABEL[item.itemType] ?? item.itemType}</span>
                      <span className={`min-w-0 flex-1 truncate text-lg ${item.itemType === "note" ? "italic" : "font-semibold"} ${item.isMissing ? "text-red-700 line-through" : ""}`}>{item.title}</span>
                      {isActive && <span data-testid="row-badge" className={`rounded px-2 py-0.5 text-xs font-bold ${BADGE_CLASS[status.kind]}`}>{badge}{focusContent && focusItem?.id === item.id && activePosition !== undefined ? ` · ${String(activePosition + 1)}/${String(focusContent.slides.length)}` : ""}</span>}
                      {!isActive && isCursor && <span className="rounded bg-blue-700 px-2 py-0.5 text-xs font-bold text-white">CURSOR</span>}
                    </span>
                    {problem && <span className="pl-9 text-sm font-semibold text-red-700">{problem}</span>}
                    {item.itemType === "note" && item.note && <span className="pl-9 text-sm text-neutral-700">{item.note}</span>}
                  </li>
                );
              })}
              {items.length === 0 && <li className="text-sm text-neutral-600">Esta lista está vacía.</li>}
            </ol>
          ) : (
            <p className="text-sm text-neutral-600">{s.loadingList ? "Cargando la lista…" : s.lists.length > 0 ? "Elige una lista." : ""}</p>
          )}

          {showEnd && (
            <div data-testid="end-marker" className="rounded-md bg-neutral-800 px-4 py-3 text-lg font-bold text-white">
              {upcoming ? `FIN · siguiente: «${upcoming.title}»` : "FIN · último elemento de la lista"}
              <span className="ml-3 text-sm font-normal">Ctrl+→ pasa al siguiente.</span>
            </div>
          )}

          {focusItem && focusItem.itemType !== "note" && (
            <section aria-label="Diapositivas del elemento" className="flex flex-col gap-2" data-testid="strip">
              <h2 className="text-sm font-semibold">{focusItem.title}{focusContent ? ` · ${String(focusContent.slides.length)} ${focusItem.itemType === "song" ? "diapositiva(s)" : "página(s)"}` : ""}</h2>
              {focusState?.status === "error" && <p role="alert" className="text-sm text-red-700">{focusState.message}</p>}
              {!focusContent && focusState?.status !== "error" && <p className="text-sm text-neutral-500">{focusItem.projectable ? "Preparando…" : "No se puede proyectar."}</p>}
              {focusContent && (
                <ol className="grid grid-cols-2 gap-3 xl:grid-cols-4" aria-label="Diapositivas">
                  {focusContent.slides.slice(0, MAX_RENDERED_SLIDES).map((blocks, position) => {
                    const here = focusItem.id === active?.itemId && position === active.position;
                    return (
                      <li
                        key={position}
                        data-live={here ? "true" : undefined}
                        style={{ contentVisibility: "auto", containIntrinsicSize: "auto 150px" }}
                        className={`flex cursor-pointer flex-col gap-1 rounded p-1 ${here ? "ring-4 ring-red-600" : ""}`}
                        onClick={() => { controller.projectSlide(focusItem.id, position); }}
                      >
                        <span className="text-xs font-semibold">{position + 1}{here ? " · EN PANTALLA" : ""}</span>
                        <BlocksCanvas theme={focusContent.theme} blocks={blocks} width={200} />
                      </li>
                    );
                  })}
                </ol>
              )}
            </section>
          )}

          {list && (
            <section aria-label="Siguiente elemento" className="flex flex-col gap-1 rounded-md border p-3" data-testid="next-preview">
              <h2 className="text-sm font-semibold">Siguiente</h2>
              {upcoming ? (
                <div className="flex items-start gap-3">
                  <div className="flex flex-col gap-1">
                    <span className="text-lg font-semibold" data-testid="next-title">{upcoming.title}</span>
                    <span className="text-xs text-neutral-600">{TYPE_LABEL[upcoming.itemType] ?? upcoming.itemType}</span>
                  </div>
                  {upcomingContent ? (
                    <BlocksCanvas theme={upcomingContent.theme} blocks={upcomingContent.slides[0] ?? []} width={220} />
                  ) : (
                    <span className="text-sm text-neutral-500">{upcomingState?.status === "error" ? upcomingState.message : "Preparando la vista previa…"}</span>
                  )}
                </div>
              ) : (
                <span className="text-sm text-neutral-600">No hay más elementos que proyectar después de este.</span>
              )}
            </section>
          )}

          {cursorItem?.itemType === "note" && cursorItem.note && (
            <p data-testid="note-banner" className="rounded-md border-2 border-neutral-700 bg-neutral-100 p-3 text-lg">{cursorItem.note}</p>
          )}
        </>
      )}

      <details className="text-sm text-neutral-700">
        <summary className="cursor-pointer font-semibold">Atajos de teclado de la pestaña</summary>
        <ul className="mt-2 grid grid-cols-1 gap-1 sm:grid-cols-2">
          {LIST_SHORTCUTS.map((k) => (
            <li key={`${k.action}-${k.key}-${String(k.ctrl)}`}><kbd className="rounded border bg-neutral-100 px-1.5 font-mono">{k.label}</kbd> {k.description}</li>
          ))}
        </ul>
        <p className="mt-2 text-xs text-neutral-500">Los atajos globales (Ctrl+Alt+Shift+M/B/C/L/R) no cambian y funcionan siempre; los controles maestros (Negro, Limpiar, Logo, Congelar) funcionan igual y solo cambian la insignia.</p>
      </details>
    </div>
  );
}
