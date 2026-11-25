import { useRef, useState } from "react";
import { useStore } from "zustand";
import { Button } from "@/components/ui/button";
import type { SongController } from "./controller";
import { windowRange } from "./windowRange";

const ROW_HEIGHT = 56;

/** Lista de canciones con búsqueda (título, autor y letra, sin tildes) y filas VIRTUALIZADAS: con miles de canciones solo se pintan las visibles. */
export function SongList({ controller, connected, searchRef }: { controller: SongController; connected: boolean; searchRef: React.RefObject<HTMLInputElement | null> }) {
  const s = useStore(controller.store);
  const [scrollTop, setScrollTop] = useState(0);
  const boxRef = useRef<HTMLDivElement>(null);
  const rows = s.results ?? s.list;
  const range = windowRange(scrollTop, boxRef.current?.clientHeight ?? 0, ROW_HEIGHT, rows.length);
  const searching = s.query.trim() !== "";

  const onSearchKey = (event: React.KeyboardEvent<HTMLInputElement>): void => {
    if (event.ctrlKey || event.altKey || event.metaKey) return;
    if (event.key === "ArrowDown") { event.preventDefault(); controller.moveList(1); }
    else if (event.key === "ArrowUp") { event.preventDefault(); controller.moveList(-1); }
    else if (event.key === "Enter") { event.preventDefault(); controller.selectIndex(s.listIndex); event.currentTarget.blur(); }   // elige la canción; el siguiente Enter proyecta su diapositiva
    else if (event.key === "Escape") { event.preventDefault(); if (s.query.length > 0) controller.setQuery(""); else event.currentTarget.blur(); }
  };

  return (
    <div className="flex flex-col gap-3">
      <div className="flex flex-wrap items-center gap-3">
        <input
          ref={searchRef}
          type="search"
          aria-label="Buscar canciones: título, autor o letra"
          placeholder="Buscar por título, autor o letra    ( / )"
          className="h-12 min-w-80 flex-1 rounded-md border-2 p-3 text-lg"
          value={s.query}
          disabled={!connected}
          autoComplete="off"
          spellCheck={false}
          onChange={(e) => { controller.setQuery(e.target.value); }}
          onKeyDown={onSearchKey}
        />
        <Button size="lg" disabled={!connected} onClick={controller.newSong}>Canción nueva (Ctrl+N)</Button>
        <Button size="lg" variant="outline" disabled={!connected} onClick={() => { controller.openImport(); }}>Importar un .txt…</Button>
        <Button size="lg" variant="outline" onClick={controller.openTheme}>Tema de las canciones</Button>
      </div>

      <p className="text-sm text-neutral-600" role="status">
        {searching ? `${String(rows.length)} resultado(s)${s.resultsTruncated ? " (hay más: afina la búsqueda)" : ""}` : `${String(rows.length)} canción(es)${s.listTruncated ? " (se muestran las primeras 2000)" : ""}`}
      </p>

      <div
        ref={boxRef}
        role="listbox"
        aria-label="Canciones"
        className="relative max-h-[30rem] min-h-40 overflow-y-auto rounded-md border"
        style={{ height: Math.min(30 * 16, Math.max(160, rows.length * ROW_HEIGHT)) }}
        onScroll={(e) => { setScrollTop(e.currentTarget.scrollTop); }}
      >
        {rows.length === 0 && <p className="p-4 text-neutral-500">{searching ? "Ninguna canción coincide." : "Todavía no hay canciones. Crea una o importa un archivo .txt."}</p>}
        <div style={{ height: rows.length * ROW_HEIGHT, position: "relative" }}>
          {rows.slice(range.start, range.end).map((song, offset) => {
            const index = range.start + offset;
            const active = index === s.listIndex;
            return (
              <div
                key={song.id}
                role="option"
                aria-selected={active}
                data-song-id={String(song.id)}
                className={`absolute inset-x-0 flex items-center gap-3 border-b px-3 ${active ? "bg-blue-100" : "hover:bg-neutral-50"}`}
                style={{ top: index * ROW_HEIGHT, height: ROW_HEIGHT }}
                onClick={() => { controller.selectIndex(index); }}
                onDoubleClick={() => { controller.openSong(song.id); }}
              >
                <div className="min-w-0 flex-1">
                  <p className="truncate font-semibold">{song.title}</p>
                  <p className="truncate text-sm text-neutral-600">
                    {song.author ?? "sin autor"}
                    {song.fragment ? <span className="ml-2 text-neutral-500">· {song.fragment}</span> : null}
                  </p>
                </div>
                <Button size="sm" variant="outline" disabled={!connected} onClick={(e) => { e.stopPropagation(); controller.openSong(song.id); }}>Editar</Button>
                <Button size="sm" variant="outline" disabled={!connected} onClick={(e) => { e.stopPropagation(); controller.duplicate(song.id); }}>Duplicar</Button>
                <Button size="sm" variant="outline" disabled={!connected} onClick={(e) => { e.stopPropagation(); controller.askDelete(song.id); }}>Borrar</Button>
              </div>
            );
          })}
        </div>
      </div>
    </div>
  );
}
