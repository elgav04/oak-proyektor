import type { TextTheme } from "@/ipc/protocol";

/** Espejo del tema bíblico por defecto del backend (`BibleDisplaySettings.DefaultTheme`); solo lo usan las pruebas y el diagnóstico. El real llega por `bible.getSettings`. */
export const BIBLE_DEFAULT_THEME: TextTheme = {
  fontFamily: "Arial, sans-serif",
  fontSize: 64,
  color: "#FFFFFF",
  outlineWidth: 2,
  outlineColor: "#000000",
  shadowBlur: 8,
  shadowOffset: 3,
  shadowColor: "#000000CC",
  align: "center",
  verticalAlign: "middle",
  lineHeight: 1.3,
  marginTop: 70,
  marginRight: 120,
  marginBottom: 70,
  marginLeft: 120,
  autoFit: true,
  minFontSize: 40,
};
