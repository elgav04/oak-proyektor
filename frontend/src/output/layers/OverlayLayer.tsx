import type { OverlayLayer as OverlayState } from "@/ipc/protocol";

/** Capa 3: solo el contenedor. Todavía no hay alertas ni ticker, así que no pinta nada; los elementos de `items` se renderizarán aquí. */
export function OverlayLayer({ overlays, visible }: { overlays: OverlayState; visible: boolean }) {
  return (
    <div
      className="layer layer-overlays"
      data-layer="overlays"
      data-items={overlays.items.length}
      style={{ visibility: visible ? "visible" : "hidden" }}
    />
  );
}
