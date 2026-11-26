import assert from "node:assert/strict";
import test, { after } from "node:test";
import { createListBackend, fakeFit, songOf } from "./list-fixtures.mjs";
import { line, tick, until } from "./song-fixtures.mjs";
import { setupDom, sleep } from "./dom.mjs";
import { closeServer, loadTs } from "./helper.mjs";

// Pestaña «Lista» (modo EN VIVO) con DOM simulado (happy-dom: simula el DOM pero NO mide; la medición es simulada): flujo de teclado, insignia derivada del estado
// confirmado, modo EDITAR sin controles y proyección de extremo a extremo hasta OutputScreen. Backend falso v13, cliente IPC real, controlador real. Datos sintéticos.
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

const BG = { kind: "video", path: "C:\\fondos\\sintetico.mp4", color: "#102030", muted: true };

function scenario(options = {}) {
  const b = createListBackend(options);
  const A = b.addSong(songOf("Canción A", 3));
  const B = b.addSong(songOf("Canción B", 2));
  const C = b.addSong(songOf("Canción C", 2, { background: BG }));
  const D = b.addSong(songOf("Canción D", 2));
  const list = b.addList({
    name: "Domingo sintético", date: "2026-11-29",
    items: [{ song: A.id }, { note: "Ofrendas", text: "Pasan los diáconos" }, { song: D.id }, { song: B.id }, { song: C.id }, { song: A.id }],
  });
  b.songs.delete(D.id);
  return { b, A, B, C, list };
}

async function mount({ options, reuse = null } = {}) {
  const s = reuse ?? scenario(options);
  const controller = createListController({ api: s.b.client, getOutput: () => s.b.output(), createFit: fakeFit, sendIntervalMs: 20, prefetchDelayMs: 10, today: () => "2026-11-29", storage: null, inflightMs: 400 });
  const editor = createListEditor({ api: s.b.client, list: controller, today: () => "2026-11-29", searchDebounceMs: 5, confirmTimeoutMs: 2000 });
  const container = win.document.createElement("div");
  win.document.body.appendChild(container);
  const root = createRoot(container);
  const render = async () => { await act(async () => { root.render(createElement(ListTab, { controller, editor, connected: true, output: s.b.output() })); }); };
  await render();
  await act(async () => { await sleep(60); });
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
  /** Cambia lo que CONFIRMA el backend (modo maestro, ventana) y vuelve a pintar con eso. */
  const confirmed = async (fn) => { fn(s.b); await render(); };
  return { ...s, controller, editor, container, q, qa, settle, press, render, confirmed, unmount: async () => { await act(async () => { root.unmount(); }); editor.dispose(); controller.dispose(); } };
}

const rows = (m) => m.qa('[data-testid="items"] [role="option"]');
const activeRows = (m) => rows(m).filter((r) => r.dataset.active === "true").map((r) => Number(r.dataset.itemId));
const cursorRow = (m) => Number(rows(m).find((r) => r.dataset.cursor === "true")?.dataset.itemId);

test("la lista muestra sus elementos, marca los FALTANTES y avisa arriba; cursor claro; sin elemento activo todavía (LISTO)", async () => {
  const m = await mount();
  try {
    assert.equal(rows(m).length, 6);
    assert.equal(cursorRow(m), 1);
    assert.deepEqual(activeRows(m), []);
    assert.equal(m.q('[data-testid="live-badge"]').textContent, "LISTO");
    const missingRow = rows(m)[2];
    assert.equal(missingRow.dataset.problem, "true");
    assert.match(missingRow.textContent, /Falta: Canción D/);
    assert.match(m.q('[data-testid="missing-banner"]').textContent, /Falta 1 elemento/);
    assert.match(rows(m)[1].textContent, /Pasan los diáconos/, "la nota enseña su texto");
    assert.equal(m.q('[data-testid="mode-chip"]').textContent, "Modo EN VIVO · Ctrl+E para editar");
    assert.equal(m.b.commands.length, 0, "solo lecturas");
  } finally { await m.unmount(); }
});

test("teclas: ↓/↑ mueven el cursor SIN proyectar; Enter activa; → avanza; Ctrl+→ salta al siguiente proyectable; Esc quita el texto", async () => {
  const m = await mount();
  try {
    await m.press("ArrowDown"); await m.press("ArrowDown");
    assert.equal(cursorRow(m), 3, "el cursor pasa por la nota y por el faltante");
    await m.press("ArrowUp");
    assert.equal(cursorRow(m), 2);
    assert.equal(m.b.commands.length, 0, "mover el cursor no manda nada a la salida");
    await m.press("ArrowDown"); await m.press("ArrowDown");   // cursor en B (elemento 4)
    await m.press("Enter");
    await until(() => m.b.shows().length === 1);
    await m.settle();
    assert.deepEqual(m.b.shows()[0].payload.origin, { playlistId: m.list.id, itemId: 4, itemType: "song", position: 0 });
    assert.deepEqual(activeRows(m), [4], "el activo sale del estado confirmado");
    await m.press("ArrowRight");
    await until(() => m.b.shows().length === 2);
    assert.equal(m.b.shows()[1].payload.origin.position, 1);
    await m.settle(40);
    await m.press("ArrowRight", { ctrlKey: true });   // salta a C (elemento 5), con su fondo
    await until(() => m.b.shows().length === 3);
    assert.equal(m.b.shows()[2].payload.origin.itemId, 5);
    assert.equal(m.b.sent("output.setBackground").length, 1);
    await m.settle(40);
    await m.press("ArrowLeft", { ctrlKey: true });
    await until(() => m.b.shows().length === 4);
    assert.equal(m.b.shows()[3].payload.origin.itemId, 4, "Ctrl+← vuelve a B");
    await m.press("Escape");
    await until(() => m.b.sent("output.clearContent").length === 1);
    await m.settle();
    assert.deepEqual(activeRows(m), [], "con la pantalla limpia nada está en vivo");
    assert.equal(m.q('[data-testid="live-badge"]').textContent, "LISTO");
  } finally { await m.unmount(); }
});

test("la insignia sale del estado CONFIRMADO: EN VIVO, OCULTO con el motivo (Negro, Limpiar, Logo, Congelado, pausa, cerrada) y vuelve; los controles maestros solo cambian la insignia", async () => {
  const m = await mount();
  try {
    await m.press("Enter");
    await until(() => m.b.shows().length === 1);
    await m.settle();
    const badge = () => m.q('[data-testid="live-badge"]').textContent;
    const rowBadge = () => m.q('[data-testid="row-badge"]').textContent;
    assert.equal(badge(), "EN VIVO");
    assert.match(rowBadge(), /^EN VIVO · 1\/3$/, "la fila activa dice en qué diapositiva está");
    const sentBefore = m.b.commands.length;
    for (const [master, text] of [["black", "OCULTO · Negro"], ["clearText", "OCULTO · Texto limpio"], ["logo", "OCULTO · Logo"], ["freeze", "OCULTO · Congelado"], ["live", "EN VIVO"]]) {
      await m.confirmed((b) => b.setMaster(master));
      assert.equal(badge(), text);
    }
    await m.confirmed((b) => b.setWindow({ isPaused: true }));
    assert.equal(badge(), "OCULTO · Salida en pausa");
    await m.confirmed((b) => b.setWindow({ isPaused: false, isOpen: false }));
    assert.equal(badge(), "OCULTO · Salida cerrada");
    await m.confirmed((b) => b.setWindow({ isOpen: true }));
    assert.equal(badge(), "EN VIVO");
    assert.equal(m.b.commands.length, sentBefore, "cambiar de modo maestro no manda nada desde la pestaña");
    // Algo que NO viene de la lista (Biblia o Canciones sueltas): la lista ya no está en pantalla.
    await m.confirmed((b) => b.showForeign("texto de otra pestaña"));
    assert.equal(badge(), "LISTO");
    assert.deepEqual(activeRows(m), []);
  } finally { await m.unmount(); }
});

test("la misma canción dos veces en la lista: solo se marca como activa la fila que se proyectó", async () => {
  const m = await mount();
  try {
    await m.press("ArrowDown", {}, win.document.body);
    // cursor al último elemento (la segunda A)
    for (let i = 0; i < 4; i++) await m.press("ArrowDown");
    assert.equal(cursorRow(m), 6);
    await m.press("Enter");
    await until(() => m.b.shows().length === 1);
    await m.settle();
    assert.deepEqual(activeRows(m), [6], "la segunda A, no la primera (mismo texto)");
  } finally { await m.unmount(); }
});

test("tras RECARGAR el panel (controlador nuevo, página nueva) la fila activa y la insignia salen del estado de la salida, sin memoria", async () => {
  const first = await mount();
  await first.press("ArrowDown"); await first.press("ArrowDown"); await first.press("ArrowDown");
  await first.press("Enter");
  await until(() => first.b.shows().length === 1);
  await first.settle();
  const shared = { b: first.b, A: first.A, B: first.B, C: first.C, list: first.list };
  await first.unmount();   // «recargar»: se pierde TODO lo del panel
  const second = await mount({ reuse: shared });
  try {
    assert.deepEqual(activeRows(second), [4]);
    assert.equal(second.q('[data-testid="live-badge"]').textContent, "EN VIVO");
    assert.equal(cursorRow(second), 4, "el cursor cae en el elemento activo");
    await second.press("ArrowRight");
    await until(() => second.b.shows().length === 2);
    assert.deepEqual(second.b.shows()[1].payload.origin, { playlistId: second.list.id, itemId: 4, itemType: "song", position: 1 }, "→ continúa donde estaba la salida");
  } finally { await second.unmount(); }
});

test("FIN: al terminar el elemento se queda y dice «FIN · siguiente: …» con el título del siguiente; Ctrl+→ pasa de verdad", async () => {
  const m = await mount();
  try {
    for (let i = 0; i < 3; i++) await m.press("ArrowDown");   // B (2 diapositivas)
    await m.press("Enter");
    await until(() => m.b.shows().length === 1);
    await m.settle(40);
    await m.press("ArrowRight");
    await until(() => m.b.shows().length === 2);
    await m.settle(40);
    await m.press("ArrowRight");   // una de más
    await m.settle();
    assert.equal(m.b.shows().length, 2, "no se proyecta nada");
    assert.equal(m.q('[data-testid="end-marker"]').textContent.startsWith("FIN · siguiente: «Canción C»"), true);
    await m.press("ArrowRight", { ctrlKey: true });
    await until(() => m.b.shows().length === 3);
    assert.equal(m.b.shows()[2].payload.origin.itemId, 5);
    await m.settle();
    assert.equal(m.q('[data-testid="end-marker"]'), null);
  } finally { await m.unmount(); }
});

test("MINIATURA del siguiente elemento: su título y su primera diapositiva, sin proyectar nada; salta el faltante y la nota", async () => {
  const m = await mount();
  try {
    await m.settle(120);
    // Con nada en pantalla, lo siguiente que proyectará → es el elemento del cursor.
    assert.equal(m.q('[data-testid="next-title"]').textContent, "Canción A");
    await m.press("Enter");
    await until(() => m.b.shows().length === 1);
    await m.settle(150);
    assert.equal(m.q('[data-testid="next-title"]').textContent, "Canción B", "tras A viene B: se saltan la nota y la canción faltante");
    const thumb = m.q('[data-testid="next-preview"] [data-testid="slide-canvas"]');
    assert.ok(thumb, "hay miniatura");
    assert.ok(thumb.textContent.includes(line(1, 0)), "con la primera diapositiva de B (letra sintética)");
    assert.equal(m.b.commands.length, 1, "preparar la vista previa no manda nada a la salida (solo la proyección de A)");
    await m.press("ArrowRight", { ctrlKey: true }); await m.settle(40);
    await m.press("ArrowRight", { ctrlKey: true });   // C
    await until(() => m.b.shows().length === 3);
    await m.settle(150);
    assert.equal(m.q('[data-testid="next-title"]').textContent, "Canción A", "tras C viene la segunda A");
  } finally { await m.unmount(); }
});
test("el modo EN VIVO NO dibuja controles de edición; Ctrl+E alterna con el modo EDITAR, donde las teclas de proyección no hacen nada", async () => {
  const m = await mount();
  try {
    const labels = m.qa("button").map((b) => b.textContent);
    assert.ok(labels.every((t) => !/añadir|agregar|mover|quitar elemento|borrar|eliminar|duplicar|renombrar|nueva lista/i.test(t)), `botones: ${labels.join(" | ")}`);
    assert.equal(m.qa("input, textarea").length, 0, "ni cuadros de texto");
    assert.equal(m.qa("[draggable='true']").length, 0, "ni arrastrar para reordenar");
    assert.equal(m.q('[data-testid="list-editor"]'), null);
    await m.press("e", { ctrlKey: true });
    assert.equal(m.q('[data-testid="list-tab"]').dataset.mode, "edit");
    assert.ok(m.q('[data-testid="list-editor"]'), "aparece el editor");
    assert.equal(rows(m).length, 0, "la lista en vivo se esconde");
    await m.press("Enter"); await m.press("ArrowRight"); await m.press("ArrowRight", { ctrlKey: true }); await m.press("Escape");
    await m.settle();
    assert.equal(m.b.commands.length, 0, "en modo EDITAR esas teclas no proyectan ni limpian");
    await m.press("e", { ctrlKey: true });
    assert.equal(m.q('[data-testid="list-tab"]').dataset.mode, "live");
    assert.equal(rows(m).length, 6);
  } finally { await m.unmount(); }
});
test("con el foco en un cuadro de texto ninguna tecla de la pestaña actúa (Ctrl+←/→ y Ctrl+E son del cuadro)", async () => {
  const m = await mount();
  try {
    const input = win.document.createElement("textarea");
    win.document.body.appendChild(input);
    input.focus();
    for (const [key, opts] of [["ArrowRight", { ctrlKey: true }], ["ArrowLeft", { ctrlKey: true }], ["Enter", {}], ["ArrowDown", {}], ["Escape", {}], ["e", { ctrlKey: true }]]) {
      const event = await m.press(key, opts, input);
      assert.equal(event.defaultPrevented, false, `${key} no se consume`);
    }
    assert.equal(m.b.commands.length, 0);
    assert.equal(m.q('[data-testid="list-tab"]').dataset.mode, "live");
    assert.equal(cursorRow(m), 1);
    input.remove();
  } finally { await m.unmount(); }
});

test("las teclas de un elemento faltante no proyectan nada y el aviso se ve", async () => {
  const m = await mount();
  try {
    await m.press("ArrowDown"); await m.press("ArrowDown");
    await m.press("Enter");
    await m.settle();
    assert.equal(m.b.shows().length, 0);
    assert.match(m.q('[data-testid="notice"]').textContent, /Falta la canción «Canción D»/);
  } finally { await m.unmount(); }
});

test("EXTREMO A EXTREMO: una canción sintética de la lista, proyectada con Enter y →, llega a la salida real (OutputScreen) con su letra y su tema", async () => {
  const m = await mount();
  try {
    await m.press("Enter");
    await until(() => m.b.shows().length === 1);
    await m.settle(40);
    await m.press("ArrowRight");
    await until(() => m.b.shows().length === 2);
    const sent = m.b.shows()[1].payload;
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
    assert.equal(sent.blocks.length, 1);
    assert.ok(shown.includes(line(2, 0)), "se ve la letra de la 2.ª diapositiva (sintética)");
    assert.equal(sent.theme.fontSize, 60, "el tema de canciones");
    assert.deepEqual(sent.origin, { playlistId: m.list.id, itemId: 1, itemType: "song", position: 1 });
    await act(async () => { outRoot.unmount(); });
  } finally { await m.unmount(); }
});

test("PANEL SIN BLOQUEOS: la pestaña no hace bucles ni mediciones al mover el cursor y el trabajo pesado va por tandas", async () => {
  const m = await mount();
  try {
    const t0 = performance.now();
    for (let i = 0; i < 40; i++) await m.press(i % 2 === 0 ? "ArrowDown" : "ArrowUp");
    assert.ok(performance.now() - t0 < 1500, "40 pulsaciones sin bloquear");
    assert.equal(m.b.commands.length, 0);
  } finally { await m.unmount(); }
});
