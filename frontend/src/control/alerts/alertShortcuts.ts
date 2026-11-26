// Atajos del panel para las alertas: CONFIGURABLES (se guardan en el almacenamiento local de la página, de mejor esfuerzo) y centralizados aquí, como el resto de atajos.
// Por defecto: Ctrl+Mayús+K = ir al cuadro de alertas y Ctrl+Mayús+H = quitar la alerta. Valen también con el foco en un cuadro de texto.
// Reglas que se comprueban al configurarlos (`validateCombo`): llevan Ctrl y NO Alt (los globales son Ctrl+Alt+Shift+M/B/C/L/R/A), no pisan los de Biblia, Canciones y Lista,
// ni los que Chromium/WebView2 reserva para el navegador (Ctrl+Mayús+Q, N, T, W, I, J…), ni se repiten entre sí.

export type AlertShortcutAction = "focus" | "hide";
export type AlertShortcutBindings = Record<AlertShortcutAction, string>;

export const DEFAULT_ALERT_SHORTCUTS: AlertShortcutBindings = { focus: "Ctrl+Shift+K", hide: "Ctrl+Shift+H" };

export const ALERT_SHORTCUT_LABELS: Record<AlertShortcutAction, string> = { focus: "Ir al cuadro de alertas", hide: "Quitar la alerta" };

/** Combinaciones que ya usa el panel (mapa único `shortcuts.ts` y las pestañas) o el sistema de ventanas: no se pueden asignar a las alertas. */
export const TAKEN_BY_PANEL: readonly string[] = [
  "Ctrl+Shift+A",                                                    // Biblia: añadir a la lista
  "Ctrl+S", "Ctrl+Enter", "Ctrl+N", "Ctrl+E", "Ctrl+D", "Ctrl+Z", "Ctrl+Left", "Ctrl+Right", "Ctrl+L",   // Canciones y Lista
];

/** Combinaciones que Chromium/WebView2 reserva para el navegador (verificar con teclado real antes de elegir otra). */
export const RESERVED_BY_BROWSER: readonly string[] = [
  "Ctrl+Shift+Q", "Ctrl+Shift+N", "Ctrl+Shift+T", "Ctrl+Shift+W", "Ctrl+Shift+I", "Ctrl+Shift+J", "Ctrl+Shift+C", "Ctrl+Shift+B", "Ctrl+Shift+O", "Ctrl+Shift+P",
  "Ctrl+Shift+R", "Ctrl+Shift+Delete", "Ctrl+Shift+Tab", "Ctrl+Shift+M", "Ctrl+Shift+D", "Ctrl+Shift+S", "Ctrl+Shift+L", "Ctrl+Shift+E", "Ctrl+Shift+F",
];

/** Los globales del backend (RegisterHotKey): Ctrl+Alt+Shift+… Aquí no se admite Alt, así que nunca pueden coincidir. */
export const RESERVED_GLOBALS: readonly string[] = ["Ctrl+Alt+Shift+M", "Ctrl+Alt+Shift+B", "Ctrl+Alt+Shift+C", "Ctrl+Alt+Shift+L", "Ctrl+Alt+Shift+R", "Ctrl+Alt+Shift+A"];

export interface KeyInput {
  key: string;
  ctrlKey: boolean;
  altKey: boolean;
  metaKey: boolean;
  shiftKey: boolean;
}

export interface Combo {
  key: string;   // una letra o un dígito en minúscula
  ctrl: boolean;
  shift: boolean;
}

const KEY_RE = /^[a-z0-9]$/;

/** «Ctrl+Shift+K» → `{ key: "k", ctrl, shift }`; null si no es una combinación admitida (Alt, Meta, sin Ctrl o una tecla que no es letra/dígito). */
export function parseCombo(text: string): Combo | null {
  const parts = text.split("+").map((p) => p.trim()).filter((p) => p.length > 0);
  if (parts.length < 2) return null;
  const key = parts[parts.length - 1]?.toLowerCase() ?? "";
  const mods = parts.slice(0, -1).map((m) => m.toLowerCase());
  if (!KEY_RE.test(key)) return null;
  if (mods.some((m) => m !== "ctrl" && m !== "shift")) return null;
  const ctrl = mods.includes("ctrl");
  if (!ctrl || new Set(mods).size !== mods.length) return null;
  return { key, ctrl, shift: mods.includes("shift") };
}

export function comboText(c: Combo): string {
  return `Ctrl+${c.shift ? "Shift+" : ""}${c.key.toUpperCase()}`;
}

/** La combinación pulsada, o null si lleva Alt/Meta o la tecla no es una letra o un dígito (así se «graba» un atajo nuevo). */
export function comboOfEvent(e: KeyInput): string | null {
  if (e.altKey || e.metaKey || !e.ctrlKey) return null;
  const key = e.key.toLowerCase();
  if (!KEY_RE.test(key)) return null;
  return comboText({ key, ctrl: true, shift: e.shiftKey });
}

const canon = (text: string): string => {
  const c = parseCombo(text);
  return c ? comboText(c) : text;
};

/** Motivo por el que NO se puede asignar `text` (sin contar `ignore`, la acción que se está reasignando), o null si vale. */
export function validateCombo(text: string, others: readonly string[] = []): string | null {
  const c = parseCombo(text);
  if (!c) return "Usa Ctrl (y opcionalmente Mayús) con una letra o un dígito; sin Alt.";
  const t = comboText(c);
  if (!c.shift) return "Añade Mayús: Ctrl+letra sola la usan el navegador y los editores.";
  if (RESERVED_GLOBALS.includes(t)) return "Es un atajo global del programa.";
  if (TAKEN_BY_PANEL.map(canon).includes(t)) return "Ya la usa el panel (Biblia, Canciones o Lista).";
  if (RESERVED_BY_BROWSER.map(canon).includes(t)) return "El navegador integrado (WebView2) la reserva y no llega a la página.";
  if (others.map(canon).includes(t)) return "Ya está asignada a otra acción de las alertas.";
  return null;
}

/** ¿Qué acción de las alertas pide esta pulsación? Los modificadores deben coincidir EXACTAMENTE (Ctrl+Alt+Shift+K, p. ej., nunca coincide). */
export function matchAlertShortcut(e: KeyInput, bindings: AlertShortcutBindings): AlertShortcutAction | null {
  if (e.altKey || e.metaKey || !e.ctrlKey) return null;
  const key = e.key.toLowerCase();
  for (const action of ["focus", "hide"] as const) {
    const c = parseCombo(bindings[action]);
    if (c && c.key === key && c.shift === e.shiftKey) return action;
  }
  return null;
}

export interface BindingsStorage {
  get(): string | null;
  set(value: string): void;
}

/** Atajos guardados (de mejor esfuerzo): lo que no sea válido vuelve a su valor por defecto, uno por uno. */
export function loadBindings(storage: BindingsStorage | null): AlertShortcutBindings {
  const result = { ...DEFAULT_ALERT_SHORTCUTS };
  try {
    const raw = storage?.get();
    if (!raw) return result;
    const parsed = JSON.parse(raw) as Partial<Record<string, unknown>>;
    for (const action of ["focus", "hide"] as const) {
      const value = parsed[action];
      if (typeof value !== "string") continue;
      const other = action === "focus" ? "hide" : "focus";
      const otherValue = typeof parsed[other] === "string" ? parsed[other] : DEFAULT_ALERT_SHORTCUTS[other];
      if (validateCombo(value, [otherValue]) === null) result[action] = canon(value);
    }
    if (canon(result.focus) === canon(result.hide)) return { ...DEFAULT_ALERT_SHORTCUTS };
  } catch {
    return { ...DEFAULT_ALERT_SHORTCUTS };
  }
  return result;
}

export function saveBindings(storage: BindingsStorage | null, bindings: AlertShortcutBindings): void {
  try { storage?.set(JSON.stringify(bindings)); } catch { /* comodidad: si no se puede guardar, vale hasta recargar */ }
}
