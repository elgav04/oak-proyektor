import { useState } from "react";
import { Button } from "@/components/ui/button";
import { runLayoutCheck, type LayoutReport } from "./bible/layoutCheck";
import { MediaTest } from "./MediaTest";

/**
 * Diagnóstico (NO es la interfaz principal): la vista provisional anterior (prueba del motor multimedia: fondo y envío de texto a mano) y la prueba de layout
 * de la paginación de la Biblia. Nada de esto guarda estado de negocio.
 */
export function Diagnostics() {
  const [report, setReport] = useState<LayoutReport | null>(null);
  return (
    <div className="flex flex-col gap-6">
      <section aria-label="Prueba de layout" className="flex flex-col gap-3 rounded-md border border-dashed border-neutral-400 p-4">
        <h2 className="text-lg font-semibold">Prueba de layout de la paginación (texto sintético)</h2>
        <p className="text-sm text-neutral-600">
          Mide con el motor real del navegador textos de longitud conocida sobre el lienzo de 1920×1080 y comprueba que la paginación no pierde ni recorta texto.
          La versión completa, contra la ventana de salida real, es <code>tools\bible-layout-test.ps1</code>.
        </p>
        <Button className="w-fit" variant="outline" onClick={() => { setReport(runLayoutCheck()); }}>Ejecutar</Button>
        {report && (
          <div role="status" className="flex flex-col gap-1 text-sm">
            <p className={report.ok ? "font-bold text-green-700" : "font-bold text-red-700"}>{report.ok ? "Todo cabe y no se pierde texto" : "HAY FALLOS"} ({String(report.totalMs)} ms)</p>
            <ul className="font-mono text-xs">
              {report.cases.map((c) => (
                <li key={c.name} className={c.lossless && c.allFit ? "" : "text-red-700"}>
                  {c.name}: {c.pages.length} pág., {c.ms} ms, {c.lossless ? "sin pérdida" : "PÉRDIDA"}, {c.allFit ? "cabe" : "NO CABE"}
                </li>
              ))}
            </ul>
          </div>
        )}
      </section>
      <MediaTest />
    </div>
  );
}
