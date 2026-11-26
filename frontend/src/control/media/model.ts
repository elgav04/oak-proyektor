import type { MediaItemDto, MediaItemKind } from "@/ipc/protocol";

/** Alto fijo de cada fila de la lista virtualizada. */
export const MEDIA_ROW_HEIGHT = 68;

export const KIND_LABELS: Record<MediaItemKind, string> = { video: "Video", audio: "Audio", image: "Imagen" };

/** «1,4 GB», «320 MB», «45 KB». Base 1024 (como el sistema operativo). */
export function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes < 0) return "—";
  const units = ["B", "KB", "MB", "GB", "TB"];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  const text = unit === 0 || value >= 100 ? String(Math.round(value)) : value.toFixed(1).replace(".", ",");
  return `${text} ${units[unit] ?? "B"}`;
}

/** «0:07», «3:25», «1:02:09». Nulo = duración desconocida. */
export function formatDuration(ms: number | null): string {
  if (ms === null || !Number.isFinite(ms) || ms < 0) return "";
  const total = Math.round(ms / 1000);
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  const pad = (n: number): string => String(n).padStart(2, "0");
  return h > 0 ? `${String(h)}:${pad(m)}:${pad(s)}` : `${String(m)}:${pad(s)}`;
}

export type BadgeTone = "ok" | "warn" | "bad" | "idle";

/** Qué dice la insignia de estado de un elemento y con qué tono. Un elemento `ok` con aviso (HEVC…) se muestra en ámbar con el texto del aviso. */
export function statusBadge(item: Pick<MediaItemDto, "status" | "statusMessage">): { text: string; tone: BadgeTone; detail: string | null } {
  switch (item.status) {
    case "pending":
      return { text: "Verificando…", tone: "idle", detail: item.statusMessage };
    case "ok":
      return item.statusMessage ? { text: "Aviso", tone: "warn", detail: item.statusMessage } : { text: "Listo", tone: "ok", detail: null };
    case "unsupported":
      return { text: "No se puede usar", tone: "bad", detail: item.statusMessage };
    case "missing":
      return { text: "Falta el archivo", tone: "bad", detail: item.statusMessage };
  }
}

/** ¿Se puede usar como fondo? (video o imagen, ni faltante ni no soportado). */
export function canBeBackground(item: Pick<MediaItemDto, "kind" | "status">): boolean {
  return item.kind !== "audio" && (item.status === "ok" || item.status === "pending");
}

/** ¿Hay algo que comprobar en el reproductor del panel? (aún sin verificar, o sin miniatura y con archivo). */
export function needsVerification(item: Pick<MediaItemDto, "status" | "fileUrl">): boolean {
  return item.status === "pending" && item.fileUrl !== null;
}

export function needsThumbnail(item: Pick<MediaItemDto, "kind" | "status" | "fileUrl" | "thumbnailUrl">): boolean {
  return item.kind !== "audio" && item.thumbnailUrl === null && item.fileUrl !== null && (item.status === "ok" || item.status === "pending");
}

/** Resumen de las dimensiones y códecs para la fila («1920×1080 · H.264 + AAC»). */
export function techLine(item: Pick<MediaItemDto, "kind" | "width" | "height" | "videoCodec" | "audioCodec" | "ext" | "hasAudio">): string {
  const parts: string[] = [];
  if (item.width !== null && item.height !== null) parts.push(`${String(item.width)}×${String(item.height)}`);
  const codecs = [item.videoCodec, item.audioCodec].filter((c): c is string => c !== null).join(" + ");
  if (codecs) parts.push(codecs);
  if (item.kind === "video" && !item.hasAudio) parts.push("sin audio");
  if (parts.length === 0) parts.push(item.ext.replace(".", "").toUpperCase());
  return parts.join(" · ");
}
