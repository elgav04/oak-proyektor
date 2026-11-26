import assert from "node:assert/strict";
import test, { after } from "node:test";
import { closeServer, loadTs } from "./helper.mjs";

// Funciones puras de la pestaña «Lista»: elemento activo derivado del origin, insignia, salto entre elementos y lista por defecto. Datos sintéticos.
const { activeFromOutput, listLiveStatus, badgeText, stepItem, pickDefaultList, localDate, whyNotProjectable } = await loadTs("/src/control/lists/active.ts");
const { matchListShortcut, matchListSearchKey, LIST_SHORTCUTS, RESERVED_GLOBAL_SHORTCUTS } = await loadTs("/src/control/shortcuts.ts");
after(closeServer);

const output = ({ origin = null, master = "live", isOpen = true, isPaused = false, text = "texto sintético" } = {}) => ({
  seq: 1,
  state: { master, content: { slide: origin === undefined ? null : { id: "s", blocks: [{ text }], theme: {}, origin }, transitionMs: 0 } },
  window: { isOpen, isPaused },
});
const list = { id: 7, items: [{ id: 11, itemType: "song" }, { id: 12, itemType: "song" }, { id: 13, itemType: "passage" }, { id: 14, itemType: "note" }] };
const org = (itemId, position = 0, itemType = "song", playlistId = 7) => ({ playlistId, itemId, itemType, position });

// ───────────── elemento activo ─────────────

test("el elemento activo sale del origin confirmado: la MISMA canción dos veces se distingue por el id del elemento, no por el texto", () => {
  // Dos elementos con la misma canción (mismo texto): solo uno es el activo.
  assert.deepEqual(activeFromOutput(output({ origin: org(12, 3) }), list), { itemId: 12, position: 3 });
  assert.deepEqual(activeFromOutput(output({ origin: org(11, 0) }), list), { itemId: 11, position: 0 });
  // El texto es irrelevante: el mismo origin con otro texto sigue siendo el mismo elemento.
  assert.deepEqual(activeFromOutput(output({ origin: org(12, 1), text: "otro texto" }), list), { itemId: 12, position: 1 });
});

test("sin origin, de otra lista, de un elemento que ya no existe o de otro tipo: nada de la lista está activo", () => {
  assert.equal(activeFromOutput(null, list), null);
  assert.equal(activeFromOutput(output({ origin: null }), list), null, "proyección suelta (Biblia o Canciones): sin origen");
  assert.equal(activeFromOutput(output({ origin: undefined }), list), null, "sin slide");
  assert.equal(activeFromOutput(output({ origin: org(11, 0, "song", 99) }), list), null, "de otra lista");
  assert.equal(activeFromOutput(output({ origin: org(999, 0) }), list), null, "elemento inexistente");
  assert.equal(activeFromOutput(output({ origin: org(11, 0, "passage") }), list), null, "el tipo no coincide");
  assert.equal(activeFromOutput(output({ origin: org(11, 0) }), null), null, "sin lista abierta");
});

test("la insignia sale del estado confirmado: EN VIVO solo si se ve; OCULTO con el motivo; LISTO si nada de la lista está en pantalla", () => {
  const active = { itemId: 11, position: 0 };
  const status = (o) => badgeText(listLiveStatus(o, active));
  assert.equal(status(output({ origin: org(11) })), "EN VIVO");
  assert.equal(status(output({ origin: org(11), master: "black" })), "OCULTO · Negro");
  assert.equal(status(output({ origin: org(11), master: "clearText" })), "OCULTO · Texto limpio");
  assert.equal(status(output({ origin: org(11), master: "logo" })), "OCULTO · Logo");
  assert.equal(status(output({ origin: org(11), master: "freeze" })), "OCULTO · Congelado");
  assert.equal(status(output({ origin: org(11), isPaused: true })), "OCULTO · Salida en pausa");
  assert.equal(status(output({ origin: org(11), isOpen: false })), "OCULTO · Salida cerrada");
  assert.equal(badgeText(listLiveStatus(output({ origin: org(11) }), null)), "LISTO");
  assert.equal(badgeText(listLiveStatus(null, active)), "LISTO");
});

// ───────────── saltar entre elementos ─────────────

const item = (id, over = {}) => ({ id, itemType: "song", title: `Elemento ${id}`, isMissing: false, projectable: true, ...over });

test("siguiente y anterior SALTAN faltantes, notas y pasajes sin resolver, avisando de los que importan, y nunca los quitan de la lista", () => {
  const items = [
    item(1),
    item(2, { isMissing: true, projectable: false }),
    item(3, { itemType: "note", projectable: false }),
    item(4, { itemType: "passage", projectable: false }),
    item(5, { itemType: "passage" }),
    item(6, { isMissing: true, projectable: false }),
    item(7),
  ];
  const fwd = stepItem(items, 0, 1);
  assert.equal(fwd.index, 4);
  assert.deepEqual(fwd.skipped, ["Falta la canción «Elemento 2»", "No se puede proyectar el pasaje «Elemento 4» (Biblia no instalada o sin ese pasaje)"], "la nota se salta sin avisar");
  assert.equal(stepItem(items, 4, 1).index, 6);
  assert.equal(stepItem(items, 6, 1).index, null, "no hay más");
  assert.equal(stepItem(items, 6, -1).index, 4);
  assert.equal(stepItem(items, 4, -1).index, 0);
  assert.equal(stepItem(items, 0, -1).index, null);
  assert.equal(stepItem(items, null, 1).index, 0, "sin elemento: el primero proyectable");
  assert.equal(stepItem(items, null, -1).index, 6, "y hacia atrás, el último");
  assert.equal(items.length, 7);
});

test("lista vacía o sin nada proyectable: no hay siguiente", () => {
  assert.equal(stepItem([], null, 1).index, null);
  assert.equal(stepItem([item(1, { isMissing: true, projectable: false }), item(2, { itemType: "note", projectable: false })], null, 1).index, null);
  assert.equal(whyNotProjectable(item(1)), null);
  assert.equal(whyNotProjectable(item(2, { itemType: "note", projectable: false })), null);
});

// ───────────── lista que se abre sola ─────────────

test("la lista por defecto: la recordada, si no la de hoy, si no la próxima, si no la más reciente pasada, si no una plantilla", () => {
  const l = (id, date) => ({ id, name: `L${id}`, serviceDate: date, itemCount: 0, missingCount: 0, revision: 1 });
  const lists = [l(1, "2026-11-22"), l(2, "2026-12-06"), l(3, "2026-11-29"), l(4, null)];
  assert.equal(pickDefaultList(lists, "2026-11-29", null), 3, "la de hoy");
  assert.equal(pickDefaultList(lists, "2026-11-23", null), 3, "la próxima (la más cercana a futuro)");
  assert.equal(pickDefaultList(lists, "2027-01-01", null), 2, "todas pasadas: la más reciente");
  assert.equal(pickDefaultList(lists, "2026-11-29", 1), 1, "la recordada gana si sigue existiendo");
  assert.equal(pickDefaultList(lists, "2026-11-29", 99), 3, "una recordada que ya no existe no cuenta");
  assert.equal(pickDefaultList([l(4, null), l(5, null)], "2026-11-29", null), 4, "solo plantillas: la primera");
  assert.equal(pickDefaultList([], "2026-11-29", null), null);
  assert.equal(localDate(new Date(2026, 10, 5)), "2026-11-05", "fecha LOCAL con ceros");
});

// ───────────── atajos ─────────────

const key = (k, o = {}) => ({ key: k, ctrlKey: false, altKey: false, metaKey: false, shiftKey: false, ...o });
const live = { mode: "live" };

test("atajos de la lista: cada tecla del mapa produce su acción, con los modificadores EXACTOS", () => {
  const m = (e, ctx = live, target = null) => matchListShortcut(e, target, ctx);
  assert.equal(m(key("ArrowUp")), "cursorPrev");
  assert.equal(m(key("ArrowDown")), "cursorNext");
  assert.equal(m(key("Enter")), "activate");
  assert.equal(m(key("ArrowRight")), "slideNext");
  assert.equal(m(key("PageDown")), "slideNext");
  assert.equal(m(key("ArrowLeft")), "slidePrev");
  assert.equal(m(key("PageUp")), "slidePrev");
  assert.equal(m(key("ArrowRight", { ctrlKey: true })), "itemNext", "Ctrl+→ salta de elemento (distinto de →)");
  assert.equal(m(key("ArrowLeft", { ctrlKey: true })), "itemPrev");
  assert.equal(m(key("Escape")), "clear");
  assert.equal(m(key("e", { ctrlKey: true })), "toggleMode");
  assert.equal(m(key("E", { ctrlKey: true })), "toggleMode", "con Bloq Mayús");
  // Modificadores que sobran: no coincide.
  assert.equal(m(key("ArrowRight", { shiftKey: true })), null);
  assert.equal(m(key("ArrowRight", { altKey: true })), null);
  assert.equal(m(key("ArrowRight", { metaKey: true })), null);
  assert.equal(m(key("Enter", { ctrlKey: true })), null);
  assert.equal(m(key("ArrowDown", { ctrlKey: true })), null);
});

test("en modo EDITAR las teclas de PROYECCIÓN no hacen nada; sí están ↑/↓ (selección), Alt+↑/↓, Supr, Ctrl+D, Ctrl+Z, / y Ctrl+E", () => {
  const edit = { mode: "edit" };
  const m = (e) => matchListShortcut(e, null, edit);
  for (const k of ["Enter", "ArrowRight", "ArrowLeft", "PageDown", "PageUp", "Escape"]) assert.equal(m(key(k)), null, k);
  assert.equal(m(key("ArrowRight", { ctrlKey: true })), null);
  assert.equal(m(key("e", { ctrlKey: true })), "toggleMode");
  assert.equal(m(key("ArrowUp")), "cursorPrev");
  assert.equal(m(key("ArrowDown")), "cursorNext");
  assert.equal(m(key("ArrowUp", { altKey: true })), "itemMoveUp");
  assert.equal(m(key("ArrowDown", { altKey: true })), "itemMoveDown");
  assert.equal(m(key("Delete")), "itemRemove");
  assert.equal(m(key("d", { ctrlKey: true })), "itemDuplicate");
  assert.equal(m(key("z", { ctrlKey: true })), "undo");
  assert.equal(m(key("/")), "focusSearch");
  assert.equal(m(key("/", { shiftKey: true })), "focusSearch", "la «/» del teclado español es Mayús+7");
  // Y en EN VIVO las de edición no existen.
  for (const [k, o] of [["Delete", {}], ["d", { ctrlKey: true }], ["z", { ctrlKey: true }], ["/", {}], ["ArrowUp", { altKey: true }], ["ArrowDown", { altKey: true }]]) assert.equal(matchListShortcut(key(k, o), null, live), null, `${k} en EN VIVO`);
});

test("las teclas del cuadro de búsqueda de canciones: ↑/↓ eligen, Enter añade al final, Ctrl+Enter después de la selección, Esc borra", () => {
  assert.equal(matchListSearchKey(key("ArrowDown")), "moveDown");
  assert.equal(matchListSearchKey(key("ArrowUp")), "moveUp");
  assert.equal(matchListSearchKey(key("Enter")), "addEnd");
  assert.equal(matchListSearchKey(key("Enter", { ctrlKey: true })), "addAfter");
  assert.equal(matchListSearchKey(key("Escape")), "clear");
  assert.equal(matchListSearchKey(key("a")), null);
  assert.equal(matchListSearchKey(key("Enter", { shiftKey: true })), null);
  assert.equal(matchListSearchKey(key("Enter", { altKey: true })), null);
});
test("con el foco en un cuadro de texto NINGÚN atajo se interpreta: Ctrl+←/→ siguen moviendo el cursor por palabras y Ctrl+E no cambia de modo a medio escribir", () => {
  for (const target of [{ tagName: "INPUT", type: "text" }, { tagName: "TEXTAREA" }, { tagName: "SELECT" }, { tagName: "DIV", isContentEditable: true }]) {
    for (const s of LIST_SHORTCUTS) {
      assert.equal(matchListShortcut(key(s.key, { ctrlKey: s.ctrl === true }), target, live), null, `${s.label} en ${target.tagName}`);
    }
  }
  // Una casilla o un botón no cuentan como escribir (las teclas siguen valiendo), pero Enter sobre un botón es del botón.
  assert.equal(matchListShortcut(key("ArrowRight"), { tagName: "INPUT", type: "checkbox" }, live), "slideNext");
  assert.equal(matchListShortcut(key("Enter"), { tagName: "BUTTON" }, live), null);
});

test("los atajos globales del backend (Ctrl+Alt+Shift+M/B/C/L/R) nunca se interpretan y ninguna entrada del mapa choca con ellos", () => {
  for (const letter of ["m", "b", "c", "l", "r"]) {
    assert.equal(matchListShortcut(key(letter, { ctrlKey: true, altKey: true, shiftKey: true }), null, live), null);
    assert.equal(matchListShortcut(key(letter.toUpperCase(), { ctrlKey: true, altKey: true, shiftKey: true }), null, live), null);
  }
  assert.equal(RESERVED_GLOBAL_SHORTCUTS.length, 5);
  for (const s of LIST_SHORTCUTS) assert.ok(!(s.ctrl === true && s.alt === true), `${s.label}: nunca Ctrl+Alt (los globales llevan Ctrl+Alt+Mayús)`);
  // Sin repeticiones: dos entradas no pueden compartir tecla y modificador.
  const seen = new Set(LIST_SHORTCUTS.map((s) => `${s.key}|${String(s.ctrl === true)}|${String(s.alt === true)}|${s.scope}`));
  assert.equal(seen.size, LIST_SHORTCUTS.length);
});
