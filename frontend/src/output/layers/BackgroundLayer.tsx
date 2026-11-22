import { useEffect, useRef } from "react";
import type { BackgroundLayer as BackgroundState } from "@/ipc/protocol";
import { createSingleVideoEngine } from "../background/engine";
import type { BackgroundEngine, BackgroundFailure, EngineOptions } from "../background/engine";

interface Props {
  background: BackgroundState;
  onFailure?: (failure: BackgroundFailure) => void;
  /** Punto de sustitución del motor de video (ver `BackgroundEngine`). */
  createEngine?: (container: HTMLElement, options: EngineOptions) => BackgroundEngine;
}

/**
 * Capa 1. React solo monta UN contenedor estable; el motor administra el DOM del fondo por su cuenta. Así el elemento `<video>` no depende del
 * ciclo de render: cambiar el texto (o cualquier otra capa) no puede remontarlo ni reiniciarlo. El efecto depende de PRIMITIVOS, no del objeto
 * `background` (que llega nuevo con cada snapshot).
 */
export function BackgroundLayer({ background, onFailure, createEngine = createSingleVideoEngine }: Props) {
  const containerRef = useRef<HTMLDivElement>(null);
  const engineRef = useRef<BackgroundEngine | null>(null);
  const failureRef = useRef(onFailure);

  useEffect(() => {
    failureRef.current = onFailure;
  }, [onFailure]);

  useEffect(() => {
    const container = containerRef.current;
    if (!container) return;
    const engine = createEngine(container, {
      onFailure: (failure) => {
        failureRef.current?.(failure);
      },
    });
    engineRef.current = engine;
    return () => {
      engine.dispose();
      engineRef.current = null;
    };
  }, [createEngine]);

  const { kind, color, mediaUrl, muted, transitionMs, retry } = background;
  useEffect(() => {
    engineRef.current?.apply({ kind, color, mediaUrl, muted, transitionMs, retry });
  }, [kind, color, mediaUrl, muted, transitionMs, retry]);

  return <div ref={containerRef} className="layer layer-background" data-layer="background" />;
}
