import assert from "node:assert/strict";
import test, { after } from "node:test";
import { closeServer, loadTs } from "./helper.mjs";

const sc = await loadTs("/src/control/shortcuts.ts");
after(closeServer);

const key = (k, mods = {}) => ({ key: k, ctrlKey: false, altKey: false, metaKey: false, shiftKey: false, ...mods });
const body = { tagName: "BODY" };
const area = { tagName: "TEXTAREA" };
const input = { tagName: "INPUT", type: "text" };
const button = { tagName: "BUTTON" };
const editor = { mode: "editor", orderFocused: false };
const order = { mode: "editor", orderFocused: true };
const list = { mode: "list", orderFocused: false };

test("Ctrl+S guarda aunque se esté escribiendo en el texto; Ctrl+Enter guarda y vuelve", () => {
  assert.equal(sc.matchSongShortcut(key("s", { ctrlKey: true }), area, editor), "save");
  assert.equal(sc.matchSongShortcut(key("S", { ctrlKey: true }), input, editor), "save", "con Bloq Mayús también");
  assert.equal(sc.matchSongShortcut(key("Enter", { ctrlKey: true }), area, editor), "saveAndClose");
  assert.equal(sc.matchSongShortcut(key("s", { ctrlKey: true }), body, list), null, "en la lista no hay nada que guardar");
});

test("escribiendo en un cuadro de texto no se interpreta ninguna otra tecla", () => {
  for (const k of ["Escape", "ArrowUp", "ArrowDown", "Delete", "/", "Enter"]) {
    assert.equal(sc.matchSongShortcut(key(k), area, editor), null, `${k} en el texto del editor`);
    assert.equal(sc.matchSongShortcut(key(k), input, list), null, `${k} en el cuadro de búsqueda (lo maneja la propia búsqueda)`);
  }
  assert.equal(sc.matchSongShortcut(key("n", { ctrlKey: true }), input, list), null);
});

test("lista: flechas, Enter, Supr, «/» (con o sin Mayús) y Ctrl+N", () => {
  assert.equal(sc.matchSongShortcut(key("ArrowDown"), body, list), "listNext");
  assert.equal(sc.matchSongShortcut(key("ArrowUp"), body, list), "listPrev");
  assert.equal(sc.matchSongShortcut(key("Enter"), body, list), "listOpen");
  assert.equal(sc.matchSongShortcut(key("Delete"), body, list), "listDelete");
  assert.equal(sc.matchSongShortcut(key("/"), body, list), "focusSearch");
  assert.equal(sc.matchSongShortcut(key("/", { shiftKey: true }), body, list), "focusSearch", "en el teclado español «/» es Mayús+7");
  assert.equal(sc.matchSongShortcut(key("n", { ctrlKey: true }), body, list), "newSong");
  assert.equal(sc.matchSongShortcut(key("Enter"), button, list), null, "Enter sobre un botón es del botón");
});

test("editor: Esc cierra (si no se escribe) y las teclas de la lista no valen", () => {
  assert.equal(sc.matchSongShortcut(key("Escape"), body, editor), "closeEditor");
  assert.equal(sc.matchSongShortcut(key("ArrowDown"), body, editor), null);
  assert.equal(sc.matchSongShortcut(key("Delete"), body, editor), null, "Supr solo actúa sobre una fila del orden");
  assert.equal(sc.matchSongShortcut(key("Escape"), body, list), null, "en la lista, Esc no hace nada");
});

test("orden: con el foco en una fila, Alt+↑/↓ mueve, Supr quita, Ctrl+D repite y las flechas cambian de fila", () => {
  assert.equal(sc.matchSongShortcut(key("ArrowUp", { altKey: true }), body, order), "orderMoveUp");
  assert.equal(sc.matchSongShortcut(key("ArrowDown", { altKey: true }), body, order), "orderMoveDown");
  assert.equal(sc.matchSongShortcut(key("Delete"), body, order), "orderRemove");
  assert.equal(sc.matchSongShortcut(key("d", { ctrlKey: true }), body, order), "orderRepeat");
  assert.equal(sc.matchSongShortcut(key("ArrowUp"), body, order), "orderPrev");
  assert.equal(sc.matchSongShortcut(key("ArrowDown"), body, order), "orderNext");
  // Sin el foco en una fila del orden, esas teclas no existen.
  assert.equal(sc.matchSongShortcut(key("ArrowUp", { altKey: true }), body, editor), null);
  assert.equal(sc.matchSongShortcut(key("d", { ctrlKey: true }), body, editor), null);
});

test("los modificadores deben coincidir EXACTAMENTE", () => {
  assert.equal(sc.matchSongShortcut(key("s"), body, editor), null, "«s» sola no guarda");
  assert.equal(sc.matchSongShortcut(key("s", { ctrlKey: true, shiftKey: true }), body, editor), null);
  assert.equal(sc.matchSongShortcut(key("s", { metaKey: true }), body, editor), null);
  assert.equal(sc.matchSongShortcut(key("ArrowDown", { shiftKey: true }), body, list), null);
  assert.equal(sc.matchSongShortcut(key("ArrowUp", { altKey: true }), body, order) !== sc.matchSongShortcut(key("ArrowUp"), body, order), true);
});

test("los atajos GLOBALES del backend (Ctrl+Alt+Shift+M/B/C/L/R) no activan nada en ningún contexto y el panel no los consume", () => {
  for (const k of ["M", "B", "C", "L", "R", "m", "b", "c", "l", "r"]) {
    for (const ctx of [list, editor, order]) {
      for (const target of [body, area, input, button]) {
        assert.equal(sc.matchSongShortcut(key(k, { ctrlKey: true, altKey: true, shiftKey: true }), target, ctx), null, `Ctrl+Alt+Shift+${k}`);
      }
    }
  }
  assert.deepEqual(sc.RESERVED_GLOBAL_SHORTCUTS, ["Ctrl+Alt+Shift+M", "Ctrl+Alt+Shift+B", "Ctrl+Alt+Shift+C", "Ctrl+Alt+Shift+L", "Ctrl+Alt+Shift+R"]);
});

test("ningún atajo del mapa usa Ctrl+Alt a la vez y no hay dos con la misma pulsación en el mismo ámbito", () => {
  const signature = (s) => `${s.scope}|${s.key.toLowerCase()}|${s.ctrl ?? false}|${s.alt ?? false}|${s.shift ?? false}`;
  const seen = new Set();
  for (const s of sc.SONG_SHORTCUTS) {
    assert.ok(!(s.ctrl && s.alt), `${s.label} usa Ctrl y Alt: es territorio de los globales`);
    assert.ok(s.label.length > 0 && s.description.length > 0);
    assert.ok(!seen.has(signature(s)), `${s.label} está repetido en el ámbito ${s.scope}`);
    seen.add(signature(s));
  }
});

test("las teclas con Ctrl o Alt de las canciones no las interpreta la pestaña de la Biblia, y los mapas son independientes", () => {
  for (const s of sc.SONG_SHORTCUTS.filter((x) => x.ctrl || x.alt)) {
    const event = key(s.key, { ctrlKey: s.ctrl ?? false, altKey: s.alt ?? false, shiftKey: s.shift === true });
    assert.equal(sc.matchBibleShortcut(event, body), null, `${s.label} no debe activar nada en la Biblia`);
  }
  // Y a la inversa: Ctrl+S, Ctrl+N… no son atajos de la Biblia.
  for (const b of sc.BIBLE_SHORTCUTS) assert.ok(!(b.key.length === 1 && b.key !== "/"), "la Biblia no usa letras sueltas");
  assert.notEqual(sc.SONG_SHORTCUTS.length, 0);
});
