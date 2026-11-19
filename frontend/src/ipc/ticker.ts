/**
 * Temporizador periódico cuyo RELOJ no vive en el hilo principal de la página: lo marca un Web Worker (creado desde un Blob, porque las páginas
 * cargan por `file://` y un worker no puede crearse desde un archivo de ese origen). El hilo principal solo recibe el `message` y ejecuta `onTick`.
 *
 * Por qué: el latido del panel hacia el backend debe significar «el JS del panel está vivo». Con `setInterval` en el hilo principal, el navegador puede
 * retrasar el temporizador de una página que considera oculta o en segundo plano (hasta 1 por minuto tras unos minutos) y el backend creería que el
 * panel está atascado cuando no lo está. Con el reloj en un worker no hay retraso por eso, y si el hilo principal de verdad está atascado, los `message`
 * se quedan en cola SIN ejecutarse: no hay latido, que es exactamente lo que el backend debe notar.
 *
 * Sin `Worker` (tests de Node, navegador raro) o si el worker falla: cae a `setInterval`. Devuelve la función que lo detiene.
 */
export function createTicker(intervalMs: number, onTick: () => void): () => void {
  let stopped = false;
  let fallback: ReturnType<typeof setInterval> | null = null;
  let worker: Worker | null = null;
  let url: string | null = null;

  const useFallback = (): void => {
    if (!stopped && fallback === null) fallback = setInterval(onTick, intervalMs);
  };

  try {
    if (typeof Worker === "undefined" || typeof Blob === "undefined" || typeof URL.createObjectURL !== "function") throw new Error("sin Web Worker");
    url = URL.createObjectURL(new Blob([`setInterval(() => postMessage(0), ${String(intervalMs)});`], { type: "text/javascript" }));
    worker = new Worker(url);
    worker.onmessage = () => {
      if (!stopped) onTick();
    };
    worker.onerror = () => {
      worker?.terminate();
      worker = null;
      useFallback();
    };
  } catch {
    useFallback();
  }

  return () => {
    stopped = true;
    worker?.terminate();
    if (fallback !== null) clearInterval(fallback);
    if (url !== null) URL.revokeObjectURL(url);
  };
}
