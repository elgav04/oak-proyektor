import assert from "node:assert/strict";
import test, { after } from "node:test";
import { createFakeBackend, until } from "./bible-fixtures.mjs";
import { setupDom, sleep } from "./dom.mjs";
import { closeServer, loadTs } from "./helper.mjs";

// «Añadir a la lista» desde la pestaña de Biblia: botón y atajo (Ctrl+Mayús+A). La pestaña solo construye el elemento (referencia canónica, sin texto bíblico) y se lo
// da al editor de la lista (aquí un doble que anota lo recibido). Backend de Biblia falso; datos sintéticos.
const win = setupDom();
for (const name of ["KeyboardEvent", "MouseEvent", "InputEvent", "HTMLInputElement", "HTMLSelectElement", "HTMLTextAreaElement", "HTMLButtonElement", "HTMLLabelElement"]) {
  if (win[name]) globalThis[name] = win[name];
}
const { act, createElement } = await import("react");
const { createRoot } = await import("react-dom/client");
const { BibleTab } = await loadTs("/src/control/bible/BibleTab.tsx");
const { createBibleController } = await loadTs("/src/control/bible/controller.ts");
const { matchBibleShortcut, BIBLE_SHORTCUTS } = await loadTs("/src/control/shortcuts.ts");
after(closeServer);

const fakeFit = () => ({ fits: (blocks) => blocks.join(" ").length <= 400, dispose: () => undefined });

async function mount(onAddToList) {
  const backend = createFakeBackend();
  const controller = createBibleController({ api: backend.client, createFit: fakeFit, searchDebounceMs: 5, recordDelayMs: 25, sendIntervalMs: 20 });
  const container = win.document.createElement("div");
  win.document.body.appendChild(container);
  const root = createRoot(container);
  await act(async () => { root.render(createElement(BibleTab, { controller, connected: true, output: null, onOpenSettings: () => undefined, onAddToList })); });
  await act(async () => { await sleep(30); });
  const input = () => container.querySelector('input[type="search"]');
  const type = async (text) => {
    await act(async () => {
      Object.getOwnPropertyDescriptor(win.HTMLInputElement.prototype, "value").set.call(input(), text);
      input().dispatchEvent(new win.Event("input", { bubbles: true }));
      await sleep(40);
    });
  };
  const press = async (key, opts = {}, target = win) => {
    await act(async () => { target.dispatchEvent(new win.KeyboardEvent("keydown", { key, bubbles: true, cancelable: true, ...opts })); await sleep(0); });
  };
  return { backend, controller, container, input, type, press, unmount: async () => { await act(async () => { root.unmount(); }); controller.dispose(); } };
}

const key = (k, o = {}) => ({ key: k, ctrlKey: false, altKey: false, metaKey: false, shiftKey: false, ...o });

test("el atajo Ctrl+Mayús+A está en el mapa único, sin Alt (no choca con los globales) y NO cambia las demás teclas de la Biblia", () => {
  const entry = BIBLE_SHORTCUTS.find((s) => s.action === "addToList");
  assert.deepEqual([entry.key, entry.ctrl, entry.shift], ["a", true, true]);
  assert.equal(matchBibleShortcut(key("A", { ctrlKey: true, shiftKey: true }), null), "addToList");
  assert.equal(matchBibleShortcut(key("a", { ctrlKey: true, shiftKey: true }), { tagName: "INPUT", type: "search" }), "addToList", "también con el foco en el cuadro de búsqueda (tras Enter el foco puede seguir ahí)");
  assert.equal(matchBibleShortcut(key("a"), null), null, "la «a» sola no hace nada");
  assert.equal(matchBibleShortcut(key("a", { ctrlKey: true }), null), null, "Ctrl+A (seleccionar todo) no se toca");
  assert.equal(matchBibleShortcut(key("a", { ctrlKey: true, shiftKey: true, altKey: true }), null), null, "Ctrl+Alt+Mayús+A: los globales llevan Alt y nunca se interpretan");
  for (const letter of ["m", "b", "c", "l", "r"]) assert.equal(matchBibleShortcut(key(letter, { ctrlKey: true, altKey: true, shiftKey: true }), null), null);
  assert.equal(matchBibleShortcut(key("ArrowDown", { ctrlKey: true }), null), null, "las flechas con Ctrl siguen sin ser de la Biblia");
  assert.equal(matchBibleShortcut(key("ArrowDown"), { tagName: "INPUT", type: "search" }), null, "y en el cuadro de texto lo demás sigue ignorado");
  assert.equal(matchBibleShortcut(key("ArrowDown"), null), "verseNext");
});

test("el botón y el atajo añaden el pasaje seleccionado (referencia canónica, rango, SIN texto bíblico) y el aviso se ve", async () => {
  const calls = [];
  const m = await mount(async (item, label) => { calls.push({ item, label }); return { ok: true, message: `«${label}» se añadió al final de «Domingo sintético».` }; });
  try {
    await m.type("alfa 1:2-3");
    await until(() => m.controller.store.getState().passage !== null && m.controller.store.getState().built !== null);
    const button = m.container.querySelector('[data-testid="add-to-list"]');
    assert.ok(button, "hay botón");
    await act(async () => { button.dispatchEvent(new win.MouseEvent("click", { bubbles: true })); await sleep(20); });
    assert.equal(calls.length, 1);
    const { item, label } = calls[0];
    assert.deepEqual([item.itemType, item.bookNumber, item.chapter, item.verseStart, item.verseEnd, item.bibleAbbreviation], ["passage", 1, 1, 2, 3, "SIN"]);
    assert.equal(label, "Alfa 1:2-3 · SIN");
    assert.ok(!JSON.stringify(item).includes("lumen"), "no lleva texto bíblico");
    assert.match(m.container.querySelector('[data-testid="add-status"]').textContent, /se añadió al final de «Domingo sintético»/);

    await m.press("A", { ctrlKey: true, shiftKey: true });
    await until(() => calls.length === 2);
    assert.deepEqual(calls[1].item, item, "el atajo hace lo mismo que el botón");
    // Una tecla MANTENIDA no añade una y otra vez.
    await act(async () => { win.dispatchEvent(new win.KeyboardEvent("keydown", { key: "A", ctrlKey: true, shiftKey: true, repeat: true, bubbles: true, cancelable: true })); await sleep(20); });
    assert.equal(calls.length, 2);
  } finally { await m.unmount(); }
});

test("sin pasaje elegido, o con una lista que no acepta el pasaje, el aviso lo dice y no se rompe nada", async () => {
  const calls = [];
  const m = await mount(async (item, label) => { calls.push(label); return { ok: false, message: "No hay ninguna lista abierta: crea o abre una en la pestaña «Lista»." }; });
  try {
    await m.press("A", { ctrlKey: true, shiftKey: true });
    await act(async () => { await sleep(20); });
    assert.equal(calls.length, 0, "sin pasaje no se llama al editor");
    assert.match(m.container.querySelector('[data-testid="add-status"]').textContent, /Elige primero un pasaje/);
    await m.type("alfa 1:2");
    await until(() => m.controller.store.getState().built !== null);
    await m.press("A", { ctrlKey: true, shiftKey: true });
    await until(() => calls.length === 1);
    await act(async () => { await sleep(20); });
    assert.match(m.container.querySelector('[data-testid="add-status"]').textContent, /No hay ninguna lista abierta/);
  } finally { await m.unmount(); }
});

test("sin el editor de listas (prop ausente) la pestaña de Biblia no dibuja el botón ni reacciona al atajo", async () => {
  const m = await mount(undefined);
  try {
    assert.equal(m.container.querySelector('[data-testid="add-to-list"]'), null);
    await m.press("A", { ctrlKey: true, shiftKey: true });
    assert.equal(m.container.querySelector('[data-testid="add-status"]'), null);
  } finally { await m.unmount(); }
});
