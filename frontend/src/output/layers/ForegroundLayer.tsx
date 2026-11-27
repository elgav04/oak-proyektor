import { useEffect, useRef } from "react";
import type { ForegroundMedia } from "@/ipc/protocol";
import { createForegroundPlayer, type ForegroundPlayer, type ForegroundReport } from "../foreground/player";

interface Props {
  foreground: ForegroundMedia | null;
  /** El control maestro lo muestra (Live y Freeze). Falso: se oculta al instante y se pausa y silencia, conservando la posición. */
  visible: boolean;
  onReport?: (report: ForegroundReport) => void;
  /** Punto de sustitución del reproductor (pruebas). */
  createPlayer?: (container: HTMLElement, options: { onReport?: (report: ForegroundReport) => void }) => ForegroundPlayer;
}

/**
 * Capa de primer plano (v18), entre el contenido y las superposiciones (la apila el CSS). Como el fondo, React solo monta UN contenedor estable y el reproductor administra el DOM por su cuenta:
 * el efecto depende de PRIMITIVOS, no del objeto del snapshot, así que un snapshot que solo cambia el texto no puede remontar ni reiniciar el video. Va FUERA del `OutputErrorBoundary`
 * (un error al pintar el texto no desmonta el video) y no toca el fondo.
 */
export function ForegroundLayer({ foreground, visible, onReport, createPlayer = createForegroundPlayer }: Props) {
  const containerRef = useRef<HTMLDivElement>(null);
  const playerRef = useRef<ForegroundPlayer | null>(null);
  const reportRef = useRef(onReport);

  useEffect(() => {
    reportRef.current = onReport;
  }, [onReport]);

  useEffect(() => {
    const container = containerRef.current;
    if (!container) return;
    const player = createPlayer(container, {
      onReport: (report) => {
        reportRef.current?.(report);
      },
    });
    playerRef.current = player;
    return () => {
      player.dispose();
      playerRef.current = null;
    };
  }, [createPlayer]);

  const id = foreground?.id ?? null;
  const url = foreground?.url ?? null;
  const phase = foreground?.phase ?? "paused";
  const volumePct = foreground?.volumePct ?? 0;
  const muted = foreground?.muted ?? true;
  const loop = foreground?.loop ?? false;
  const seekMs = foreground?.seekMs ?? 0;
  const seekSeq = foreground?.seekSeq ?? 0;
  const fadeInMs = foreground?.fadeInMs ?? 0;
  const fadeOutMs = foreground?.fadeOutMs ?? 0;
  const backdrop = foreground?.backdrop ?? "black";
  useEffect(() => {
    playerRef.current?.apply(id === null || url === null ? null : { id, url, phase, volumePct, muted, loop, seekMs, seekSeq, fadeInMs, fadeOutMs, backdrop }, visible);
  }, [id, url, phase, volumePct, muted, loop, seekMs, seekSeq, fadeInMs, fadeOutMs, backdrop, visible]);

  return <div ref={containerRef} className="layer layer-foreground" data-layer="foreground" data-foreground={id ?? ""} />;
}