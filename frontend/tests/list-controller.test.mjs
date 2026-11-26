import assert from "node:assert/strict";
import test, { after } from "node:test";
import { createListBackend, fakeFit, songOf } from "./list-fixtures.mjs";
import { tick, until } from "./song-fixtures.mjs";
import { closeServer, loadTs } from "./helper.mjs";

// Pestaña «Lista», modo EN VIVO: controlador real + cliente IPC real + backend falso (v13). Datos SINTÉTICOS (letra y texto bíblico inventados).
const { createListController } = await loadTs("/src/control/lists/controller.ts");
const { activeFromOutput } = await loadTs("/src/control/lists/active.ts");
after(closeServer);

const BG = { kind: "video", path: "C:\\fondos\\sintetico.mp4", color: "#102030", muted: true };

/** Una lista del domingo: A, nota, D (se borra: faltante), B, pasaje, C (con fondo), A otra vez. Ids de elemento 1..7. */
function scenario(options = {}) {
  const b = createListBackend(options);
  const A = b.addSong(songOf("Canción A", 3));
  const B = b.addSong(songOf("Canción B", 2));
  const C = b.addSong(songOf("Canción C", 2, { background: BG }));
  const D = b.addSong(songOf("Canción D", 2));
  b.addChapter("TST", 43, 3, ["uno sintético", "dos sintético", "tres sintético"], "Aviso sintético");
  const list = b.addList({
    name: "Domingo sintético", date: "2026-11-29",
    items: [{ song: A.id }, { note: "Ofrendas", text: "Pasan los diáconos" }, { song: D.id }, { song: B.id }, { passage: [43, 3, 1, 2, "TST"] }, { song: C.id }, { song: A.id }],
  });
  b.songs.delete(D.id);
  return { b, A, B, C, D, list };
}

async function make(options = {}) {
  const s = scenario(options);
  const controller = createListController({
    api: s.b.client, getOutput: () => s.b.output(), createFit: fakeFit, sendIntervalMs: 30, prefetchDelayMs: 10, inflightMs: 400,
    today: () => "2026-11-29", storage: null, sliceBudgetMs: 12,
  });
  await controller.init();
  const get = controller.store.getState;
  const shows = () => s.b.shows();
  const lastOrigin = () => shows().at(-1)?.payload.origin ?? null;
  /** Espera a que lo pedido llegue (envío agrupado de la ráfaga). */
  const settle = async (ms = 60) => { await tick(ms); };
  return { ...s, controller, get, shows, lastOrigin, settle };
}
const onlyOutputCommands = (b) => assert.deepEqual([...new Set(b.commands.map((c) => c.type))].filter((t) => !t.startsWith("output.")), [], "ningún comando que no sea de la salida: no se escribe nada en la BD");

test("al abrir la pestaña se elige la lista de HOY, el cursor cae en el primer elemento proyectable y solo se LEE (nada de escribir ni de tocar la salida)", async () => {
  const m = await make();
  assert.equal(m.get().openId, m.list.id);
  assert.equal(m.get().list.items.length, 7);
  assert.equal(m.get().cursor, 0);
  assert.equal(m.get().settings.onItemEnd, "stay");
  assert.equal(m.b.commands.length, 0, "abrir la lista no manda ningún comando");
  assert.ok(m.b.queries.every((q) => ["playlist.getSettings", "song.getSettings", "bible.getSettings", "playlist.list", "playlist.get", "song.get", "bible.chapter"].includes(q.type)));
  m.controller.dispose();
});

test("Enter proyecta el elemento del cursor: un único output.showContent con el texto, el tema con red de seguridad y el origin con SOLO ids", async () => {
  const m = await make();
  m.controller.activate();
  await until(() => m.shows().length === 1);
  const sent = m.shows()[0].payload;
  assert.deepEqual(sent.blocks, [m.A.sections[0].text]);
  assert.deepEqual(sent.origin, { playlistId: m.list.id, itemId: 1, itemType: "song", position: 0 });
  assert.equal(sent.theme.autoFit, true);
  assert.ok(sent.theme.minFontSize < sent.theme.fontSize);
  assert.equal(sent.transitionMs, 400);
  assert.deepEqual(Object.keys(sent.origin).sort(), ["itemId", "itemType", "playlistId", "position"], "el origin no lleva texto");
  assert.equal(m.b.sent("output.setBackground").length, 0, "una canción sin fondo propio NO toca el fondo (no corta el video actual)");
  onlyOutputCommands(m.b);
  m.controller.dispose();
});

test("→ avanza dentro de la canción; en la última diapositiva se QUEDA con «FIN» (ajuste por defecto) y no vacía la pantalla; ← quita el FIN", async () => {
  const m = await make();
  m.controller.activate();
  await until(() => m.shows().length === 1);
  for (const position of [1, 2]) { await m.settle(35); m.controller.nextSlide(); await until(() => m.lastOrigin()?.position === position); }
  assert.equal(m.get().atEnd, false);
  await m.settle(35);
  const before = m.shows().length;
  m.controller.nextSlide();   // una pulsación de más
  assert.equal(m.get().atEnd, true);
  await m.settle(80);
  assert.equal(m.shows().length, before, "no se proyecta nada más");
  assert.equal(m.b.sent("output.clearContent").length, 0, "y no se vacía la pantalla");
  m.controller.prevSlide();
  assert.equal(m.get().atEnd, false);
  await until(() => m.lastOrigin()?.position === 1);
  m.controller.prevSlide();
  await until(() => m.lastOrigin()?.position === 0);
  await m.settle(35);
  const n = m.shows().length;
  m.controller.prevSlide();   // antes de la primera no pasa nada
  await m.settle(60);
  assert.equal(m.shows().length, n);
  m.controller.dispose();
});

test("al terminar un elemento: «pasar al siguiente» salta faltantes y notas y avisa; «limpiar» quita el texto", async () => {
  const m = await make({ onItemEnd: "next" });
  assert.equal(m.get().settings.onItemEnd, "next");
  m.controller.activate();
  await until(() => m.shows().length === 1);
  for (let i = 0; i < 2; i++) { await m.settle(35); m.controller.nextSlide(); }
  await until(() => m.lastOrigin()?.position === 2);
  await m.settle(35);
  m.controller.nextSlide();   // en la última de A: pasa al siguiente proyectable
  await until(() => m.lastOrigin()?.itemId === 4);
  assert.deepEqual(m.lastOrigin(), { playlistId: m.list.id, itemId: 4, itemType: "song", position: 0 }, "B, saltando la nota y la canción faltante");
  assert.match(m.get().notice ?? "", /Se omitió: Falta la canción «Canción D»/);
  assert.equal(m.get().cursor, 3);
  m.controller.dispose();

  const c = await make();
  c.b.state.playlistSettings = { onItemEnd: "clear" };
  await c.controller.onShow();   // al volver a la pestaña se vuelven a leer los ajustes
  assert.equal(c.get().settings.onItemEnd, "clear");
  c.controller.setCursor(3);
  c.controller.activate();
  await until(() => c.shows().length === 1);
  await c.settle(35);
  c.controller.nextSlide();
  await until(() => c.lastOrigin()?.position === 1);
  await c.settle(35);
  c.controller.nextSlide();
  await until(() => c.b.sent("output.clearContent").length === 1);
  assert.equal(c.get().atEnd, true);
  c.controller.dispose();
});

test("Ctrl+→ / Ctrl+← saltan de elemento proyectable (nunca paran en un faltante, una nota ni un pasaje sin resolver) y en los extremos avisan sin proyectar", async () => {
  const m = await make();
  m.controller.activate();
  await until(() => m.shows().length === 1);
  const jump = async (dir, expectedItem) => {
    await m.settle(35);
    if (dir > 0) m.controller.nextItem(); else m.controller.prevItem();
    await until(() => m.lastOrigin()?.itemId === expectedItem);
    assert.equal(m.lastOrigin().position, 0, "se llega a la primera diapositiva");
  };
  await jump(1, 4);   // B (salta nota y faltante)
  await jump(1, 5);   // pasaje
  await jump(1, 6);   // C
  await jump(1, 7);   // A otra vez
  const n = m.shows().length;
  m.controller.nextItem();
  assert.match(m.get().notice ?? "", /No hay más elementos/);
  await m.settle(60);
  assert.equal(m.shows().length, n, "al final no se proyecta nada");
  await jump(-1, 6);
  await jump(-1, 5);
  await jump(-1, 4);
  await jump(-1, 1);
  m.controller.prevItem();
  assert.match(m.get().notice ?? "", /No hay elementos anteriores/);
  onlyOutputCommands(m.b);
  m.controller.dispose();
});

test("la MISMA canción dos veces: el elemento activo es el que se proyectó (por su id), nunca el primero con ese texto", async () => {
  const m = await make();
  m.controller.setCursor(6);   // la segunda A
  m.controller.activate();
  await until(() => m.shows().length === 1);
  assert.equal(m.lastOrigin().itemId, 7);
  assert.deepEqual(activeFromOutput(m.b.output(), m.get().list), { itemId: 7, position: 0 }, "activa la segunda, no la primera (mismo texto)");
  await m.settle(35);
  m.controller.nextSlide();
  await until(() => m.shows().length === 2);
  assert.deepEqual(m.lastOrigin(), { playlistId: m.list.id, itemId: 7, itemType: "song", position: 1 }, "sigue en la segunda copia");
  assert.equal(m.shows()[0].payload.blocks[0], m.A.sections[0].text);
  m.controller.dispose();
});

test("tras RECARGAR el panel el elemento activo se recupera del origin que confirma el backend, sin ninguna memoria del panel, y → continúa desde ahí", async () => {
  const m = await make();
  m.b.showOrigin({ playlistId: m.list.id, itemId: 7, itemType: "song", position: 1 }, m.A.sections[1].text);   // lo que había en pantalla antes de recargar
  const reloaded = createListController({ api: m.b.client, getOutput: () => m.b.output(), createFit: fakeFit, sendIntervalMs: 30, prefetchDelayMs: 10, today: () => "2026-11-29", storage: null });
  await reloaded.init();
  assert.deepEqual(reloaded.anchor(), { itemId: 7, position: 1 });
  assert.equal(reloaded.store.getState().cursor, 6, "el cursor cae en el elemento activo");
  reloaded.nextSlide();
  await until(() => m.shows().length === 1);
  assert.deepEqual(m.lastOrigin(), { playlistId: m.list.id, itemId: 7, itemType: "song", position: 2 });
  reloaded.dispose();
  m.controller.dispose();
});

test("un origin de otra lista, de un elemento que no existe o ausente (Biblia o Canciones sueltas): nada está activo y → proyecta el elemento del cursor", async () => {
  const m = await make();
  for (const origin of [{ playlistId: 999, itemId: 1, itemType: "song", position: 0 }, { playlistId: m.list.id, itemId: 888, itemType: "song", position: 0 }, null]) {
    if (origin) m.b.showOrigin(origin); else m.b.showForeign("texto de la Biblia");
    assert.equal(m.controller.anchor(), null);
    assert.equal(activeFromOutput(m.b.output(), m.get().list), null);
  }
  m.controller.nextSlide();
  await until(() => m.shows().length === 1);
  assert.equal(m.lastOrigin().itemId, 1);
  m.controller.dispose();
});

test("RÁFAGAS: con 8 pulsaciones seguidas a la salida no llega cada una y GANA LA ÚLTIMA", async () => {
  const b = createListBackend();
  const song = b.addSong(songOf("Canción larga", 14));
  b.addList({ name: "Domingo", date: "2026-11-29", items: [{ song: song.id }] });
  const controller = createListController({ api: b.client, getOutput: () => b.output(), createFit: fakeFit, sendIntervalMs: 40, prefetchDelayMs: 10, today: () => "2026-11-29", storage: null });
  await controller.init();
  controller.activate();
  await until(() => b.shows().length === 1);
  await tick(50);
  for (let i = 0; i < 8; i++) controller.nextSlide();
  await tick(150);
  assert.equal(b.shows().at(-1).payload.origin.position, 8, "la última pulsación es la que queda en pantalla");
  assert.ok(b.shows().length < 9, `se agruparon (${String(b.shows().length)} envíos para 9 diapositivas)`);
  assert.deepEqual(b.shows().map((s) => s.payload.origin.position), [...b.shows().map((s) => s.payload.origin.position)].sort((x, y) => x - y), "y nunca llega una vieja después de una nueva");
  controller.dispose();
});

test("RÁFAGAS entre elementos aún sin cargar: lo último que se pidió gana y lo anterior, que terminaba de cargar después, no se envía", async () => {
  const m = await make();
  for (const index of [3, 4, 5]) { m.controller.setCursor(index); m.controller.activate(); }   // sin esperar: B, pasaje y C
  await m.settle(150);
  assert.equal(m.shows().length, 1, "solo se proyectó lo último");
  assert.equal(m.lastOrigin().itemId, 6);
  m.controller.dispose();
});

test("un elemento FALTANTE no se proyecta: Enter lo avisa y la pantalla no cambia; el cursor sí puede pasar por él", async () => {
  const m = await make();
  m.controller.moveCursor(2);
  assert.equal(m.get().cursor, 2);
  assert.equal(m.get().list.items[2].isMissing, true);
  m.controller.activate();
  assert.match(m.get().notice ?? "", /Falta la canción «Canción D»/);
  await m.settle(60);
  assert.equal(m.shows().length, 0);
  m.controller.dispose();
});

test("una nota no se proyecta; solo si pide limpiar (apagado por defecto) quita el texto de la pantalla", async () => {
  const m = await make();
  m.b.addItem(m.list.id, { note: "Silencio", clear: true });
  m.b.event("playlist.changed", { commandId: "x", playlistId: m.list.id, revision: 2, kind: "itemAdded", itemId: 8, sourceId: null, copied: 0, skipped: 0 });
  await until(() => m.get().list.items.length === 8);
  m.controller.setCursor(1);   // «Ofrendas»: sin limpiar
  m.controller.activate();
  await m.settle(60);
  assert.equal(m.b.commands.length, 0, "no cambia nada en la pantalla");
  m.controller.setCursor(7);   // «Silencio»: limpia
  m.controller.activate();
  await until(() => m.b.sent("output.clearContent").length === 1);
  assert.equal(m.shows().length, 0);
  m.controller.dispose();
});

test("un pasaje se proyecta con su texto, su referencia y el aviso de derechos, con el origin del pasaje y el tema de la Biblia", async () => {
  const m = await make();
  m.controller.setCursor(4);
  m.controller.activate();
  await until(() => m.shows().length === 1);
  const sent = m.shows()[0].payload;
  assert.equal(sent.blocks.length, 3);
  assert.match(sent.blocks[0], /¹ uno sintético ² dos sintético/);
  assert.match(sent.blocks[1], /Libro sintético 3:1-2 · TST/);
  assert.equal(sent.blocks[2], "Aviso sintético");
  assert.deepEqual(sent.origin, { playlistId: m.list.id, itemId: 5, itemType: "passage", position: 0 });
  assert.equal(sent.theme.fontSize, 64, "el tema de la Biblia");
  assert.equal(sent.transitionMs, 280);
  m.controller.dispose();
});

test("FONDO: una canción con fondo propio manda UN setBackground (antes del texto) y no lo repite al navegar; una sin fondo no manda nada", async () => {
  const m = await make();
  m.controller.setCursor(5);   // C, con fondo
  m.controller.activate();
  await until(() => m.shows().length === 1);
  const order = m.b.commands.map((c) => c.type);
  assert.deepEqual(order, ["output.setBackground", "output.showContent"], "el fondo va ANTES del primer texto");
  assert.equal(m.b.sent("output.setBackground")[0].payload.path, BG.path);
  await m.settle(35);
  m.controller.nextSlide();
  await until(() => m.shows().length === 2);
  assert.equal(m.b.sent("output.setBackground").length, 1, "navegar dentro de la canción no reenvía el fondo");
  await m.settle(35);
  m.controller.setCursor(0);   // A: sin fondo propio
  m.controller.activate();
  await until(() => m.shows().length === 3);
  assert.equal(m.b.sent("output.setBackground").length, 1, "una canción sin fondo no manda NADA: el video actual no se corta");
  await m.settle(35);
  m.controller.setCursor(5);
  m.controller.activate();
  await until(() => m.shows().length === 4);
  assert.equal(m.b.sent("output.setBackground").length, 1, "el mismo fondo ya aplicado no se reenvía");
  m.controller.dispose();
});

test("si la salida rechaza la diapositiva (command.failed) el panel lo dice y deja de dar por pedida esa diapositiva", async () => {
  const m = await make();
  m.b.failNext("output.showContent", "output.invalidContent", "El slide no tiene texto.");
  m.controller.activate();
  await until(() => m.get().notice !== null);
  assert.match(m.get().notice, /La salida rechazó la diapositiva: El slide no tiene texto\./);
  assert.equal(m.get().requested.inflight, false);
  m.controller.dispose();
});

test("Esc quita el texto sin cambiar de elemento y → continúa con la siguiente diapositiva de lo último que se mostró", async () => {
  const m = await make();
  m.controller.activate();
  await until(() => m.shows().length === 1);
  await m.settle(35);
  m.controller.clear();
  await until(() => m.b.sent("output.clearContent").length === 1);
  assert.equal(m.b.sent("output.clearContent")[0].payload.transitionMs, 400);
  assert.equal(activeFromOutput(m.b.output(), m.get().list), null, "con la pantalla limpia nada está en vivo");
  m.controller.nextSlide();
  await until(() => m.shows().length === 2);
  assert.equal(m.lastOrigin().position, 1);
  m.controller.dispose();
});

test("en modo EDITAR nada se proyecta ni se limpia; al volver a EN VIVO todo funciona", async () => {
  const m = await make();
  m.controller.toggleMode();
  assert.equal(m.get().mode, "edit");
  m.controller.activate(); m.controller.nextSlide(); m.controller.nextItem(); m.controller.clear(); m.controller.moveCursor(1);
  await m.settle(80);
  assert.equal(m.b.commands.length, 0);
  assert.equal(m.get().cursor, 0, "tampoco se mueve el cursor");
  m.controller.toggleMode();
  m.controller.activate();
  await until(() => m.shows().length === 1);
  m.controller.dispose();
});

test("una canción borrada mientras tanto pasa a faltante (se vuelve a leer la lista) y su contenido memorizado se descarta; el cursor se queda en su elemento", async () => {
  const m = await make();
  m.controller.setCursor(3);
  await until(() => m.get().contents[4]?.status === "ready");
  m.b.removeSong(m.B.id);
  await until(() => m.get().list.items[3].isMissing);
  assert.notEqual(m.get().contents[4]?.status, "ready", "lo memorizado de la canción borrada ya no vale");
  assert.equal(m.get().cursor, 3);
  m.controller.dispose();
});

test("la lista se vuelve a leer con cada playlist.changed y el cursor sigue al MISMO elemento aunque cambie su posición", async () => {
  const m = await make();
  m.controller.setCursor(3);
  const id = m.get().list.items[3].id;
  m.b.lists.get(m.list.id).items.unshift({ ...m.b.lists.get(m.list.id).items[1], id: 50, title: "Nota nueva" });   // otro panel añadió una nota al principio
  m.b.event("playlist.changed", { commandId: "x", playlistId: m.list.id, revision: 2, kind: "itemAdded", itemId: 50, sourceId: null, copied: 0, skipped: 0 });
  await until(() => m.get().list.items.length === 8);
  assert.equal(m.get().list.items[m.get().cursor].id, id);
  m.controller.dispose();
});

test("PRECARGA: solo el elemento del cursor y el siguiente proyectable, de uno en uno y tras un reposo; moverse deprisa no carga nada de más", async () => {
  const m = await make();
  await until(() => m.get().contents[1]?.status === "ready" && m.get().contents[4]?.status === "ready");
  assert.deepEqual(Object.keys(m.get().contents).map(Number).sort((a, b) => a - b), [1, 4], "A (cursor) y B (el siguiente proyectable, saltando nota y faltante)");
  const before = m.b.queries.filter((q) => q.type === "song.get").length;
  for (let i = 0; i < 6; i++) m.controller.moveCursor(i % 2 === 0 ? 1 : -1);
  assert.equal(m.b.queries.filter((q) => q.type === "song.get").length, before, "mover el cursor no carga nada al instante");
  m.controller.dispose();
});

test("sin ninguna lista: el panel lo dice y no falla", async () => {
  const b = createListBackend();
  const controller = createListController({ api: b.client, getOutput: () => null, createFit: fakeFit, today: () => "2026-11-29", storage: null });
  await controller.init();
  assert.equal(controller.store.getState().loaded, true);
  assert.equal(controller.store.getState().list, null);
  controller.activate(); controller.nextSlide(); controller.nextItem();
  assert.equal(b.commands.length, 0);
  controller.dispose();
});

test("la lista abierta se recuerda y vuelve a abrirse; una recordada que ya no existe se ignora", async () => {
  const b = createListBackend();
  const one = b.addList({ name: "Una", date: "2026-11-29" });
  const two = b.addList({ name: "Dos", date: "2026-12-06" });
  let saved = null;
  const storage = { get: () => saved, set: (v) => { saved = v; } };
  const c1 = createListController({ api: b.client, getOutput: () => null, createFit: fakeFit, today: () => "2026-11-29", storage });
  await c1.init();
  assert.equal(c1.store.getState().openId, one.id, "la de hoy");
  await c1.openList(two.id);
  assert.equal(saved, String(two.id));
  c1.dispose();
  const c2 = createListController({ api: b.client, getOutput: () => null, createFit: fakeFit, today: () => "2026-11-29", storage });
  await c2.init();
  assert.equal(c2.store.getState().openId, two.id, "la recordada");
  c2.dispose();
  saved = "9999";
  const c3 = createListController({ api: b.client, getOutput: () => null, createFit: fakeFit, today: () => "2026-11-29", storage });
  await c3.init();
  assert.equal(c3.store.getState().openId, one.id);
  c3.dispose();
});
