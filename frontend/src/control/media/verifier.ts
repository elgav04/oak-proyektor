import type { IpcClient } from "@/ipc/client";
import type { MediaItemDto } from "@/ipc/protocol";
import { needsThumbnail, needsVerification } from "./model";

export type VerifierApi = Pick<IpcClient, "send">;

/** Lo que averigua el reproductor oculto del panel al cargar la COPIA local de un elemento. */
export interface ProbeOutcome {
  ok: boolean;
  durationMs: number | null;
  width: number | null;
  height: number | null;
  /** "unsupported" (el navegador no sabe decodificarlo) | "audioUnsupported" | "decode" | "timeout". */
  errorCode: string | null;
  /** Miniatura JPEG en base64 (sin el prefijo `data:`), si se pidió y se pudo sacar. */
  thumbnailBase64: string | null;
}

export type ProbeRunner = (item: MediaItemDto, wantThumbnail: boolean) => Promise<ProbeOutcome>;

const THUMB_W = 320;
const THUMB_H = 180;
/** Calidad de la miniatura; si pasa de este tamaño (200 KB es el tope del backend) se repite con menos calidad. */
const THUMB_MAX_B64 = 150_000;

function drawCover(ctx: CanvasRenderingContext2D, source: CanvasImageSource, sw: number, sh: number): void {
  const scale = Math.max(THUMB_W / sw, THUMB_H / sh);
  const w = sw * scale;
  const h = sh * scale;
  ctx.fillStyle = "#000";
  ctx.fillRect(0, 0, THUMB_W, THUMB_H);
  ctx.drawImage(source, (THUMB_W - w) / 2, (THUMB_H - h) / 2, w, h);
}

function jpegBase64(canvas: HTMLCanvasElement): string | null {
  for (const q of [0.7, 0.5, 0.3]) {
    const url = canvas.toDataURL("image/jpeg", q);
    const comma = url.indexOf(",");
    if (comma < 0 || !url.startsWith("data:image/jpeg")) return null;
    const b64 = url.slice(comma + 1);
    if (b64.length <= THUMB_MAX_B64) return b64;
  }
  return null;
}

/**
 * Comprobación REAL en el navegador del panel (sin ffmpeg): carga la copia local en un elemento oculto, espera los metadatos y, en un video, un fotograma decodificado (una búsqueda ya
 * decodifica el fotograma); saca la miniatura en un canvas de 320×180. Es la prueba de «esto se reproduce EN ESTE EQUIPO» (cubre HEVC, que depende de la tarjeta gráfica y de Windows).
 * Siempre libera el elemento (`src` fuera, `load()` y a un lado) y tiene un tope de tiempo: nunca deja un bucle ni un elemento vivo.
 */
export function createBrowserProbeRunner(doc: Document = document, timeoutMs = 8000): ProbeRunner {
  return (item, wantThumbnail) =>
    new Promise<ProbeOutcome>((resolve) => {
      const url = item.fileUrl;
      let settled = false;
      let timer: ReturnType<typeof setTimeout> | null = null;
      let cleanup: () => void = () => undefined;
      const finish = (outcome: ProbeOutcome): void => {
        if (settled) return;
        settled = true;
        if (timer) clearTimeout(timer);
        try { cleanup(); } catch { /* liberar nunca debe fallar la comprobación */ }
        resolve(outcome);
      };
      const fail = (errorCode: string): void => { finish({ ok: false, durationMs: null, width: null, height: null, errorCode, thumbnailBase64: null }); };
      if (!url) { fail("decode"); return; }
      timer = setTimeout(() => { fail("timeout"); }, timeoutMs);

      const canvas = doc.createElement("canvas");
      canvas.width = THUMB_W;
      canvas.height = THUMB_H;
      const thumb = (source: CanvasImageSource, sw: number, sh: number): string | null => {
        if (!wantThumbnail || sw <= 0 || sh <= 0) return null;
        try {
          const ctx = canvas.getContext("2d");
          if (!ctx) return null;
          drawCover(ctx, source, sw, sh);
          return jpegBase64(canvas);
        } catch { return null; }
      };

      if (item.kind === "image") {
        const img = doc.createElement("img");
        img.onload = () => { finish({ ok: true, durationMs: null, width: img.naturalWidth || null, height: img.naturalHeight || null, errorCode: null, thumbnailBase64: thumb(img, img.naturalWidth, img.naturalHeight) }); };
        img.onerror = () => { fail("decode"); };
        cleanup = () => { img.onload = null; img.onerror = null; img.removeAttribute("src"); };
        img.src = url;
        return;
      }

      const el = doc.createElement(item.kind === "video" ? "video" : "audio");
      el.muted = true;
      el.preload = "auto";
      const codeOf = (): string => (el.error?.code === 4 ? "unsupported" : "decode");
      el.addEventListener("error", () => { fail(codeOf()); });
      cleanup = () => { el.pause(); el.removeAttribute("src"); el.load(); };

      if (el instanceof HTMLVideoElement) {
        el.addEventListener("loadedmetadata", () => {
          const duration = Number.isFinite(el.duration) ? el.duration : 0;
          // Una búsqueda a ~10 % decodifica ese fotograma: es la prueba de que SE DECODIFICA (no solo que el contenedor se lee).
          el.currentTime = duration > 0 ? Math.min(Math.max(1, duration * 0.1), duration * 0.5) : 0;
        });
        el.addEventListener("seeked", () => {
          if (el.videoWidth <= 0) { fail("decode"); return; }
          const duration = Number.isFinite(el.duration) ? Math.round(el.duration * 1000) : null;
          finish({ ok: true, durationMs: duration, width: el.videoWidth, height: el.videoHeight, errorCode: null, thumbnailBase64: thumb(el, el.videoWidth, el.videoHeight) });
        });
      } else {
        el.addEventListener("canplay", () => {
          finish({ ok: true, durationMs: Number.isFinite(el.duration) ? Math.round(el.duration * 1000) : null, width: null, height: null, errorCode: null, thumbnailBase64: null });
        });
      }
      el.src = url;
    });
}

export interface MediaVerifier {
  /** Encola lo que haya que comprobar (sin verificar o sin miniatura). Cada elemento se intenta UNA vez por sesión: un fallo no se reintenta en bucle. */
  enqueue(items: readonly MediaItemDto[]): void;
  /** Id del elemento que se está comprobando ahora (null = nada). */
  current(): number | null;
  pending(): number;
  stop(): void;
}

export interface VerifierOptions {
  api: VerifierApi;
  run: ProbeRunner;
  /** Pausa entre un elemento y el siguiente: cede el hilo principal (por defecto 30 ms). */
  gapMs?: number;
  onChange?: () => void;
}

/**
 * Cola de verificación y miniaturas: de UNO EN UNO (un elemento de video decodificando a la vez), cediendo el hilo entre elementos. Manda `media.reportProbe` solo para los pendientes y
 * `media.setThumbnail` cuando falta la miniatura. Sin estado de negocio: el estado real (OK, no soportado, miniatura) lo guarda el backend y vuelve por `media.changed`.
 */
export function createMediaVerifier(options: VerifierOptions): MediaVerifier {
  const { api, run, gapMs = 30, onChange } = options;
  const verified = new Set<number>();
  const thumbed = new Set<number>();
  const queue: MediaItemDto[] = [];
  const queued = new Set<number>();
  let working = false;
  const flags = { stopped: false };
  const isStopped = (): boolean => flags.stopped;   // una función: `stop()` lo cambia desde fuera del bucle y TypeScript no debe dar el valor por constante
  let currentId: number | null = null;

  const next = async (): Promise<void> => {
    if (working) return;
    working = true;
    try {
      while (!isStopped()) {
        const item = queue.shift();
        if (!item) break;
        queued.delete(item.id);
        const doVerify = needsVerification(item) && !verified.has(item.id);
        const doThumb = needsThumbnail(item) && !thumbed.has(item.id);
        if (!doVerify && !doThumb) continue;
        currentId = item.id;
        onChange?.();
        if (doVerify) verified.add(item.id);
        if (doThumb) thumbed.add(item.id);
        let outcome: ProbeOutcome;
        try { outcome = await run(item, doThumb); }
        catch { outcome = { ok: false, durationMs: null, width: null, height: null, errorCode: "decode", thumbnailBase64: null }; }
        if (isStopped()) break;
        if (doVerify) api.send("media.reportProbe", { mediaId: item.id, ok: outcome.ok, durationMs: outcome.durationMs, width: outcome.width, height: outcome.height, errorCode: outcome.errorCode });
        if (doThumb && outcome.ok && outcome.thumbnailBase64) api.send("media.setThumbnail", { mediaId: item.id, jpegBase64: outcome.thumbnailBase64 });
        currentId = null;
        onChange?.();
        await new Promise<void>((r) => { setTimeout(r, gapMs); });   // cede el hilo principal entre elementos
      }
    } finally {
      currentId = null;
      working = false;
    }
  };

  return {
    enqueue(items) {
      for (const item of items) {
        if (queued.has(item.id)) continue;
        if ((needsVerification(item) && !verified.has(item.id)) || (needsThumbnail(item) && !thumbed.has(item.id))) {
          queue.push(item);
          queued.add(item.id);
        }
      }
      if (queue.length > 0) void next();
    },
    current: () => currentId,
    pending: () => queue.length,
    stop() { flags.stopped = true; queue.length = 0; queued.clear(); },
  };
}
