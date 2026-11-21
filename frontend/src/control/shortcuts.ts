// MAPA ÚNICO de atajos del panel (CLAUDE.md: «Atajos de teclado centralizados»). Ningún componente declara teclas por su cuenta.
//
// No pueden chocar con los atajos GLOBALES del backend (RegisterHotKey, Ctrl+Alt+Shift+M/B/C/L/R): todos los de aquí van SIN Ctrl ni Alt, y
// `matchBibleShortcut` exige que los modificadores coincidan exactamente (Ctrl+Alt+Shift+B nunca se interpreta como nada de este mapa).
// Un test (`bible-shortcuts.test.mjs`) lo comprueba.

export const RESERVED_GLOBAL_SHORTCUTS = ["Ctrl+Alt+Shift+M", "Ctrl+Alt+Shift+B", "Ctrl+Alt+Shift+C", "Ctrl+Alt+Shift+L", "Ctrl+Alt+Shift+R"] as const;

export type BibleAction =
  | "focusSearch"
  | "verseNext"
  | "versePrev"
  | "extendNext"
  | "extendPrev"
  | "project"
  | "clear"
  | "pageNext"
  | "pagePrev";

export interface Shortcut {
  action: BibleAction;
  /** `KeyboardEvent.key`. */
  key: string;
  /** true = exige Mayús, false/ausente = sin Mayús, "any" = da igual (la «/» es Mayús+7 en el teclado español). */
  shift?: boolean | "any";
  /** Qué se muestra al operador. */
  label: string;
  description: string;
}

export const BIBLE_SHORTCUTS: readonly Shortcut[] = [
  { action: "focusSearch", key: "/", shift: "any", label: "/", description: "Ir al cuadro de búsqueda" },
  { action: "versePrev", key: "ArrowUp", label: "↑", description: "Versículo anterior" },
  { action: "verseNext", key: "ArrowDown", label: "↓", description: "Versículo siguiente" },
  { action: "extendPrev", key: "ArrowUp", shift: true, label: "Mayús+↑", description: "Ampliar la selección hacia arriba (varios versículos)" },
  { action: "extendNext", key: "ArrowDown", shift: true, label: "Mayús+↓", description: "Ampliar la selección hacia abajo (varios versículos)" },
  { action: "project", key: "Enter", label: "Enter", description: "Proyectar la selección" },
  { action: "clear", key: "Escape", label: "Esc", description: "Quitar el texto proyectado (en el cuadro de búsqueda: borrarlo)" },
  { action: "pageNext", key: "ArrowRight", label: "→", description: "Página siguiente del pasaje (la proyecta)" },
  { action: "pageNext", key: "PageDown", label: "Av Pág", description: "Página siguiente del pasaje (la proyecta)" },
  { action: "pagePrev", key: "ArrowLeft", label: "←", description: "Página anterior del pasaje (la proyecta)" },
  { action: "pagePrev", key: "PageUp", label: "Re Pág", description: "Página anterior del pasaje (la proyecta)" },
];

/** Datos mínimos de un `KeyboardEvent` (así se prueba sin DOM). */
export interface KeyInput {
  key: string;
  ctrlKey: boolean;
  altKey: boolean;
  metaKey: boolean;
  shiftKey: boolean;
}

export interface KeyTarget {
  tagName?: string;
  isContentEditable?: boolean;
}

const TYPING_TAGS = new Set(["INPUT", "TEXTAREA", "SELECT"]);
const BUTTON_TAGS = new Set(["BUTTON", "A"]);

/** ¿El foco está en un control donde las teclas escriben o eligen (cuadro de texto, lista desplegable)? */
export function isTypingTarget(target: KeyTarget | null | undefined): boolean {
  if (!target) return false;
  return TYPING_TAGS.has((target.tagName ?? "").toUpperCase()) || target.isContentEditable === true;
}

/**
 * Acción del mapa para una pulsación, o null. Con el foco en un cuadro de texto no se interpreta nada (la búsqueda maneja sus propias teclas);
 * con el foco en un botón, Enter es del botón. Los modificadores deben coincidir exactamente.
 */
export function matchBibleShortcut(event: KeyInput, target: KeyTarget | null | undefined): BibleAction | null {
  if (event.ctrlKey || event.altKey || event.metaKey) return null;
  if (isTypingTarget(target)) return null;
  const tag = (target?.tagName ?? "").toUpperCase();
  const found = BIBLE_SHORTCUTS.find((s) => s.key === event.key && (s.shift === "any" || (s.shift ?? false) === event.shiftKey));
  if (!found) return null;
  if (found.key === "Enter" && BUTTON_TAGS.has(tag)) return null;
  return found.action;
}

/** Teclas del cuadro de búsqueda (el único control de texto con atajos propios). */
export type SearchKeyAction = "moveDown" | "moveUp" | "submit" | "clear";

export function matchSearchKey(event: KeyInput): SearchKeyAction | null {
  if (event.ctrlKey || event.altKey || event.metaKey || event.shiftKey) return null;
  switch (event.key) {
    case "ArrowDown":
      return "moveDown";
    case "ArrowUp":
      return "moveUp";
    case "Enter":
      return "submit";
    case "Escape":
      return "clear";
    default:
      return null;
  }
}
