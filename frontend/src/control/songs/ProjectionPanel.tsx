import { useStore } from "zustand";
import { Button } from "@/components/ui/button";
import type { OutputStateChangedPayload } from "@/ipc/protocol";
import type { SongController } from "./controller";
import { SONG_DEFAULT_SETTINGS } from "./defaults";
import { songLiveStatus } from "./liveStatus";
import { SlideStrip } from "./SlideStrip";

const BADGE_CLASS = { live: "bg-red-600 text-white", hidden: "bg-amber-500 text-black", ready: "bg-neutral-300 text-neutral-900" } as const;

/**
 * Proyección de la canción seleccionada: su tira de diapositivas en el orden de reproducción. La diapositiva «en pantalla» y la insignia EN VIVO salen del
 * estado que CONFIRMA el backend (`output`, de `output.stateChanged`), nunca de lo que el panel envió (hallazgo H5). El cursor (azul) es solo la selección del panel.
 */
export function ProjectionPanel({ controller, connected, output }: { controller: SongController; connected: boolean; output: OutputStateChangedPayload | null }) {
  const s = useStore(controller.store);
  const p = s.projection;
  if (!p) return <p className="mt-3 text-sm text-neutral-600">Elige una canción de la lista (clic o ↑ ↓) para ver sus diapositivas y proyectarlas.</p>;
  const { status, liveIndex } = songLiveStatus(output, p.slides, p.requested);
  const count = p.slides?.length ?? 0;
  const atEnd = liveIndex !== null && liveIndex === count - 1 && p.atEnd;
  const badge = status.kind === "live" ? "EN VIVO" : status.kind === "hidden" ? `OCULTO · ${status.why}` : "LISTO";
  return (
    <section aria-label="Proyección de la canción" className="mt-4 flex flex-col gap-3 rounded-md border-2 p-3" data-testid="projection">
      <div className="flex flex-wrap items-center gap-3">
        <h2 className="min-w-0 flex-1 truncate text-lg font-bold">{p.title || "…"}</h2>
        <span data-testid="live-badge" data-status={status.kind} className={`rounded px-3 py-1 text-sm font-bold ${BADGE_CLASS[status.kind]}`}>{badge}</span>
        {atEnd && <span data-testid="end-marker" className="rounded bg-neutral-800 px-3 py-1 text-sm font-bold text-white">FIN · última diapositiva</span>}
        <Button variant="outline" disabled={!connected || count === 0} onClick={controller.prevSlide}>← Anterior</Button>
        <Button disabled={!connected || count === 0} onClick={controller.nextSlide}>Siguiente →</Button>
        <Button variant="outline" disabled={!connected} onClick={controller.clearProjection}>Quitar texto (Esc)</Button>
      </div>
      {p.error && <p role="alert" className="text-sm text-red-700">{p.error}</p>}
      {p.background && <p className="text-xs text-neutral-600">Esta canción tiene fondo propio: se aplica al proyectar su primera diapositiva. Si no se puede usar, la salida muestra su color de reserva y el panel avisa.</p>}
      <SlideStrip slides={p.slides} theme={(s.settings ?? SONG_DEFAULT_SETTINGS).theme} busy={p.busy} selected={p.cursor} live={liveIndex} onSelect={controller.projectSlide} />
    </section>
  );
}
