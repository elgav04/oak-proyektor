import type { SongSettingsPayload, TextTheme } from "@/ipc/protocol";

/** Espejo del tema de canciones por defecto del backend (`SongDisplaySettings.DefaultTheme`); lo usan las pruebas y la pantalla de tema. El real llega por `song.getSettings`. */
export const SONG_DEFAULT_THEME: TextTheme = {
  fontFamily: "Arial, sans-serif",
  fontSize: 60,
  color: "#FFFFFF",
  outlineWidth: 2,
  outlineColor: "#000000",
  shadowBlur: 8,
  shadowOffset: 3,
  shadowColor: "#000000CC",
  align: "center",
  verticalAlign: "middle",
  lineHeight: 1.25,
  marginTop: 70,
  marginRight: 120,
  marginBottom: 70,
  marginLeft: 120,
  autoFit: true,
  minFontSize: 36,
};

export const SONG_DEFAULT_SETTINGS: SongSettingsPayload = { theme: SONG_DEFAULT_THEME, transitionMs: 400, atEnd: "stay" };

/** Ejemplo del formato (palabras inventadas) que se muestra en un editor vacío. */
export const TEXT_PLACEHOLDER = "[Verso 1]\nPrimera línea de la estrofa\nSegunda línea de la estrofa\n\n[Coro]\nLínea del coro\nOtra línea del coro";
