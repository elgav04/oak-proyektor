import assert from "node:assert/strict";
import test, { after } from "node:test";
import { createListBackend, fakeFit, songOf } from "./list-fixtures.mjs";
import { tick, until } from "./song-fixtures.mjs";
import { closeServer, loadTs } from "./helper.mjs";

// Modo EDITAR (controlador): editor real + controlador de la lista real + cliente IPC real + backend falso v13 con la misma semántica de revisión que el real
// (conflicto si caduca, una edición = un comando). Datos SINTÉTICOS.
const { createListController } = await loadTs("/src/control/lists/controller.ts");
const { createListEditor } = await loadTs("/src/control/lists/editor.ts");
after(closeServer);

/** Domingo: A, nota, D (se borra: faltante), B, pasaje de una Biblia NO instalada, C. Ids de elemento 1..6. */
function scenario(options = {}) {
  const b = createListBackend(options);
  const A = b.addSong(songOf("Canción A", 3));
  const B = b.addSong(songOf("Canción B", 2));
  const C = b.addSong(songOf("Canción C", 2));
  const D = b.addSong(songOf("Canción D", 2));
  b.addSong(songOf("Canción E", 2));
  const list = b.addList({
    name: "Domingo sintético", date: "2026-11-22",
    items: [{ song: A.id }, { note: "Ofrendas", text: "Pasan los diáconos" }, { song: D.id }, { song: B.id }, { passage: [43, 3, 1, 2, "NOINSTALADA"] }, { song: C.id }],
  });
  b.songs.delete(D.id);
  return { b, A, B, C, D, list };
}

async function make(options = {}, deps = {}) {
  const s = scenario(options);
  const ctl = createListController({ api: s.b.client, getOutput: () => s.b.output(), createFit: fakeFit, prefetchDelayMs: 5, sendIntervalMs: 20, today: () => "2026-11-25", storage: null });
  await ctl.init();
  const editor = createListEditor({ api: s.b.client, list: ctl, today: () => "2026-11-25", searchDebounceMs: 5, confirmTimeoutMs: 600, ...deps });
  ctl.setMode("edit");
  await editor.enter();
  const get = editor.store.getState;
  const items = () => ctl.store.getState().list?.items ?? [];
  const ids = () => items().map((i) => i.id);
  /** Espera a que el editor termine y la lista leída del backend coincida con la del backend. */
  const idle = async () => {
    await until(() => get().busy === 0);
    await until(() => get().localOrder === null && ctl.store.getState().list?.revision === s.b.lists.get(ctl.store.getState().openId)?.revision, 3000);
  };
  const backendIds = () => s.b.lists.get(s.list.id).items.map((i) => i.id);
  return { ...s, ctl, editor, get, items, ids, idle, backendIds, dispose: () => { editor.dispose(); ctl.dispose(); } };
}
const onlyListCommands = (b) => assert.deepEqual([...new Set(b.commands.map((c) => c.type))].filter((t) => !t.startsWith("playlist.")), [], "el modo EDITAR no toca la salida ni escribe canciones");

// ───────────── listas ─────────────

test("crear una lista con fecha y una PLANTILLA sin fecha; un nombre vacío o una fecha mala no mandan nada", async () => {
  const m = await make();
  m.editor.openForm("new");
  m.editor.submitForm();
  assert.match(m.get().message.text, /necesita un nombre/);
  m.editor.setField("name", "Jueves sintético");
  m.editor.setField("date", "30/11/2026");
  m.editor.submitForm();
  assert.match(m.get().message.text, /fecha/);
  assert.equal(m.b.writes.length, 0, "nada se mandó");

  m.editor.setField("date", "2026-12-03");
  m.editor.submitForm();
  await until(() => m.ctl.store.getState().list?.name === "Jueves sintético");
  assert.equal(m.ctl.store.getState().list.serviceDate, "2026-12-03");
  assert.equal(m.get().form, null);
  assert.match(m.get().message.text, /creada/);

  m.editor.openForm("new");
  m.editor.setField("name", "Plantilla sintética");
  m.editor.submitForm();
  await until(() => m.ctl.store.getState().list?.name === "Plantilla sintética");
  assert.equal(m.ctl.store.getState().list.serviceDate, null, "sin fecha = plantilla");
  assert.match(m.get().message.text, /plantilla/);
  assert.deepEqual([...new Set(m.b.writes)], ["playlist.create"]);
  onlyListCommands(m.b);
  m.dispose();
});

test("renombrar y cambiar la fecha usa la revisión de lo que se ve; con una revisión CADUCADA no se aplica, se avisa, se recarga y lo escrito SIGUE en el formulario", async () => {
  const m = await make();
  m.editor.openForm("meta");
  assert.equal(m.get().form.name, "Domingo sintético", "parte del nombre actual");
  m.editor.setField("name", "Domingo 22 sintético");
  m.editor.setField("date", "");
  m.b.externalEdit(m.list.id, (l) => { l.items.pop(); });   // otra ventana edita la lista SIN avisar a este panel
  m.editor.submitForm();
  await until(() => m.get().message?.kind === "conflict");
  assert.match(m.get().message.text, /NO se aplicó/);
  assert.equal(m.b.lists.get(m.list.id).name, "Domingo sintético", "no se aplicó nada");
  assert.equal(m.get().form?.kind, "meta", "el formulario sigue abierto");
  assert.equal(m.get().form.name, "Domingo 22 sintético", "con lo que estaba escrito");
  await until(() => m.ctl.store.getState().list.revision === m.b.lists.get(m.list.id).revision && m.items().length === 5, 2000);   // ya se recargó

  m.editor.submitForm();   // repetir la acción con la revisión al día
  await until(() => m.ctl.store.getState().list.name === "Domingo 22 sintético");
  assert.equal(m.ctl.store.getState().list.serviceDate, null);
  assert.equal(m.get().form, null);
  m.dispose();
});

test("DUPLICAR con un faltante: la copia no lo lleva, el aviso dice cuántos se omitieron y cuáles, y la original lo conserva", async () => {
  const m = await make();
  m.editor.openForm("duplicate");
  assert.equal(m.get().form.name, "Copia de Domingo sintético");
  assert.equal(m.get().form.date, "2026-11-29", "una semana después de la original");
  m.editor.submitForm();
  await until(() => m.ctl.store.getState().list?.name === "Copia de Domingo sintético");
  const copy = m.ctl.store.getState().list;
  assert.equal(copy.items.length, 5);
  assert.equal(copy.missingCount, 0, "la copia no hereda faltantes");
  assert.equal(copy.serviceDate, "2026-11-29");
  assert.match(m.get().message.text, /se copiaron 5 elemento/);
  assert.match(m.get().message.text, /OMITIERON 1 porque faltaba su canción: «Canción D»/);
  assert.equal(m.get().message.kind, "error", "se destaca porque algo NO se copió");
  assert.equal(m.b.lists.get(m.list.id).items.length, 6, "la original conserva su elemento faltante");
  m.dispose();

  // Sin faltantes el aviso es solo informativo; una plantilla propone el próximo domingo.
  const n = await make();
  n.b.addList({ name: "Plantilla", date: null, items: [{ note: "Oración" }] });
  await n.ctl.openList(2);
  n.editor.openForm("duplicate");
  assert.equal(n.get().form.date, "2026-11-29", "desde hoy (2026-11-25): el próximo domingo");
  n.editor.submitForm();
  await until(() => n.ctl.store.getState().list?.name === "Copia de Plantilla");
  assert.equal(n.get().message.kind, "info");
  assert.doesNotMatch(n.get().message.text, /OMITIERON/);
  n.dispose();
});

test("borrar una lista pide confirmación dentro del panel y después abre otra", async () => {
  const m = await make();
  m.b.addList({ name: "Otra lista", date: "2026-12-06" });
  await m.ctl.reload();
  m.editor.openForm("delete");
  assert.equal(m.b.writes.length, 0, "abrir la confirmación no borra nada");
  m.editor.submitForm();
  await until(() => !m.b.lists.has(m.list.id));
  await until(() => m.ctl.store.getState().list?.name === "Otra lista");
  assert.match(m.get().message.text, /borrada/);
  assert.equal(m.get().form, null);
  m.dispose();
});

// ───────────── añadir ─────────────

test("añadir una canción desde la búsqueda: al final con Enter, después de la selección con Ctrl+Enter; sin tocar la salida", async () => {
  const m = await make();
  m.editor.setQuery("Canción E");
  await until(() => m.get().results.length === 1 && m.get().results[0].title === "Canción E");
  m.editor.addResult("end");
  await m.idle();
  assert.equal(m.items().at(-1).title, "Canción E");
  assert.equal(m.get().selectedId, m.items().at(-1).id, "queda seleccionada");

  m.editor.select(1);
  m.editor.setQuery("Canción B");
  await until(() => m.get().results[0]?.title === "Canción B");
  m.editor.addResult("after");
  await m.idle();
  assert.deepEqual(m.items().slice(0, 3).map((i) => i.title), ["Canción A", "Canción B", "Ofrendas"], "después del elemento seleccionado (el 1)");
  assert.deepEqual([...new Set(m.b.writes)], ["playlist.addItem"]);
  onlyListCommands(m.b);
  m.dispose();
});

test("la misma canción se puede añadir dos veces (son dos elementos) y una que ya no existe se rechaza con un mensaje", async () => {
  const m = await make();
  m.editor.addSong(m.A.id, "end");
  m.editor.addSong(m.A.id, "end");
  await m.idle();
  const as = m.items().filter((i) => i.songId === m.A.id);
  assert.equal(as.length, 3, "la A original y dos más");
  assert.equal(new Set(as.map((i) => i.id)).size, 3);
  m.b.songs.delete(m.C.id);
  m.editor.addSong(m.C.id, "end");
  await until(() => m.get().message?.kind === "error");
  assert.match(m.get().message.text, /no existe/);
  m.dispose();
});

test("añadir una NOTA: el «limpiar al activar» está apagado por defecto, sin título no se añade y se puede editar", async () => {
  const m = await make();
  m.editor.openForm("note");
  assert.equal(m.get().form.clear, false, "apagado por defecto");
  m.editor.submitForm();
  assert.match(m.get().message.text, /necesita un título/);
  assert.equal(m.b.writes.length, 0);
  m.editor.setField("title", "Anuncios");
  m.editor.setField("text", "Habla la hermana sintética");
  m.editor.submitForm();
  await m.idle();
  const note = m.items().find((i) => i.title === "Anuncios");
  assert.deepEqual([note.itemType, note.note, note.clearOnActivate], ["note", "Habla la hermana sintética", false]);
  assert.equal(m.b.commands.find((c) => c.payload.item?.title === "Anuncios").payload.item.clearOnActivate, false, "lo que se mandó");

  m.editor.openForm("note", note.id);
  assert.equal(m.get().form.title, "Anuncios");
  m.editor.setClear(true);
  m.editor.setField("text", "");
  m.editor.submitForm();
  await m.idle();
  const edited = m.items().find((i) => i.id === note.id);
  assert.deepEqual([edited.note, edited.clearOnActivate], [null, true]);
  assert.equal(m.get().form, null);
  m.dispose();
});

test("añadir un PASAJE desde la Biblia: al final de la lista abierta y con un aviso; sin lista abierta, no hace nada y lo dice", async () => {
  const m = await make();
  const item = { itemType: "passage", songId: null, bookNumber: 43, chapter: 3, verseStart: 16, verseEnd: 17, bibleAbbreviation: "TST", title: "Libro sintético 3:16-17 · TST", note: null, clearOnActivate: false };
  const r = await m.editor.addPassage(item, "Libro sintético 3:16-17 · TST");
  assert.equal(r.ok, true);
  assert.match(r.message, /se añadió al final de «Domingo sintético»/);
  await m.idle();
  const added = m.items().at(-1);
  assert.deepEqual([added.itemType, added.verseStart, added.verseEnd, added.bibleAbbreviation, added.resolution], ["passage", 16, 17, "TST", "resolved"]);
  m.dispose();

  const empty = createListBackend();
  const ctl = createListController({ api: empty.client, getOutput: () => null, createFit: fakeFit, today: () => "2026-11-25", storage: null });
  const ed = createListEditor({ api: empty.client, list: ctl, confirmTimeoutMs: 300 });
  const none = await ed.addPassage(item, "x");
  assert.equal(none.ok, false);
  assert.match(none.message, /No hay ninguna lista abierta/);
  assert.equal(empty.writes.length, 0);
  ed.dispose(); ctl.dispose();
});

// ───────────── reordenar ─────────────

test("REORDENAR: se ve al instante (orden optimista), cada movimiento es UN comando con la revisión vigente y el orden final es el del backend", async () => {
  const m = await make();
  const before = ids(m);
  function ids(x) { return x.ids(); }
  m.editor.moveItem(before[0], 3);
  assert.deepEqual(m.get().localOrder, [before[1], before[2], before[3], before[0], before[4], before[5]], "se ve ya, sin esperar al backend");
  assert.deepEqual(m.editor.displayedItems().map((i) => i.id), m.get().localOrder);
  await m.idle();
  const move = m.b.commands.find((c) => c.type === "playlist.moveItem");
  assert.deepEqual([move.payload.itemId, move.payload.toIndex, move.payload.expectedRevision], [before[0], 3, 1]);
  assert.deepEqual(m.ids(), m.backendIds());
  assert.deepEqual(m.ids(), [before[1], before[2], before[3], before[0], before[4], before[5]]);
  assert.equal(m.get().localOrder, null);
  m.dispose();
});

test("una RÁFAGA de movimientos (teclado o dos arrastres seguidos) se encadena: cada uno con la revisión que dejó el anterior y ninguno se pisa", async () => {
  const m = await make();
  const first = m.ids()[0];
  m.editor.select(first);
  for (let i = 0; i < 4; i++) m.editor.moveSelected(1);   // 4 × Alt+↓ sin esperar
  await m.idle();
  assert.equal(m.b.writes.filter((w) => w === "playlist.moveItem").length, 4);
  assert.equal(m.ids().indexOf(first), 4, "el elemento bajó 4 puestos");
  assert.deepEqual(m.ids(), m.backendIds());
  const revisions = m.b.commands.filter((c) => c.type === "playlist.moveItem").map((c) => c.payload.expectedRevision);
  assert.deepEqual(revisions, [1, 2, 3, 4], "cada uno con la revisión vigente tras el anterior");
  assert.equal(m.get().message, null, "sin conflictos");
  m.dispose();
});

test("la edición con revisión CADUCADA (otra ventana cambió la lista): no se aplica, se avisa, se recarga y el orden vuelve a ser el del backend", async () => {
  const m = await make();
  const original = m.backendIds();
  m.b.externalEdit(m.list.id, (l) => { l.items.splice(2, 1); });   // otra ventana quita el elemento 3 sin avisar
  m.editor.moveItem(original[0], 3);
  assert.notDeepEqual(m.get().localOrder, null, "primero se ve el cambio optimista");
  await until(() => m.get().message?.kind === "conflict");
  assert.match(m.get().message.text, /NO se aplicó/);
  await until(() => m.get().localOrder === null && m.items().length === 5, 3000);
  assert.deepEqual(m.ids(), m.backendIds(), "el panel muestra lo que hay de verdad");
  assert.equal(m.ids()[0], original[0], "el movimiento no se aplicó");
  m.dispose();
});

test("soltar un elemento sobre otro (el gesto del arrastre) lo mueve a esa posición; sobre sí mismo no hace nada", async () => {
  const m = await make();
  const [a, , , d] = m.ids();
  m.editor.dropItem(a, a);
  assert.equal(m.get().busy, 0);
  assert.equal(m.b.writes.length, 0);
  m.editor.dropItem(a, d);
  await m.idle();
  assert.equal(m.ids().indexOf(a), 3);
  m.dispose();
});

// ───────────── quitar, deshacer, duplicar un elemento ─────────────

test("quitar un elemento (comando con la revisión) y DESHACER lo recupera en su sitio; quitar con la revisión caducada no quita nada", async () => {
  const m = await make();
  const original = m.backendIds();
  m.editor.removeItem(original[3]);   // la canción B
  assert.ok(!m.editor.displayedItems().some((i) => i.id === original[3]), "desaparece al instante");
  await m.idle();
  assert.ok(!m.ids().includes(original[3]));
  assert.match(m.get().message.text, /Se quitó «Canción B» de la lista\. Ctrl\+Z lo recupera/);
  m.editor.undo();
  await m.idle();
  assert.equal(m.items()[3].title, "Canción B", "vuelve a su posición");
  assert.equal(m.get().lastRemoved, null);

  m.b.externalEdit(m.list.id, () => undefined);   // la revisión caduca
  const count = m.backendIds().length;
  m.editor.removeItem(m.ids()[0]);
  await until(() => m.get().message?.kind === "conflict");
  await m.idle();
  assert.equal(m.b.lists.get(m.list.id).items.length, count, "no se quitó nada");
  assert.equal(m.ids().length, count);
  m.dispose();
});

test("duplicar un elemento lo pone justo después; un faltante no se puede duplicar", async () => {
  const m = await make();
  const [a] = m.ids();
  m.editor.duplicateItem(a);
  await m.idle();
  assert.deepEqual(m.items().slice(0, 2).map((i) => i.title), ["Canción A", "Canción A"]);
  assert.notEqual(m.items()[0].id, m.items()[1].id);
  const missing = m.items().find((i) => i.isMissing);
  const writes = m.b.writes.length;
  m.editor.duplicateItem(missing.id);
  assert.match(m.get().message.text, /no se puede duplicar/);
  assert.equal(m.b.writes.length, writes);
  m.dispose();
});

// ───────────── faltantes ─────────────

test("un elemento FALTANTE se puede REEMPLAZAR por otra canción: entra en su sitio y el faltante se quita, en dos comandos pequeños", async () => {
  const m = await make();
  const missing = m.items().find((i) => i.isMissing);
  const at = m.ids().indexOf(missing.id);
  m.editor.startReplace(missing.id);
  assert.equal(m.get().replaceTarget, missing.id);
  m.editor.addSong(m.b.songs.keys().next().value === m.A.id ? m.C.id : m.C.id, "end");
  await m.idle();
  assert.equal(m.get().replaceTarget, null);
  assert.equal(m.items().some((i) => i.isMissing), false, "ya no hay faltantes");
  assert.equal(m.items()[at].title, "Canción C", "la nueva ocupa el sitio del faltante");
  assert.equal(m.items().length, 6);
  assert.deepEqual(m.b.writes.slice(-2), ["playlist.addItem", "playlist.removeItem"]);
  m.dispose();
});

test("un pasaje con una Biblia NO instalada se puede pasar a otra Biblia (o a la ofrecida) y entonces se resuelve; también se puede quitar", async () => {
  const m = await make();
  const passage = m.items().find((i) => i.itemType === "passage");
  assert.deepEqual([passage.resolution, passage.projectable, passage.offeredBible], ["bibleNotInstalled", false, "TST"], "el backend ofrece la Biblia por defecto, sin aplicarla");
  assert.equal(passage.bibleAbbreviation, "NOINSTALADA", "el elemento conserva su Biblia hasta que el operador elija");
  m.editor.setPassageBible(passage.id, "TST");
  await m.idle();
  const fixed = m.items().find((i) => i.id === passage.id);
  assert.deepEqual([fixed.resolution, fixed.projectable, fixed.bibleAbbreviation], ["resolved", true, "TST"]);
  assert.match(m.get().message.text, /Biblia TST/);
  m.dispose();
});

// ───────────── aviso al pasar a EN VIVO ─────────────

test("pasar a EN VIVO con un formulario sin terminar NO cambia de modo y avisa; «Seguir editando» lo deja y «Descartar» pasa y limpia", async () => {
  const m = await make();
  m.editor.openForm("note");
  m.editor.setField("title", "Silencio");
  m.ctl.toggleMode();
  assert.equal(m.ctl.store.getState().mode, "edit", "no se pasó");
  assert.match(m.ctl.store.getState().leaveWarning, /formulario sin terminar: «Nueva nota»/);
  m.ctl.dismissLeaveWarning();
  assert.equal(m.ctl.store.getState().leaveWarning, null);
  assert.equal(m.get().form.title, "Silencio", "seguir editando conserva lo escrito");

  m.ctl.toggleMode();
  assert.ok(m.ctl.store.getState().leaveWarning);
  m.ctl.confirmLeave();
  assert.equal(m.ctl.store.getState().mode, "live");
  assert.equal(m.get().form, null, "se descartó");
  assert.equal(m.ctl.store.getState().leaveWarning, null);
  assert.equal(m.b.writes.length, 0, "no se escribió nada");
  m.dispose();
});

test("pasar a EN VIVO con una edición ENVIÁNDOSE avisa; cuando termina, se puede pasar sin avisos", async () => {
  const m = await make();
  m.b.holdWrites();
  m.editor.moveItem(m.ids()[0], 2);
  await until(() => m.get().busy === 1);
  assert.match(m.editor.pendingReason(), /guardando una edición/);
  m.ctl.setMode("live");
  assert.equal(m.ctl.store.getState().mode, "edit");
  assert.ok(m.ctl.store.getState().leaveWarning);
  m.b.releaseWrites();
  await m.idle();
  assert.equal(m.editor.pendingReason(), null);
  m.ctl.setMode("live");
  assert.equal(m.ctl.store.getState().mode, "live");
  m.dispose();
});

test("una edición sin confirmación (el backend no contesta) acaba con un aviso y la lista vuelve a leerse, sin quedarse «guardando» para siempre", async () => {
  const m = await make({}, { confirmTimeoutMs: 60 });
  m.b.holdWrites();
  m.editor.moveItem(m.ids()[0], 2);
  await until(() => m.get().message?.text.includes("no confirmó"), 2000);
  assert.equal(m.get().busy, 0);
  assert.equal(m.get().localOrder, null);
  m.b.releaseWrites();
  m.dispose();
});

test("cambiar de lista con un formulario sin terminar se avisa desde el editor (no se pierde lo escrito)", async () => {
  const m = await make();
  m.editor.openForm("meta");
  m.editor.setField("name", "Nombre a medias");
  assert.ok(m.editor.pendingReason());
  m.editor.warn("No se cambió de lista.");
  assert.equal(m.get().message.kind, "error");
  assert.equal(m.get().form.name, "Nombre a medias");
  m.dispose();
});

test("la edición NO manda nada a la salida ni a la base fuera de playlist.* y no bloquea: 100 comandos encadenados se resuelven por turnos", async () => {
  const m = await make();
  const t0 = performance.now();
  for (let i = 0; i < 40; i++) m.editor.addSong(m.A.id, "end");
  assert.ok(performance.now() - t0 < 200, "encolar es instantáneo");
  assert.equal(m.get().busy > 0, true);
  await until(() => m.get().busy === 0, 8000);
  await m.idle();
  assert.equal(m.items().length, 46);
  assert.deepEqual(m.ids(), m.backendIds());
  onlyListCommands(m.b);
  await tick(10);
  m.dispose();
});
