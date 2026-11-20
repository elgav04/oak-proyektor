import type { BackgroundKind, TextTheme } from "@/ipc/protocol";

/** Extensiones que el backend acepta (la validación real es del backend; esto solo elige el tipo de fondo al escribir la ruta). */
const VIDEO_EXTENSIONS = [".mp4", ".m4v", ".webm"];
const IMAGE_EXTENSIONS = [".jpg", ".jpeg", ".png", ".webp"];

/** «Copiar como ruta» de Windows entrega la ruta entre comillas: se quitan. */
export function cleanPath(raw: string): string {
  return raw.trim().replace(/^"(.*)"$/, "$1").trim();
}

/** Tipo de fondo según la extensión. Una extensión desconocida se manda como video: el backend responde con el motivo («Formato no soportado»). */
export function kindOfPath(path: string): Extract<BackgroundKind, "image" | "video"> {
  const lower = path.toLowerCase();
  return IMAGE_EXTENSIONS.some((e) => lower.endsWith(e)) && !VIDEO_EXTENSIONS.some((e) => lower.endsWith(e)) ? "image" : "video";
}

/** Temas básicos de prueba (medidas en píxeles del lienzo de 1920×1080). */
const BASE: TextTheme = {
  fontFamily: "Arial, sans-serif", fontSize: 72, color: "#FFFFFF", outlineWidth: 0, outlineColor: "#000000", shadowBlur: 8, shadowOffset: 3, shadowColor: "#000000CC",
  align: "center", verticalAlign: "middle", lineHeight: 1.25, marginTop: 80, marginRight: 120, marginBottom: 80, marginLeft: 120, autoFit: true, minFontSize: 28,
};

export const THEME_PRESETS: Record<string, { label: string; theme: TextTheme }> = {
  white: { label: "Blanco con sombra (centrado)", theme: BASE },
  yellow: { label: "Amarillo con contorno (centrado)", theme: { ...BASE, color: "#FFE14D", outlineWidth: 3, shadowBlur: 0, shadowOffset: 0 } },
  lower: { label: "Abajo a la izquierda, pequeño", theme: { ...BASE, fontSize: 48, align: "left", verticalAlign: "bottom", marginLeft: 100, marginBottom: 100, autoFit: false } },
};
