import assert from "node:assert/strict";
import test, { after } from "node:test";
import { createListBackend, fakeFit, songOf } from "./list-fixtures.mjs";
import { tick, until } from "./song-fixtures.mjs";
import { setupDom, sleep } from "./dom.mjs";
import { closeServer, loadTs } from "./helper.mjs";

// Correcciones de la revisión de las listas (panel): menos playlist.get, borrar con revisión (v14), no mandar ediciones sin cambios y el orden de un movimiento justo
// después de añadir. Backend falso v14 con la semántica del real. Datos SINTÉTICOS.
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
after(closeServer);

/** Domingo: A, nota, D (faltante), B, pasaje, C. Ids de elemento 1..6. Hay otra lista (id 2) y una canción E para añadir. */
function scenario() {
  const b = createListBackend();
  const A = b.addSong(songOf("Canción A", 3));
  const B = b.addSong(songOf("Canción B", 2));
  const C = b.addSong(songOf("Canción C", 2));
  const D = b.addSong(songOf("Canción D", 2));
  const E = b.addSong(songOf("Canción E", 2));
  const list = b.addList({ name: "Domingo sintético", date: "2026-11-22", items: [{ song: A.id }, { note: "Ofrendas", text: "Pasan los diáconos" }, { song: D.id }, { song: B.id }, { passage: [43, 3, 1, 2, "TST"] }, { song: C.id }] });
  const other = b.addList({ name: "Otra lista", date: "2026-12-06", items: [{ note: "Bienvenida" }] });
  b.songs.delete(D.id);
  return { b, A, B, C, D, E, list, other };
}

async function make() {
  const s = scenario();
  const ctl = createListController({ api: s.b.client, getOutput: () => s.b.output(), createFit: fakeFit, prefetchDelayMs: 5, sendIntervalMs: 20, today: () => "2026-11-25", storage: null });
  await ctl.init();
  await ctl.openList(s.list.id);
  const editor = createListEditor({ api: s.b.client, list: ctl, today: () => "2026-11-25", searchDebounceMs: 5, confirmTimeoutMs: 800 });
  ctl.setMode("edit");
  await editor.enter();
  await tick(60);
  const get = editor.store.getState;
  const ids = () => ctl.store.getState().list?.items.map((i) => i.id) ?? [];
  const titles = () => s.b.lists.get(s.list.id).items.map((i) => (i.itemType === "song" ? s.b.songs.get(i.songId)?.title ?? `faltante ${String(i.songId)}` : i.title));
  const getsOf = (id) => s.b.queries.filter((q) => q.type === "playlist.get" && q.payload.id === id).length;
  const idle = async () => {
    await until(() => get().busy === 0);
    await until(() => ctl.store.getState().list?.revision === s.b.lists.get(s.list.id)?.revision && get().localOrder === null, 3000);
    await tick(150);   // deja pasar cualquier relectura retrasada (antirrebote de 80 ms) para poder contarlas
  };
  return { ...s, ctl, editor, get, ids, titles, getsOf, idle, dispose: () => { editor.dispose(); ctl.dispose(); } };
}

// ───────────── 1. playlist.get solo cuando hace falta ─────────────

test("un playlist.changed de OTRA lista no vuelve a pedir la lista abierta", async () => {
  const m = await make();
  try {
    await tick(150);
    const before = m.getsOf(m.list.id);
    m.b.externalEditNotify(m.other.id, (l) => { l.name = "Otra lista renombrada"; });
    await until(() => m.ctl.store.getState().lists.some((l) => l.name === "Otra lista renombrada"));   // el resumen de listas sí se actualiza
    await tick(150);
    assert.equal(m.getsOf(m.list.id), before, "la lista abierta no cambió: no se vuelve a pedir");
  } finally { m.dispose(); }
});

test("un playlist.changed con una revisión que el panel YA tiene no vuelve a pedir la lista", async () => {
  const m = await make();
  try {
    await tick(150);
    const before = m.getsOf(m.list.id);
    const rev = m.ctl.store.getState().list.revision;
    m.b.event("playlist.changed", { commandId: "otra-ventana", playlistId: m.list.id, revision: rev, kind: "itemMoved", itemId: null, sourceId: null, copied: 0, skipped: 0 });
    await tick(200);
    assert.equal(m.getsOf(m.list.id), before);
  } finally { m.dispose(); }
});

test("una edición propia = UNA sola relectura de la lista (antes: la del evento y otra al terminar); una ráfaga de 4 también se agrupa", async () => {
  const m = await make();
  try {
    await tick(150);
    let before = m.getsOf(m.list.id);
    m.editor.moveItem(m.ids()[0], 2);
    await m.idle();
    assert.equal(m.getsOf(m.list.id) - before, 1, "una edición, una relectura");
    assert.deepEqual(m.ids(), m.b.lists.get(m.list.id).items.map((i) => i.id), "y el panel muestra lo que hay");

    before = m.getsOf(m.list.id);
    m.editor.select(m.ids()[0]);
    for (let i = 0; i < 4; i++) m.editor.moveSelected(1);
    await m.idle();
    assert.ok(m.getsOf(m.list.id) - before <= 2, `4 movimientos seguidos: ${String(m.getsOf(m.list.id) - before)} relecturas (antes, una por evento más una al terminar)`);
    assert.deepEqual(m.ids(), m.b.lists.get(m.list.id).items.map((i) => i.id));
  } finally { m.dispose(); }
});

// ───────────── 2. borrar con revisión (v14) ─────────────

test("BORRAR con la revisión vigente borra; con una revisión CADUCADA no borra nada, avisa y recarga la lista", async () => {
  const m = await make();
  try {
    m.b.externalEdit(m.list.id, (l) => { l.items.pop(); });   // otra ventana cambió la lista sin que este panel lo sepa
    m.editor.openForm("delete");
    m.editor.submitForm();
    await until(() => m.get().message?.kind === "conflict");
    assert.match(m.get().message.text, /NO se aplicó/);
    assert.ok(m.b.lists.has(m.list.id), "no se borró nada");
    const sent = m.b.commands.find((c) => c.type === "playlist.delete");
    assert.equal(typeof sent.payload.expectedRevision, "number", "el panel manda la revisión que ve");
    await until(() => m.ctl.store.getState().list?.revision === m.b.lists.get(m.list.id).revision && m.ctl.store.getState().list.items.length === 5, 2000);   // recargada
    assert.equal(m.get().form?.kind, "delete", "la confirmación sigue a la vista");

    m.editor.submitForm();   // repetirlo con la revisión al día sí borra
    await until(() => !m.b.lists.has(m.list.id));
    await until(() => m.ctl.store.getState().list?.id === m.other.id);
  } finally { m.dispose(); }
});

// ───────────── 3. sin cambios, no se manda nada ─────────────

test("editar una nota o la Biblia de un pasaje SIN cambios no manda ningún comando (el backend no contestaría con un evento)", async () => {
  const m = await make();
  try {
    m.editor.openForm("note", 2);   // «Ofrendas»
    m.editor.submitForm();
    m.editor.openForm("note", 2);
    m.editor.setField("title", "  Ofrendas ");
    m.editor.setField("text", " Pasan los diáconos ");
    m.editor.submitForm();
    m.editor.setPassageBible(5, "TST");
    m.editor.setPassageBible(5, " TST ");
    await tick(100);
    assert.equal(m.b.commands.filter((c) => c.type === "playlist.updateItem").length, 0);
    assert.equal(m.get().form, null, "el formulario se cierra igual");
    assert.equal(m.get().busy, 0);
    assert.notEqual(m.get().message?.kind, "error", "sin el aviso de «no confirmó»");

    m.editor.openForm("note", 2);
    m.editor.setClear(true);   // un cambio de verdad sí se manda
    m.editor.submitForm();
    await m.idle();
    assert.equal(m.b.commands.filter((c) => c.type === "playlist.updateItem").length, 1);
    assert.equal(m.b.lists.get(m.list.id).items[1].clearOnActivate, true);
  } finally { m.dispose(); }
});

// ───────────── 4. el orden de un movimiento justo después de añadir ─────────────

test("AÑADIR y MOVER enseguida (sin esperar a que la lista se relea): el movimiento cuenta con el elemento añadido", async () => {
  const m = await make();
  try {
    const [a, , , b] = m.ids();
    m.editor.select(a);
    m.editor.addSong(m.E.id, "after");   // entra en la posición 1 (después de A); el panel aún no lo ve
    m.editor.select(b);
    m.editor.moveSelected(1);             // el operador baja B un puesto: debe quedar DESPUÉS del pasaje
    await m.idle();
    assert.deepEqual(m.titles(), ["Canción A", "Canción E", "Ofrendas", "faltante 4", "Libro 43 3:1-2", "Canción B", "Canción C"]);
  } finally { m.dispose(); }
});

test("AÑADIR y bajar ENSEGUIDA el elemento recién añadido (aún sin ver): se mueve un puesto, no se pierde la pulsación", async () => {
  const m = await make();
  try {
    m.editor.select(m.ids()[0]);
    m.editor.addSong(m.E.id, "after");
    m.editor.moveSelected(1);   // la selección ya es la canción nueva
    await m.idle();
    assert.deepEqual(m.titles().slice(0, 3), ["Canción A", "Ofrendas", "Canción E"]);
  } finally { m.dispose(); }
});

test("EXTREMO A EXTREMO en la pestaña: «+ después» y Alt+↓ enseguida, con el teclado y sin esperar", async () => {
  const s = scenario();
  const ctl = createListController({ api: s.b.client, getOutput: () => s.b.output(), createFit: fakeFit, prefetchDelayMs: 5, sendIntervalMs: 20, today: () => "2026-11-25", storage: null });
  const editor = createListEditor({ api: s.b.client, list: ctl, today: () => "2026-11-25", searchDebounceMs: 5, confirmTimeoutMs: 800 });
  const container = win.document.createElement("div");
  win.document.body.appendChild(container);
  const root = createRoot(container);
  const render = async () => { await act(async () => { root.render(createElement(ListTab, { controller: ctl, editor, connected: true, output: null })); }); };
  try {
    await render();
    await act(async () => { await sleep(60); });
    await ctl.openList(s.list.id);
    ctl.setMode("edit");
    await render();
    await act(async () => { await sleep(80); });
    await render();
    const add = container.querySelector(`button[aria-label="Añadir Canción E después de la selección"]`);
    assert.ok(add, "hay botón «+ después» para la canción E");
    await act(async () => {
      add.dispatchEvent(new win.MouseEvent("click", { bubbles: true, cancelable: true }));
      win.document.body.dispatchEvent(new win.KeyboardEvent("keydown", { key: "ArrowDown", altKey: true, bubbles: true, cancelable: true }));   // sin esperar nada
      await sleep(0);
    });
    await until(() => editor.store.getState().busy === 0, 3000);
    await act(async () => { await sleep(250); });
    const backend = s.b.lists.get(s.list.id).items.map((i) => (i.itemType === "song" ? s.b.songs.get(i.songId)?.title ?? "faltante" : i.title));
    assert.deepEqual(backend.slice(0, 3), ["Canción A", "Ofrendas", "Canción E"], "la canción añadida bajó un puesto");
  } finally {
    await act(async () => { root.unmount(); });
    editor.dispose(); ctl.dispose();
  }
});
