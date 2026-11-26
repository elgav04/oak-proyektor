import assert from "node:assert/strict";
import test, { after } from "node:test";
import { createListBackend, fakeFit, songOf } from "./list-fixtures.mjs";
import { line, until } from "./song-fixtures.mjs";
import { setupDom, sleep } from "./dom.mjs";
import { closeServer, loadTs } from "./helper.mjs";

// Modo EDITAR con DOM simulado (happy-dom: simula el DOM, NO hace arrastres con ratón ni mide): formularios, teclado sin ratón, faltantes, aviso al pasar a EN VIVO,
// conflicto de revisión y de extremo a extremo (armar una lista desde cero y proyectarla hasta OutputScreen). Backend falso v13. Datos sintéticos.
const win = setupDom();
for (const name of ["KeyboardEvent", "MouseEvent", "InputEvent", "HTMLInputElement", "HTMLSelectElement", "HTMLTextAreaElement", "HTMLButtonElement", "HTMLLIElement", "HTMLOListElement"]) {
  if (win[name]) globalThis[name] = win[name];
}
globalThis.requestAnimationFrame = (cb) => setTimeout(cb, 0);
globalThis.cancelAnimationFrame = (id) => clearTimeout(id);
const { act, createElement } = await import("react");
const { createRoot } = await import("react-dom/client");
const { ListTab } = await loadTs("/src/control/lists/ListTab.tsx");
const { createListController } = await loadTs("/src/control/lists/controller.ts");
const { createListEditor } = await loadTs("/src/control/lists/editor.ts");
const { OutputScreen } = await loadTs("/src/output/OutputScreen.tsx");
after(closeServer);

function scenario(withList = true) {
  const b = createListBackend();
  const A = b.addSong(songOf("Canción A", 3));
  const B = b.addSong(songOf("Canción B", 2));
  const C = b.addSong(songOf("Canción C", 2));
  const D = b.addSong(songOf("Canción D", 2));
  const list = withList
    ? b.addList({ name: "Domingo sintético", date: "2026-11-22", items: [{ song: A.id }, { note: "Ofrendas", text: "Pasan los diáconos" }, { song: D.id }, { song: B.id }, { passage: [43, 3, 1, 2, "NOINSTALADA"] }] })
    : null;
  b.songs.delete(D.id);
  return { b, A, B, C, D, list };
}

async function mount({ withList = true, edit = true } = {}) {
  const s = scenario(withList);
  const controller = createListController({ api: s.b.client, getOutput: () => s.b.output(), createFit: fakeFit, sendIntervalMs: 20, prefetchDelayMs: 10, today: () => "2026-11-25", storage: null });
  const editor = createListEditor({ api: s.b.client, list: controller, today: () => "2026-11-25", searchDebounceMs: 5, confirmTimeoutMs: 2000 });
  const container = win.document.createElement("div");
  win.document.body.appendChild(container);
  const root = createRoot(container);
  const render = async () => { await act(async () => { root.render(createElement(ListTab, { controller, editor, connected: true, output: s.b.output() })); }); };
  await render();
  await act(async () => { await sleep(60); });
  if (edit) { controller.setMode("edit"); }
  await render();
  await act(async () => { await sleep(40); });
  await render();
  const q = (sel) => container.querySelector(sel);
  const qa = (sel) => [...container.querySelectorAll(sel)];
  const settle = async (ms = 80) => { await act(async () => { await sleep(ms); }); await render(); };
  const press = async (key, opts = {}, target = win.document.body) => {
    await act(async () => {
      const event = new win.KeyboardEvent("keydown", { key, bubbles: true, cancelable: true, ...opts });
      target.dispatchEvent(event);
      press.last = event;
      await sleep(0);
    });
    return press.last;
  };
  const typeInto = async (element, value) => {
    await act(async () => {
      const proto = element.tagName === "TEXTAREA" ? win.HTMLTextAreaElement.prototype : win.HTMLInputElement.prototype;
      Object.getOwnPropertyDescriptor(proto, "value").set.call(element, value);
      element.dispatchEvent(new win.Event("input", { bubbles: true }));
      await sleep(30);
    });
  };
  const click = async (element) => { await act(async () => { element.dispatchEvent(new win.MouseEvent("click", { bubbles: true, cancelable: true })); await sleep(0); }); };
  const btn = (re, within = container) => [...within.querySelectorAll("button")].find((b) => re.test(b.textContent));
  const rowIds = () => qa("[data-edit-row]").map((r) => Number(r.dataset.editRow));
  const rowTitles = () => qa("[data-edit-row]").map((r) => r.getAttribute("aria-label").replace(/^\d+\.\s*/, ""));
  const idle = async () => { await until(() => editor.store.getState().busy === 0); await settle(120); };
  return { ...s, controller, editor, container, q, qa, settle, press, typeInto, click, btn, rowIds, rowTitles, idle, render, unmount: async () => { await act(async () => { root.unmount(); }); editor.dispose(); controller.dispose(); } };
}

test("el modo EDITAR muestra la lista con sus elementos, la búsqueda de canciones y los botones de lista; EN VIVO no se ve", async () => {
  const m = await mount();
  try {
    assert.ok(m.q('[data-testid="list-editor"]'));
    assert.equal(m.q('[data-testid="items"]'), null, "la lista de operar se esconde");
    assert.deepEqual(m.rowTitles(), ["Canción A", "Ofrendas", "Canción D", "Canción B", "Libro 43 3:1-2"]);
    for (const re of [/^Nueva lista/, /^Renombrar/, /^Duplicar$/, /^Borrar$/, /^\+ Nota o separador/]) assert.ok(m.btn(re), `falta el botón ${String(re)}`);
    assert.ok(m.q('input[aria-label="Buscar una canción para añadir"]'));
    assert.equal(m.q('[data-testid="note-form"] input[type="checkbox"]'), null, "el formulario de la nota solo aparece al abrirlo");
  } finally { await m.unmount(); }
});

test("SIN RATÓN: ↑/↓ selecciona, Alt+↓/Alt+↑ mueve, Supr quita, Ctrl+Z recupera, Ctrl+D duplica y «/» lleva a la búsqueda", async () => {
  const m = await mount();
  try {
    const [a, note, d, b] = m.rowIds();
    assert.equal(m.q('[data-selected="true"]').dataset.editRow, String(a), "empieza con el primero seleccionado");
    await m.press("ArrowDown");
    assert.equal(m.q('[data-selected="true"]').dataset.editRow, String(note));
    await m.press("ArrowDown", { altKey: true });   // Alt+↓: baja la nota
    await m.idle();
    assert.deepEqual(m.rowIds().slice(0, 3), [a, d, note], "la nota bajó un puesto");
    assert.deepEqual(m.rowIds(), m.b.lists.get(m.list.id).items.map((i) => i.id), "y el backend tiene el mismo orden");
    assert.equal(m.q('[data-selected="true"]').dataset.editRow, String(note), "la selección sigue al elemento movido");
    await m.press("ArrowUp", { altKey: true });
    await m.idle();
    assert.deepEqual(m.rowIds().slice(0, 3), [a, note, d]);

    await m.press("ArrowDown"); await m.press("ArrowDown");   // la canción B queda seleccionada (índice 3)
    assert.equal(m.q('[data-selected="true"]').dataset.editRow, String(b));
    await m.press("Delete");
    await m.idle();
    assert.ok(!m.rowIds().includes(b), "se quitó");
    assert.match(m.q('[data-testid="edit-message"]').textContent, /Se quitó «Canción B»/);
    await m.press("z", { ctrlKey: true });
    await m.idle();
    assert.equal(m.rowTitles()[3], "Canción B", "Ctrl+Z la recupera en su sitio");

    await m.press("ArrowUp"); await m.press("ArrowUp"); await m.press("ArrowUp");
    await m.press("d", { ctrlKey: true });
    await m.idle();
    assert.deepEqual(m.rowTitles().slice(0, 3), ["Canción A", "Canción A", "Ofrendas"], "duplicada justo después");

    await m.press("/");
    assert.equal(win.document.activeElement, m.q('input[aria-label="Buscar una canción para añadir"]'));
    assert.deepEqual([...new Set(m.b.commands.map((c) => c.type))].filter((t) => !t.startsWith("playlist.")), [], "solo comandos de lista");
  } finally { await m.unmount(); }
});

test("con el foco en un cuadro de texto, Supr, Ctrl+Z y las flechas son del cuadro: no tocan la lista", async () => {
  const m = await mount();
  try {
    const search = m.q('input[aria-label="Buscar una canción para añadir"]');
    search.focus();
    const before = m.rowIds();
    for (const [k, o] of [["Delete", {}], ["z", { ctrlKey: true }], ["d", { ctrlKey: true }], ["ArrowDown", { altKey: true }]]) {
      const ev = await m.press(k, o, search);
      assert.equal(ev.defaultPrevented, false, `${k} no se consume`);
    }
    await m.settle();
    assert.deepEqual(m.rowIds(), before);
    assert.equal(m.b.writes.length, 0);
  } finally { await m.unmount(); }
});

test("crear una lista, renombrarla con fecha y borrarla desde los formularios del panel (sin confirm ni prompt)", async () => {
  const m = await mount({ withList: false });
  try {
    await m.click(m.btn(/^Nueva lista/));
    const form = m.q('[data-testid="form-new"]');
    assert.ok(form);
    await m.typeInto(form.querySelector('input[aria-label="Nombre de la lista"]'), "Culto sintético");
    await m.typeInto(form.querySelector('input[type="date"]'), "2026-11-29");
    await m.click(m.btn(/^Crear$/, form));
    await until(() => m.controller.store.getState().list?.name === "Culto sintético");
    await m.settle();
    assert.match(m.q('[data-testid="edit-message"]').textContent, /creada/);
    assert.equal(m.q('[data-testid="form-new"]'), null);

    await m.click(m.btn(/^Renombrar/));
    const meta = m.q('[data-testid="form-meta"]');
    await m.typeInto(meta.querySelector('input[aria-label="Nombre de la lista"]'), "Culto del 29");
    await m.click(m.btn(/^Sin fecha/, meta));
    await m.click(m.btn(/^Guardar$/, meta));
    await until(() => m.controller.store.getState().list?.name === "Culto del 29");
    assert.equal(m.controller.store.getState().list.serviceDate, null, "sin fecha = plantilla");

    await m.click(m.btn(/^Borrar$/));
    assert.ok(m.q('[data-testid="form-delete"]'), "pide confirmación dentro del panel");
    assert.equal(m.b.lists.size, 1);
    await m.click(m.btn(/^Borrar la lista$/));
    await until(() => m.b.lists.size === 0);
  } finally { await m.unmount(); }
});

test("DUPLICAR desde la interfaz con un faltante: el aviso visible dice qué se omitió", async () => {
  const m = await mount();
  try {
    await m.click(m.btn(/^Duplicar$/));
    const form = m.q('[data-testid="form-duplicate"]');
    assert.match(form.textContent, /NO se copian/);
    assert.equal(form.querySelector('input[aria-label="Nombre de la lista"]').value, "Copia de Domingo sintético");
    await m.click(m.btn(/^Duplicar$/, form));
    await until(() => m.controller.store.getState().list?.name === "Copia de Domingo sintético");
    await m.settle();
    const message = m.q('[data-testid="edit-message"]');
    assert.equal(message.dataset.kind, "error");
    assert.match(message.textContent, /OMITIERON 1 porque faltaba su canción: «Canción D»/);
    assert.equal(m.rowIds().length, 4, "la copia solo trae lo que se pudo copiar");
    assert.equal(m.qa('[data-problem="true"]').length, 1, "queda el pasaje con la Biblia no instalada, que sí se copia");
  } finally { await m.unmount(); }
});

test("FALTANTES: cada uno dice su motivo y ofrece reemplazar o quitar; un pasaje con la Biblia ausente ofrece la Biblia por defecto y las instaladas", async () => {
  const m = await mount();
  try {
    const problems = m.qa('[data-problem="true"]');
    assert.equal(problems.length, 2);
    const missing = problems[0];
    assert.match(missing.textContent, /La canción ya no existe \(se borró\)/);
    assert.match(missing.textContent, /en EN VIVO se salta/);
    const passage = problems[1];
    assert.match(passage.textContent, /La Biblia NOINSTALADA no está instalada/);
    await m.click(m.btn(/^Usar la Biblia TST$/, passage));
    await until(() => m.b.writes.includes("playlist.updateItem"));
    await m.idle();
    assert.equal(m.qa('[data-problem="true"]').length, 1, "el pasaje ya se resuelve");

    await m.click(m.btn(/^Reemplazar por otra canción/, m.q('[data-problem="true"]')));
    assert.match(m.q('[data-testid="song-search"]').textContent, /Reemplazar «Canción D»: elige una canción/);
    await m.click(m.btn(/^Usar esta$/, m.q('[data-song-id="3"]')));   // Canción C
    await m.idle();
    assert.equal(m.qa('[data-problem="true"]').length, 0, "sin faltantes");
    assert.equal(m.rowTitles()[2], "Canción C", "ocupa el sitio del faltante");

    // Y «Quitar» siempre está.
    await m.click(m.btn(/^✕ Quitar/, m.q('[data-edit-row]')));
    await m.idle();
    assert.equal(m.rowIds().length, 4);
  } finally { await m.unmount(); }
});

test("AVISO al pasar a EN VIVO con una edición pendiente: se ve, no cambia de modo, y se puede seguir editando o descartar", async () => {
  const m = await mount();
  try {
    await m.click(m.btn(/^Nueva lista/));
    await m.typeInto(m.q('input[aria-label="Nombre de la lista"]'), "A medias");
    await m.press("e", { ctrlKey: true });
    const warning = m.q('[data-testid="leave-warning"]');
    assert.ok(warning, "aparece el aviso");
    assert.match(warning.textContent, /edición pendiente/);
    assert.match(warning.textContent, /«Nueva lista»/);
    assert.equal(m.q('[data-testid="list-tab"]').dataset.mode, "edit", "NO se pasó a EN VIVO");
    await m.click(m.btn(/^Seguir editando/, warning));
    assert.equal(m.q('[data-testid="leave-warning"]'), null);
    assert.equal(m.q('input[aria-label="Nombre de la lista"]').value, "A medias", "lo escrito sigue ahí");

    await m.press("e", { ctrlKey: true });
    await m.click(m.btn(/^Descartar y pasar a EN VIVO/, m.q('[data-testid="leave-warning"]')));
    assert.equal(m.q('[data-testid="list-tab"]').dataset.mode, "live");
    assert.equal(m.b.writes.length, 0, "no se escribió nada");
    await m.press("e", { ctrlKey: true });   // y sin nada pendiente, el cambio es directo
    assert.equal(m.q('[data-testid="list-tab"]').dataset.mode, "edit");
    await m.press("e", { ctrlKey: true });
    assert.equal(m.q('[data-testid="list-tab"]').dataset.mode, "live");
    assert.equal(m.q('[data-testid="leave-warning"]'), null);
  } finally { await m.unmount(); }
});

test("CONFLICTO de revisión en la interfaz: aviso visible, la lista se recarga con lo que hay de verdad y no se aplica el movimiento", async () => {
  const m = await mount();
  try {
    const before = m.rowIds();
    m.b.externalEdit(m.list.id, (l) => { l.items.pop(); });   // otra ventana quitó el pasaje
    await m.press("ArrowDown", { altKey: true });
    await until(() => m.editor.store.getState().message?.kind === "conflict");
    await m.settle(200);
    const message = m.q('[data-testid="edit-message"]');
    assert.equal(message.dataset.kind, "conflict");
    assert.match(message.textContent, /NO se aplicó/);
    assert.deepEqual(m.rowIds(), before.slice(0, 4), "se ve lo que hay de verdad (sin el pasaje) y sin el movimiento");
    assert.deepEqual(m.rowIds(), m.b.lists.get(m.list.id).items.map((i) => i.id));
  } finally { await m.unmount(); }
});

test("EXTREMO A EXTREMO: armar una lista desde cero (crear, buscar canciones, nota con «limpiar» apagado, pasaje) y proyectarla en la salida real (OutputScreen)", async () => {
  const m = await mount({ withList: false });
  try {
    await m.click(m.btn(/^Nueva lista/));
    await m.typeInto(m.q('input[aria-label="Nombre de la lista"]'), "Culto sintético");
    await m.typeInto(m.q('input[type="date"]'), "2026-11-29");
    await m.click(m.btn(/^Crear$/, m.q('[data-testid="form-new"]')));
    await until(() => m.controller.store.getState().list?.name === "Culto sintético");
    await m.settle();

    // Canciones: una con el teclado en el cuadro de búsqueda (Enter) y otra con el botón.
    const search = m.q('input[aria-label="Buscar una canción para añadir"]');
    await m.typeInto(search, "Canción A");
    await until(() => m.editor.store.getState().results[0]?.title === "Canción A");
    await m.press("Enter", {}, search);
    await m.idle();
    await m.typeInto(search, "Canción B");
    await until(() => m.editor.store.getState().results[0]?.title === "Canción B");
    await m.settle();
    await m.click(m.q('button[aria-label="Añadir Canción B al final"]'));
    await m.idle();

    // Una nota: el «limpiar al activar» está apagado por defecto.
    await m.click(m.btn(/^\+ Nota o separador/));
    const noteForm = m.q('[data-testid="note-form"]');
    assert.equal(noteForm.querySelector('input[type="checkbox"]').checked, false, "apagado por defecto");
    await m.typeInto(noteForm.querySelector('input[aria-label="Título de la nota"]'), "Ofrendas");
    await m.click(m.btn(/^Añadir después de la selección/, noteForm));
    await m.idle();

    // Un pasaje (lo que hace «Añadir a la lista» de la pestaña Biblia).
    const added = await m.editor.addPassage({ itemType: "passage", songId: null, bookNumber: 43, chapter: 3, verseStart: 16, verseEnd: null, bibleAbbreviation: "TST", title: "Libro sintético 3:16 · TST", note: null, clearOnActivate: false }, "Libro sintético 3:16 · TST");
    assert.equal(added.ok, true);
    await m.idle();
    assert.deepEqual(m.rowTitles(), ["Canción A", "Canción B", "Ofrendas", "Libro sintético 3:16 · TST"]);
    assert.equal(m.b.lists.get(1).items.find((i) => i.itemType === "note").clearOnActivate, false);

    // A EN VIVO (sin nada pendiente) y a proyectar.
    await m.press("e", { ctrlKey: true });
    assert.equal(m.q('[data-testid="list-tab"]').dataset.mode, "live");
    m.controller.setCursor(0);
    await m.press("Enter");
    await until(() => m.b.shows().length === 1);
    await m.settle(40);
    await m.press("ArrowRight");
    await until(() => m.b.shows().length === 2);
    const sent = m.b.shows()[1].payload;
    assert.deepEqual(sent.origin, { playlistId: 1, itemId: m.b.lists.get(1).items[0].id, itemType: "song", position: 1 });
    const state = {
      schemaVersion: 3, revision: 0, master: "live",
      background: { kind: "color", color: "#102030", mediaUrl: null, fileName: null, muted: true, transitionMs: 0, warning: null, retry: 0 },
      content: { slide: { id: "e2e", blocks: sent.blocks.map((text) => ({ text })), theme: sent.theme, origin: sent.origin }, transitionMs: sent.transitionMs },
      overlays: { alert: null },
    };
    const out = win.document.createElement("div");
    win.document.body.appendChild(out);
    const outRoot = createRoot(out, { onCaughtError: () => undefined });
    await act(async () => { outRoot.render(createElement(OutputScreen, { state, seq: 1 })); });
    await act(async () => { await sleep(80); });
    const shown = [...out.querySelectorAll(".content-text")].map((n) => n.textContent).join("|");
    assert.ok(shown.includes(line(2, 0)), "la salida real pinta la 2.ª diapositiva de la canción añadida (letra sintética)");
    await act(async () => { outRoot.unmount(); });
  } finally { await m.unmount(); }
});
