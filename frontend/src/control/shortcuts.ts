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
  /** `type` de un `<input>` (text, checkbox…): solo los de texto cuentan como escritura. */
  type?: string;
}

const TYPING_TAGS = new Set(["INPUT", "TEXTAREA", "SELECT"]);
const BUTTON_TAGS = new Set(["BUTTON", "A"]);

/** `<input>` que NO son de escritura: pulsarlos deja el foco en ellos, pero las flechas, Enter, etc. no les sirven de nada (H12). */
const NON_TYPING_INPUT_TYPES = new Set(["checkbox", "button", "submit", "reset", "image", "file", "color"]);

/**
 * ¿El foco está en un control donde las teclas escriben o eligen (cuadro de texto, lista desplegable)? Una casilla, un botón o un selector de color NO:
 * tras pulsar «Proyectar al navegar» los atajos siguen funcionando. (La lista desplegable sí cuenta: ↑/↓ cambian su valor; por eso el selector de
 * Biblia suelta el foco al elegir.)
 */
export function isTypingTarget(target: KeyTarget | null | undefined): boolean {
  if (!target) return false;
  const tag = (target.tagName ?? "").toUpperCase();
  if (tag === "INPUT") return !NON_TYPING_INPUT_TYPES.has((target.type ?? "text").toLowerCase());
  return TYPING_TAGS.has(tag) || target.isContentEditable === true;
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

// ───────────────────────── Pestaña de Canciones ─────────────────────────
// Mismo criterio que la Biblia: nunca Ctrl+Alt+Shift (los globales del backend) y los modificadores deben coincidir EXACTAMENTE. Algunas llevan Ctrl o Alt porque
// funcionan escribiendo en el cuadro de texto (guardar) o sobre una fila de la lista de orden (mover). Solo hay UNA pestaña montada a la vez, así que sus teclas
// simples (flechas, Enter, Esc, «/») nunca coinciden con las de la Biblia al mismo tiempo; los atajos con Ctrl/Alt son propios de esta pestaña. Un test lo comprueba.

export type SongAction =
  | "save"
  | "saveAndClose"
  | "closeEditor"
  | "newSong"
  | "focusSearch"
  | "listPrev"
  | "listNext"
  | "listOpen"
  | "listDelete"
  | "slideProject"
  | "slideNext"
  | "slidePrev"
  | "slideClear"
  | "orderPrev"
  | "orderNext"
  | "orderMoveUp"
  | "orderMoveDown"
  | "orderRemove"
  | "orderRepeat";

/** Dónde vale cada atajo: `list` = lista de canciones; `editor` = editor abierto; `order` = el foco está en una fila del orden de reproducción. */
export type SongScope = "list" | "editor" | "order";

export interface SongShortcut {
  action: SongAction;
  key: string;
  ctrl?: boolean;
  alt?: boolean;
  shift?: boolean | "any";
  scope: SongScope;
  /** true = también vale con el foco en un cuadro de texto (guardar). */
  whileTyping?: boolean;
  label: string;
  description: string;
}

export const SONG_SHORTCUTS: readonly SongShortcut[] = [
  { action: "save", key: "s", ctrl: true, scope: "editor", whileTyping: true, label: "Ctrl+S", description: "Guardar en la canción" },
  { action: "saveAndClose", key: "Enter", ctrl: true, scope: "editor", whileTyping: true, label: "Ctrl+Enter", description: "Guardar y volver a la lista" },
  { action: "closeEditor", key: "Escape", scope: "editor", label: "Esc", description: "Cerrar el editor (avisa si hay cambios sin guardar)" },
  { action: "newSong", key: "n", ctrl: true, scope: "list", label: "Ctrl+N", description: "Canción nueva" },
  { action: "focusSearch", key: "/", shift: "any", scope: "list", label: "/", description: "Ir al cuadro de búsqueda" },
  { action: "listPrev", key: "ArrowUp", scope: "list", label: "↑", description: "Canción anterior de la lista (muestra su tira de diapositivas; no proyecta)" },
  { action: "listNext", key: "ArrowDown", scope: "list", label: "↓", description: "Canción siguiente de la lista" },
  { action: "listOpen", key: "e", ctrl: true, scope: "list", label: "Ctrl+E", description: "Editar la canción seleccionada (también: doble clic o el botón Editar)" },
  // Proyección (pantalla de la lista): la diapositiva seleccionada de la canción seleccionada. Mismas teclas de página que la Biblia.
  { action: "slideProject", key: "Enter", scope: "list", label: "Enter", description: "Proyectar la diapositiva seleccionada" },
  { action: "slideNext", key: "ArrowRight", scope: "list", label: "→", description: "Diapositiva siguiente (la proyecta; al final se queda en la última: «FIN»)" },
  { action: "slideNext", key: "PageDown", scope: "list", label: "Av Pág", description: "Diapositiva siguiente (la proyecta)" },
  { action: "slidePrev", key: "ArrowLeft", scope: "list", label: "←", description: "Diapositiva anterior (la proyecta)" },
  { action: "slidePrev", key: "PageUp", scope: "list", label: "Re Pág", description: "Diapositiva anterior (la proyecta)" },
  { action: "slideClear", key: "Escape", scope: "list", label: "Esc", description: "Quitar el texto de la pantalla (output.clearContent)" },
  { action: "listDelete", key: "Delete", scope: "list", label: "Supr", description: "Borrar la canción (pide confirmación)" },
  { action: "orderPrev", key: "ArrowUp", scope: "order", label: "↑", description: "Elemento anterior del orden" },
  { action: "orderNext", key: "ArrowDown", scope: "order", label: "↓", description: "Elemento siguiente del orden" },
  { action: "orderMoveUp", key: "ArrowUp", alt: true, scope: "order", label: "Alt+↑", description: "Subir el elemento del orden" },
  { action: "orderMoveDown", key: "ArrowDown", alt: true, scope: "order", label: "Alt+↓", description: "Bajar el elemento del orden" },
  { action: "orderRemove", key: "Delete", scope: "order", label: "Supr", description: "Quitar el elemento del orden (la sección y su texto se conservan)" },
  { action: "orderRepeat", key: "d", ctrl: true, scope: "order", label: "Ctrl+D", description: "Repetir la sección justo después (sin duplicar el texto)" },
];

export interface SongKeyContext {
  /** Qué hay en pantalla: la lista o el editor. */
  mode: "list" | "editor";
  /** El foco está en una fila de la lista de orden. */
  orderFocused: boolean;
}

/** Acción de la pestaña de Canciones para una pulsación, o null. Ctrl+Alt+Shift (los globales) nunca coincide con nada. */
export function matchSongShortcut(event: KeyInput, target: KeyTarget | null | undefined, context: SongKeyContext): SongAction | null {
  if (event.metaKey) return null;
  if (event.ctrlKey && event.altKey) return null;   // incluye Ctrl+Alt+Shift+M/B/C/L/R, que son del backend
  const typing = isTypingTarget(target);
  const tag = (target?.tagName ?? "").toUpperCase();
  const key = event.key.length === 1 ? event.key.toLowerCase() : event.key;
  for (const s of SONG_SHORTCUTS) {
    if (s.key !== key) continue;
    if ((s.ctrl ?? false) !== event.ctrlKey || (s.alt ?? false) !== event.altKey) continue;
    if (s.shift !== "any" && (s.shift ?? false) !== event.shiftKey) continue;
    if (s.scope === "list" && context.mode !== "list") continue;
    if (s.scope === "editor" && context.mode !== "editor") continue;
    if (s.scope === "order" && !(context.mode === "editor" && context.orderFocused)) continue;
    if (typing && !s.whileTyping) continue;
    if (s.key === "Enter" && !s.ctrl && BUTTON_TAGS.has(tag)) continue;   // Enter sobre un botón es del botón
    return s.action;
  }
  return null;
}

// ───────────────────────── Pestaña «Lista» (modo EN VIVO) ─────────────────────────
// Misma regla que las demás: nunca Ctrl+Alt+Shift (los globales M/B/C/L/R) y los modificadores deben coincidir EXACTAMENTE. Con el foco en un cuadro de texto no se
// interpreta NADA (Ctrl+←/→ mueven el cursor por palabras y Ctrl+E no debe cambiar de modo a medio escribir: verificado con teclado real que llegan a la página sin
// que WebView2 se las quede). Solo una pestaña está montada a la vez, así que sus teclas simples no chocan con las de la Biblia ni las de Canciones.
// Ctrl+E es la MISMA tecla que «editar» en Canciones (otra pestaña): aquí alterna EN VIVO / EDITAR. En modo EDITAR solo Ctrl+E está activo (el resto no proyecta nada).

export type ListAction =
  | "cursorPrev"
  | "cursorNext"
  | "activate"
  | "slideNext"
  | "slidePrev"
  | "itemNext"
  | "itemPrev"
  | "clear"
  | "toggleMode";

export interface ListShortcut {
  action: ListAction;
  key: string;
  ctrl?: boolean;
  /** `live` = solo en modo EN VIVO; `any` = también en modo EDITAR. */
  scope: "live" | "any";
  label: string;
  description: string;
}

export const LIST_SHORTCUTS: readonly ListShortcut[] = [
  { action: "cursorPrev", key: "ArrowUp", scope: "live", label: "↑", description: "Elemento anterior de la lista (mueve el cursor; NO proyecta)" },
  { action: "cursorNext", key: "ArrowDown", scope: "live", label: "↓", description: "Elemento siguiente de la lista (mueve el cursor; NO proyecta)" },
  { action: "activate", key: "Enter", scope: "live", label: "Enter", description: "Activar el elemento del cursor (proyecta su primera diapositiva o página)" },
  { action: "slideNext", key: "ArrowRight", scope: "live", label: "→", description: "Diapositiva o página siguiente del elemento (al final: «FIN · siguiente: …», según el ajuste)" },
  { action: "slideNext", key: "PageDown", scope: "live", label: "Av Pág", description: "Diapositiva o página siguiente del elemento" },
  { action: "slidePrev", key: "ArrowLeft", scope: "live", label: "←", description: "Diapositiva o página anterior del elemento" },
  { action: "slidePrev", key: "PageUp", scope: "live", label: "Re Pág", description: "Diapositiva o página anterior del elemento" },
  { action: "itemNext", key: "ArrowRight", ctrl: true, scope: "live", label: "Ctrl+→", description: "Saltar al elemento siguiente que se pueda proyectar (salta faltantes y notas) y proyectarlo" },
  { action: "itemPrev", key: "ArrowLeft", ctrl: true, scope: "live", label: "Ctrl+←", description: "Saltar al elemento anterior que se pueda proyectar y proyectarlo" },
  { action: "clear", key: "Escape", scope: "live", label: "Esc", description: "Quitar el texto de la pantalla (output.clearContent); no cambia el elemento activo" },
  { action: "toggleMode", key: "e", ctrl: true, scope: "any", label: "Ctrl+E", description: "Alternar entre el modo EN VIVO y el modo EDITAR" },
];

export interface ListKeyContext {
  mode: "live" | "edit";
}

/** Acción de la pestaña «Lista» para una pulsación, o null. Ctrl+Alt+Shift (los globales) nunca coincide; con el foco en un cuadro de texto nunca coincide nada. */
export function matchListShortcut(event: KeyInput, target: KeyTarget | null | undefined, context: ListKeyContext): ListAction | null {
  if (event.metaKey || event.altKey) return null;
  if (isTypingTarget(target)) return null;
  const tag = (target?.tagName ?? "").toUpperCase();
  const key = event.key.length === 1 ? event.key.toLowerCase() : event.key;
  for (const s of LIST_SHORTCUTS) {
    if (s.key !== key) continue;
    if ((s.ctrl ?? false) !== event.ctrlKey || event.shiftKey) continue;
    if (s.scope === "live" && context.mode !== "live") continue;
    if (s.key === "Enter" && BUTTON_TAGS.has(tag)) continue;   // Enter sobre un botón es del botón
    return s.action;
  }
  return null;
}