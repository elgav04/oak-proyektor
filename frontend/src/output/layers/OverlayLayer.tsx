import type { OverlayLayer as OverlayState } from "@/ipc/protocol";
import { AlertErrorBoundary, AlertLayer } from "./AlertLayer";

/**
 * Capa 3: la alerta (cinta estática o con desplazamiento). `visible` lo decide el control maestro (Texto limpio, Negro y Logo la ocultan al instante; Freeze NO la congela: ver `freeze.ts`).
 * La alerta va dentro de su PROPIO límite de errores: un fallo al pintarla la oculta a ella sola, sin tocar el contenido ni el fondo.
 */
export function OverlayLayer({ overlays, visible }: { overlays: OverlayState; visible: boolean }) {
  return (
    <div
      className="layer layer-overlays"
      data-layer="overlays"
      data-alert={overlays.alert?.id ?? ""}
      style={{ visibility: visible ? "visible" : "hidden" }}
    >
      <AlertErrorBoundary resetKey={overlays.alert?.id ?? null}>
        <AlertLayer alert={overlays.alert} />
      </AlertErrorBoundary>
    </div>
  );
}
