import { useEffect, useRef } from "react";
import { useStore } from "zustand";
import { Button } from "@/components/ui/button";
import { matchBibleShortcut, matchSearchKey, BIBLE_SHORTCUTS } from "../shortcuts";
import type { BibleController } from "./controller";

/**
 * Pestaña de Biblia. Todo el estado y la lógica están en el controlador (que habla con el backend); aquí solo se pinta y se conectan las teclas, que salen
 * del mapa único (`../shortcuts.ts`). Listas cortas (un capítulo ≤ 176 versículos, ≤ 50 resultados, ≤ 30 citas): no hace falta virtualizar.
 */
export function BibleTab({ controller, connected, onOpenSettings }: { controller: BibleController; connected: boolean; onOpenSettings: () => void }) {
  const s = useStore(controller.store);
  const searchRef = useRef<HTMLInputElement>(null);
  const listRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!s.loaded) void controller.init();
  }, [controller, s.loaded]);

  // Teclas de la pestaña (mapa único). El foco en un cuadro de texto/lista desplegable las deja pasar; el cuadro de búsqueda maneja las suyas.
  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent): void => {
      if (event.repeat && (event.key === "Enter" || event.key === "Escape")) return;   // una tecla mantenida no repite proyecciones ni borrados
      const action = matchBibleShortcut(event, event.target as HTMLElement | null);
      if (!action) return;
      event.preventDefault();
      switch (action) {
        case "focusSearch":
          searchRef.current?.focus();
          searchRef.current?.select();
          break;
        case "verseNext":
          controller.moveSelection(1);
          break;
        case "versePrev":
          controller.moveSelection(-1);
          break;
        case "extendNext":
          controller.moveSelection(1, true);
          break;
        case "extendPrev":
          controller.moveSelection(-1, true);
          break;
        case "project":
          controller.project();
          break;
        case "clear":
          controller.clearProjection();
          break;
        case "pageNext":
          controller.pageBy(1);
          break;
        case "pagePrev":
          controller.pageBy(-1);
          break;
      }
    };
    window.addEventListener("keydown", onKeyDown);
    return () => {
      window.removeEventListener("keydown", onKeyDown);
    };
  }, [controller]);

  // El versículo activo siempre a la vista.
  useEffect(() => {
    const row = listRef.current?.querySelector<HTMLElement>('[aria-selected="true"][data-active="true"]');
    row?.scrollIntoView({ block: "nearest" });
  }, [s.active, s.passage]);

  const onSearchKeyDown = (event: React.KeyboardEvent<HTMLInputElement>): void => {
    const action = matchSearchKey(event.nativeEvent);
    if (!action) return;
    event.preventDefault();
    event.stopPropagation();
    switch (action) {
      case "moveDown":
        controller.moveSuggestion(1);
        break;
      case "moveUp":
        controller.moveSuggestion(-1);
        break;
      case "submit":
        controller.submitSearch();
        event.currentTarget.blur();
        break;
      case "clear":
        if (s.query.length > 0) controller.setQuery("");
        else event.currentTarget.blur();
        break;
    }
  };

  const built = s.built;
  const page = built?.pages[s.pageIndex];
  const live = s.projected !== null && built !== null && s.projected.reference === built.reference && s.projected.page === s.pageIndex;
  const lo = Math.min(s.anchor, s.active);
  const hi = Math.max(s.anchor, s.active);

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center gap-3">
        <input
          ref={searchRef}
          type="search"
          aria-label="Buscar: referencia («Jn 3:16») o texto"
          placeholder="Referencia («Jn 3:16-18») o palabras del texto    ( / )"
          className="h-12 min-w-80 flex-1 rounded-md border-2 p-3 text-lg"
          value={s.query}
          disabled={!connected}
          onChange={(e) => { controller.setQuery(e.target.value); }}
          onKeyDown={onSearchKeyDown}
          autoComplete="off"
          spellCheck={false}
        />
        <label className="flex items-center gap-2 text-sm">
          Biblia
          <select
            className="h-12 rounded-md border-2 p-2 text-base"
            value={s.activeBible ?? ""}
            disabled={!connected || s.bibles.length === 0}
            onChange={(e) => { controller.setBible(e.target.value); }}
          >
            {s.bibles.map((b) => (
              <option key={b.abbreviation} value={b.abbreviation}>
                {s.settings?.shortNames.find((n) => n.bible === b.abbreviation)?.shortName ?? b.abbreviation} — {b.name}
              </option>
            ))}
          </select>
        </label>
        <label className="flex cursor-pointer items-center gap-2 text-sm">
          <input
            type="checkbox"
            checked={s.settings?.projectOnNavigate ?? false}
            disabled={!connected || !s.settings}
            onChange={(e) => { if (s.settings) void controller.saveSettings({ ...s.settings, projectOnNavigate: e.target.checked }); }}
          />
          Proyectar al navegar
        </label>
        <Button variant="outline" onClick={onOpenSettings}>Configuración de la Biblia</Button>
      </div>

      {s.message && (
        <div role="status" className="flex items-start justify-between gap-3 rounded-md border border-amber-600 bg-amber-100 p-3 text-sm text-amber-950">
          <span>{s.message}</span>
          <button type="button" className="shrink-0 underline" onClick={controller.dismissMessage}>Cerrar</button>
        </div>
      )}

      {(s.suggestions.length > 0 || s.hits.length > 0) && (
        <ul role="listbox" aria-label="Resultados" className="flex max-h-72 flex-col overflow-y-auto rounded-md border">
          {s.suggestions.map((sug, i) => (
            <li key={sug.bookNumber} role="option" aria-selected={i === s.suggestionIndex}>
              <button
                type="button"
                className={`w-full px-3 py-2 text-left text-base ${i === s.suggestionIndex ? "bg-blue-100 font-semibold" : "hover:bg-neutral-100"}`}
                onClick={() => { controller.pickSuggestion(sug.bookNumber); }}
              >
                {sug.name} <span className="text-sm text-neutral-500">({sug.shortName})</span>
              </button>
            </li>
          ))}
          {s.canSearchText && (
            <li>
              <button type="button" className="w-full px-3 py-2 text-left text-sm text-neutral-700 hover:bg-neutral-100" onClick={controller.searchText}>
                Buscar «{s.query.trim()}» en el texto de la Biblia
              </button>
            </li>
          )}
          {s.hits.map((hit, i) => (
            <li key={`${String(hit.bookNumber)}:${String(hit.chapter)}:${String(hit.verseNumber)}`} role="option" aria-selected={i === s.suggestionIndex}>
              <button
                type="button"
                className={`w-full px-3 py-2 text-left ${i === s.suggestionIndex ? "bg-blue-100" : "hover:bg-neutral-100"}`}
                onClick={() => { controller.openHit(hit); }}
              >
                <span className="font-semibold">{hit.bookName} {hit.chapter}:{hit.verseNumber}</span>{" "}
                <span className="text-sm text-neutral-700">{hit.fragment.replace(/[«»]/g, "")}</span>
              </button>
            </li>
          ))}
          {s.hitsTruncated && <li className="px-3 py-2 text-xs text-neutral-500">Hay más resultados: afina la búsqueda.</li>}
        </ul>
      )}

      <div className="grid grid-cols-1 gap-4 lg:grid-cols-[1fr_22rem]">
        <section aria-label="Versículos" className="flex flex-col gap-2">
          <h2 className="text-lg font-semibold">
            {s.passage ? `${s.passage.verses[0]?.bookName ?? ""} ${String(s.passage.verses[0]?.chapter ?? "")}` : "Versículos"}
            {s.passage && <span className="ml-2 text-sm font-normal text-neutral-500">{s.passage.bible}</span>}
          </h2>
          <div ref={listRef} tabIndex={-1} role="listbox" aria-multiselectable="true" className="max-h-[28rem] overflow-y-auto rounded-md border outline-none">
            {!s.passage && <p className="p-4 text-neutral-500">Escribe una referencia o elige un libro.</p>}
            {s.passage?.verses.map((v, i) => {
              const selected = i >= lo && i <= hi;
              return (
                <div
                  key={`${String(v.chapter)}:${String(v.verseNumber)}`}
                  role="option"
                  aria-selected={selected}
                  data-active={i === s.active ? "true" : "false"}
                  className={`flex cursor-pointer gap-3 border-b px-3 py-2 text-base ${selected ? "bg-blue-100" : "hover:bg-neutral-50"} ${i === s.active ? "border-l-4 border-l-blue-700 font-medium" : ""}`}
                  onClick={(e) => { controller.selectIndex(i, e.shiftKey); }}
                  onDoubleClick={() => { controller.selectIndex(i); controller.project(); }}
                >
                  <span className="w-10 shrink-0 text-right font-semibold text-neutral-600">{v.verseNumber}</span>
                  <span>{v.text}</span>
                </div>
              );
            })}
          </div>
        </section>

        <section aria-label="En pantalla" className="flex flex-col gap-3 rounded-md border p-3">
          <div className="flex items-center justify-between">
            <h2 className="text-lg font-semibold">{built?.reference ?? "Selección"}</h2>
            <span className={`rounded-full px-3 py-1 text-sm font-bold ${live ? "bg-green-600 text-white" : "bg-neutral-200 text-neutral-700"}`}>{live ? "EN VIVO" : "LISTO"}</span>
          </div>
          {built && page ? (
            <>
              <p className="max-h-48 overflow-y-auto whitespace-pre-wrap rounded bg-neutral-900 p-3 text-sm text-white">{page.join("\n")}</p>
              <div className="flex items-center justify-between">
                <Button variant="outline" disabled={s.pageIndex === 0} onClick={() => { controller.pageBy(-1); }}>← Anterior</Button>
                <span className="text-lg font-bold tabular-nums" aria-label="Página">{s.pageIndex + 1}/{built.pages.length}</span>
                <Button variant="outline" disabled={s.pageIndex >= built.pages.length - 1} onClick={() => { controller.pageBy(1); }}>Siguiente →</Button>
              </div>
            </>
          ) : (
            <p className="text-sm text-neutral-500">Elige un versículo.</p>
          )}
          <div className="flex gap-3">
            <Button size="lg" className="h-12 flex-1" disabled={!connected || !built} onClick={controller.project}>Proyectar (Enter)</Button>
            <Button size="lg" variant="outline" className="h-12" disabled={!connected} onClick={controller.clearProjection}>Limpiar (Esc)</Button>
          </div>
        </section>
      </div>

      <section aria-label="Historial de citas" className="flex flex-col gap-2">
        <h2 className="text-lg font-semibold">Historial</h2>
        {s.history.length === 0 ? (
          <p className="text-sm text-neutral-500">Todavía no se ha proyectado ninguna cita.</p>
        ) : (
          <ul className="flex flex-wrap gap-2">
            {s.history.map((h) => (
              <li key={h.id}>
                <button type="button" className="rounded-full border px-3 py-1 text-sm hover:bg-neutral-100" disabled={!connected} onClick={() => { controller.reproject(h); }}>
                  {h.label} <span className="text-neutral-500">{h.bible}</span>
                </button>
              </li>
            ))}
          </ul>
        )}
      </section>

      <details className="text-sm text-neutral-700">
        <summary className="cursor-pointer font-semibold">Atajos de teclado de la pestaña</summary>
        <ul className="mt-2 grid grid-cols-1 gap-1 sm:grid-cols-2">
          {BIBLE_SHORTCUTS.map((k) => (
            <li key={`${k.action}-${k.key}-${String(k.shift)}`}><kbd className="rounded border bg-neutral-100 px-1.5 font-mono">{k.label}</kbd> {k.description}</li>
          ))}
        </ul>
        <p className="mt-2 text-xs text-neutral-500">Los atajos globales (Ctrl+Alt+Shift+M/B/C/L/R) no cambian y funcionan siempre.</p>
      </details>
    </div>
  );
}
