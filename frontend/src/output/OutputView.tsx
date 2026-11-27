import { useCallback, useEffect, useState } from "react";
import { ipc } from "@/ipc/instance";
import type { OutputStateChangedPayload } from "@/ipc/protocol";
import { createRetrier } from "@/ipc/retry";
import type { BackgroundFailure } from "./background/engine";
import { isEmergencyKey, isHideAlertKey, isSilenceKey } from "./emergency";
import type { ForegroundReport } from "./foreground/player";
import { nextView, type OutputView as View } from "./freeze";
import { OutputScreen } from "./OutputScreen";

/** La salida es "tonta": recibe snapshots por IPC y los pinta; nada más. */
export function OutputView() {
  const [view, setView] = useState<View | null>(null);

  useEffect(() => {
    // Un snapshot se aplica solo si su secuencia es estrictamente mayor que la última (descarta viejos y repetidos). Con Freeze, `nextView` retiene lo pintado (ver freeze.ts).
    const apply = (next: OutputStateChangedPayload): void => {
      setView((current) => nextView(current, next));
    };
    const off = ipc.subscribe("output.stateChanged", apply);

    // Estado inicial: la ventana puede cargarse después de que ocurrió el último evento. Reintenta sin rendirse
    // (si el backend tarda, la salida se queda en negro hasta que responda; el siguiente evento también la actualiza).
    const initial = createRetrier(async () => {
      apply(await ipc.request("output.getState", {}));
    });
    initial.start();

    // Respaldo del atajo global: pide a C# minimizar la salida. La salida no decide nada; solo avisa.
    const onKeyDown = (e: KeyboardEvent): void => {
      if (isSilenceKey(e)) {
        // Respaldo del atajo global «Silencio»: solo calla (las pistas y el video en primer plano); nunca reanuda.
        e.preventDefault();
        ipc.send("media.stopAll", {});
        return;
      }
      if (isHideAlertKey(e)) {
        // Mismo respaldo para quitar la alerta (solo quita): por si el atajo global no pudo registrarse.
        e.preventDefault();
        ipc.send("alert.hide", {});
        return;
      }
      if (!isEmergencyKey(e)) return;
      e.preventDefault();
      ipc.send("output.recover", { reason: "emergency" });
    };
    window.addEventListener("keydown", onKeyDown);
    return () => {
      initial.stop();
      off();
      window.removeEventListener("keydown", onKeyDown);
    };
  }, []);

  // El fondo no pudo reproducirse (ya cayó al color de reserva): la salida solo avisa al backend; este decide qué le dice al panel.
  const reportFailure = useCallback((failure: BackgroundFailure): void => {
    ipc.send("output.reportBackgroundFailure", { mediaUrl: failure.mediaUrl, reason: failure.reason, detail: failure.detail });
  }, []);

  // El video en primer plano informa su posición y estado REALES (≈ 4 por segundo mientras suena, y al instante en cada cambio): el backend decide qué hacer al terminar o fallar.
  const reportForeground = useCallback((report: ForegroundReport): void => {
    ipc.send("media.reportProgress", { id: report.id, positionMs: report.positionMs, durationMs: report.durationMs, state: report.state, errorCode: report.errorCode });
  }, []);

  return (
    <>
      <OutputScreen state={view?.shown ?? null} seq={view?.seq ?? null} onBackgroundFailure={reportFailure} onForegroundReport={reportForeground} />
      {view?.panelAlert === true && (
        // Aviso discreto, solo si el operador lo configuró: el panel está atascado pero la salida sigue proyectando.
        <div
          role="status"
          style={{ position: "fixed", right: 12, bottom: 12, padding: "6px 12px", borderRadius: 6, background: "#b91c1c", color: "#fff", font: "600 16px system-ui, sans-serif", zIndex: 2147483647 }}
        >
          PANEL SIN RESPUESTA
        </div>
      )}
    </>
  );
}