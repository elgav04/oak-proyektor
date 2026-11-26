import { useEffect, useRef, useState } from "react";
import { useStore } from "zustand";
import { Button } from "@/components/ui/button";
import type { MediaItemDto, MediaItemKind } from "@/ipc/protocol";
import { windowRange } from "../songs/windowRange";
import type { FolderFilter, MediaController } from "./controller";
import { KIND_LABELS, MEDIA_ROW_HEIGHT, canBeBackground, formatBytes, formatDuration, statusBadge, techLine } from "./model";

const TONE: Record<string, string> = { ok: "bg-green-100 text-green-900", warn: "bg-amber-100 text-amber-900", bad: "bg-red-100 text-red-900", idle: "bg-neutral-200 text-neutral-700" };
const VIEWPORT = 440;

function Row({ item, controller, folders, connected, muted }: { item: MediaItemDto; controller: MediaController; folders: { id: number; name: string }[]; connected: boolean; muted: boolean }) {
  const [renaming, setRenaming] = useState<string | null>(null);
  const [confirming, setConfirming] = useState(false);
  const badge = statusBadge(item);
  const duration = formatDuration(item.durationMs);
  return (
    <li className="flex items-center gap-3 border-b px-2" style={{ height: MEDIA_ROW_HEIGHT }} data-testid="media-row" data-media-id={item.id}>
      <div className="flex h-[54px] w-24 shrink-0 items-center justify-center overflow-hidden rounded bg-neutral-200 text-xl text-neutral-500" aria-hidden="true">
        {item.thumbnailUrl ? <img src={item.thumbnailUrl} alt="" className="h-full w-full object-cover" loading="lazy" /> : item.kind === "audio" ? "♪" : item.kind === "image" ? "▣" : "▶"}
      </div>
      <div className="min-w-0 flex-1">
        {renaming !== null ? (
          <input
            autoFocus className="h-8 w-full rounded border-2 px-2" aria-label="Nombre" value={renaming}
            onChange={(e) => { setRenaming(e.target.value); }}
            onKeyDown={(e) => {
              if (e.key === "Enter") { controller.rename(item, renaming); setRenaming(null); }
              else if (e.key === "Escape") setRenaming(null);
              e.stopPropagation();
            }}
            onBlur={() => { setRenaming(null); }}
          />
        ) : (
          <p className="truncate font-semibold" title={item.name}>{item.name}</p>
        )}
        <p className="truncate text-xs text-neutral-600">
          {KIND_LABELS[item.kind]}{duration ? ` · ${duration}` : ""} · {techLine(item)} · {formatBytes(item.bytes)}
        </p>
        {badge.detail && <p className="truncate text-xs text-neutral-700" title={badge.detail}>{badge.detail}</p>}
      </div>
      <span className={`shrink-0 rounded-full px-2 py-0.5 text-xs font-semibold ${TONE[badge.tone] ?? ""}`} data-testid="media-badge">{badge.text}</span>
      {confirming ? (
        <div className="flex shrink-0 items-center gap-2" role="group" aria-label="Confirmar el borrado">
          <span className="text-sm">¿Borrar la copia?</span>
          <Button size="sm" variant="destructive" disabled={!connected} onClick={() => { controller.remove(item); setConfirming(false); }}>Sí, borrar</Button>
          <Button size="sm" variant="outline" onClick={() => { setConfirming(false); }}>No</Button>
        </div>
      ) : (
        <div className="flex shrink-0 items-center gap-2">
          {canBeBackground(item) && <Button size="sm" disabled={!connected} title="Usar como fondo de la salida" onClick={() => { controller.useAsBackground(item, muted); }}>Fondo</Button>}
          <select
            className="h-8 w-28 rounded border-2 px-1 text-sm" aria-label={`Carpeta de ${item.name}`} value={item.folderId ?? ""} disabled={!connected}
            onChange={(e) => { controller.move(item, e.target.value === "" ? null : Number(e.target.value)); }}
          >
            <option value="">Sin carpeta</option>
            {folders.map((f) => <option key={f.id} value={f.id}>{f.name}</option>)}
          </select>
          <Button size="sm" variant="outline" disabled={!connected} aria-label={`Renombrar ${item.name}`} onClick={() => { setRenaming(item.name); }}>✎</Button>
          <Button size="sm" variant="outline" disabled={!connected} aria-label={`Borrar ${item.name}`} onClick={() => { setConfirming(true); }}>✕</Button>
        </div>
      )}
    </li>
  );
}

/**
 * Biblioteca de medios: importar (se COPIA a la carpeta de la aplicación: reproducir nunca lee de un USB ni de la red), carpetas, búsqueda y la lista (virtualizada, ≤ 500 por consulta).
 * Sin estado de negocio: todo sale de `media.list` y los eventos `media.*`. Nada de `alert/confirm/prompt`: las confirmaciones son barras en línea.
 */
export function MediaTab({ controller, connected }: { controller: MediaController; connected: boolean }) {
  const s = useStore(controller.store);
  const [path, setPath] = useState("");
  const [name, setName] = useState("");
  const [targetFolder, setTargetFolder] = useState<number | null>(null);
  const [scrollTop, setScrollTop] = useState(0);
  const [muted, setMuted] = useState(true);
  const [folderName, setFolderName] = useState("");
  const [folderEdit, setFolderEdit] = useState<string | null>(null);
  const boxRef = useRef<HTMLDivElement>(null);

  useEffect(() => { if (connected) controller.init(); }, [controller, connected]);

  const range = windowRange(scrollTop, boxRef.current?.clientHeight ?? VIEWPORT, MEDIA_ROW_HEIGHT, s.items.length);
  const visible = s.items.slice(range.start, range.end);
  const selectedFolder = typeof s.folder === "number" ? s.folders.find((f) => f.id === s.folder) : undefined;
  const pct = s.import.totalBytes > 0 ? Math.min(100, Math.round((s.import.doneBytes / s.import.totalBytes) * 100)) : 0;
  const folderButton = (key: string, label: string, value: FolderFilter, count?: number) => (
    <button
      key={key} type="button" aria-pressed={s.folder === value}
      className={`rounded-full border px-3 py-1 text-sm ${s.folder === value ? "border-blue-700 bg-blue-700 text-white" : "bg-white hover:bg-neutral-100"}`}
      onClick={() => { controller.setFolder(value); setScrollTop(0); }}
    >
      {label}{count !== undefined ? ` (${String(count)})` : ""}
    </button>
  );

  return (
    <section aria-label="Biblioteca de medios" data-testid="media-tab" className="flex flex-col gap-4">
      <div className="flex flex-wrap items-baseline justify-between gap-2">
        <h2 className="text-xl font-bold">Biblioteca de medios</h2>
        <p className="text-sm text-neutral-600" data-testid="media-usage">
          {String(s.totalItems)} elementos · {formatBytes(s.totalBytes)}{s.freeBytes >= 0 ? ` · ${formatBytes(s.freeBytes)} libres en el disco` : ""}
        </p>
      </div>

      {s.notice && (
        <div role="alert" data-testid="media-notice" className={`flex flex-wrap items-center justify-between gap-3 rounded-md border-2 p-3 ${s.notice.kind === "error" ? "border-red-600 bg-red-50" : s.notice.kind === "warn" ? "border-amber-600 bg-amber-50" : "border-blue-600 bg-blue-50"}`}>
          <span>{s.notice.text}</span>
          <div className="flex gap-2">
            {s.notice.duplicateOf && <Button size="sm" onClick={() => { const d = s.notice?.duplicateOf; if (d) controller.showExisting(d.name); }}>Mostrarlo</Button>}
            <Button size="sm" variant="outline" onClick={() => { controller.dismissNotice(); }}>Cerrar aviso</Button>
          </div>
        </div>
      )}
      {s.listError && <p role="alert" className="text-sm text-red-700">{s.listError}</p>}

      <form
        className="flex flex-col gap-2 rounded-lg border-2 p-3" aria-label="Importar un archivo"
        onSubmit={(e) => { e.preventDefault(); if (controller.importFile(path, name, targetFolder)) { setPath(""); setName(""); } }}
      >
        <p className="text-sm font-semibold">Importar un archivo (se copia a la biblioteca: después ya no hace falta el USB ni la red)</p>
        <div className="flex flex-wrap items-center gap-2">
          <input
            className="h-10 min-w-80 flex-1 rounded-md border-2 p-2 font-mono text-sm" aria-label="Ruta completa del archivo" placeholder="Ruta completa, por ejemplo E:\Videos\anuncio.mp4"
            value={path} disabled={!connected || s.import.running} onChange={(e) => { setPath(e.target.value); }}
          />
          <input className="h-10 w-48 rounded-md border-2 p-2" aria-label="Nombre (opcional)" placeholder="Nombre (opcional)" value={name} disabled={!connected || s.import.running} onChange={(e) => { setName(e.target.value); }} />
          <select className="h-10 w-40 rounded-md border-2 p-2" aria-label="Carpeta de destino" value={targetFolder ?? ""} disabled={!connected || s.import.running} onChange={(e) => { setTargetFolder(e.target.value === "" ? null : Number(e.target.value)); }}>
            <option value="">Sin carpeta</option>
            {s.folders.map((f) => <option key={f.id} value={f.id}>{f.name}</option>)}
          </select>
          <Button type="submit" size="lg" disabled={!connected || s.import.running || path.trim() === ""}>Importar</Button>
        </div>
        {s.import.running && (
          <div className="flex items-center gap-3" data-testid="media-progress">
            <div className="h-3 flex-1 overflow-hidden rounded bg-neutral-200" role="progressbar" aria-valuemin={0} aria-valuemax={100} aria-valuenow={pct}><div className="h-full bg-blue-600" style={{ width: `${String(pct)}%` }} /></div>
            <span className="w-40 text-sm tabular-nums">{s.import.phase} · {formatBytes(s.import.doneBytes)} de {formatBytes(s.import.totalBytes)}</span>
            <Button type="button" size="sm" variant="outline" onClick={() => { controller.cancelImport(); }}>Cancelar</Button>
          </div>
        )}
        <p className="text-xs text-neutral-600">Videos MP4/M4V/WebM, audio MP3/M4A/AAC/WAV/FLAC e imágenes JPG/PNG/WebP. Máximo 4 GB por archivo. Los respaldos de la base de datos NO incluyen estos archivos.</p>
      </form>

      <div className="flex flex-wrap items-center gap-2" role="group" aria-label="Carpetas">
        {folderButton("all", "Todas", "all")}
        {folderButton("unfiled", "Sin carpeta", "unfiled")}
        {s.folders.map((f) => folderButton(`f${String(f.id)}`, f.name, f.id, f.itemCount))}
        <form className="flex items-center gap-1" onSubmit={(e) => { e.preventDefault(); controller.createFolder(folderName); setFolderName(""); }}>
          <input className="h-8 w-36 rounded border-2 px-2 text-sm" aria-label="Nueva carpeta" placeholder="+ Nueva carpeta" value={folderName} disabled={!connected} onChange={(e) => { setFolderName(e.target.value); }} />
        </form>
      </div>
      {selectedFolder && (
        <div className="flex items-center gap-2 text-sm">
          {folderEdit === null ? (
            <>
              <span>Carpeta «{selectedFolder.name}»</span>
              <Button size="sm" variant="outline" disabled={!connected} onClick={() => { setFolderEdit(selectedFolder.name); }}>Renombrar</Button>
              <Button size="sm" variant="outline" disabled={!connected} onClick={() => { controller.deleteFolder(selectedFolder); }}>Borrar carpeta (los elementos se conservan)</Button>
            </>
          ) : (
            <input
              autoFocus className="h-8 w-56 rounded border-2 px-2" aria-label="Nuevo nombre de la carpeta" value={folderEdit}
              onChange={(e) => { setFolderEdit(e.target.value); }}
              onKeyDown={(e) => { if (e.key === "Enter") { controller.renameFolder(selectedFolder, folderEdit); setFolderEdit(null); } else if (e.key === "Escape") setFolderEdit(null); e.stopPropagation(); }}
              onBlur={() => { setFolderEdit(null); }}
            />
          )}
        </div>
      )}

      <div className="flex flex-wrap items-center gap-3">
        <input className="h-10 w-72 rounded-md border-2 p-2" type="search" aria-label="Buscar por nombre" placeholder="Buscar por nombre" value={s.query} onChange={(e) => { controller.setQuery(e.target.value); setScrollTop(0); }} />
        <select className="h-10 rounded-md border-2 p-2" aria-label="Tipo" value={s.kind ?? ""} onChange={(e) => { controller.setKind(e.target.value === "" ? null : (e.target.value as MediaItemKind)); setScrollTop(0); }}>
          <option value="">Todos los tipos</option><option value="video">Videos</option><option value="audio">Audios</option><option value="image">Imágenes</option>
        </select>
        <label className="flex cursor-pointer items-center gap-2 text-sm">
          <input type="checkbox" checked={muted} onChange={(e) => { setMuted(e.target.checked); }} /> Fondo en silencio
        </label>
        <Button variant="outline" disabled={!connected} onClick={() => { controller.verifyLibrary(); }}>Verificar biblioteca</Button>
        {s.verifying !== null && <span className="text-sm text-neutral-600" data-testid="media-verifying">Comprobando que se reproduzca…</span>}
      </div>

      {s.loaded && s.items.length === 0 ? (
        <p className="rounded-md border p-6 text-center text-neutral-600" data-testid="media-empty">
          {s.totalItems === 0 ? "La biblioteca está vacía. Importa tu primer archivo arriba." : "Ningún elemento coincide con el filtro."}
        </p>
      ) : (
        <div ref={boxRef} className="overflow-y-auto rounded-md border" style={{ height: VIEWPORT }} data-testid="media-list" onScroll={(e) => { setScrollTop(e.currentTarget.scrollTop); }}>
          <ul style={{ height: s.items.length * MEDIA_ROW_HEIGHT, position: "relative" }}>
            <div style={{ position: "absolute", top: range.start * MEDIA_ROW_HEIGHT, left: 0, right: 0 }}>
              {visible.map((item) => <Row key={item.id} item={item} controller={controller} folders={s.folders} connected={connected} muted={muted} />)}
            </div>
          </ul>
        </div>
      )}
      {s.truncated && <p className="text-sm text-amber-800">Hay más elementos de los que caben en la lista: usa la búsqueda o una carpeta para acotar.</p>}
    </section>
  );
}
