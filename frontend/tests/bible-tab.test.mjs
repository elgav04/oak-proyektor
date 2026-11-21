import assert from "node:assert/strict";
import test, { after } from "node:test";
import { createFakeBackend, tick, until, verseText } from "./bible-fixtures.mjs";
import { setupDom, sleep } from "./dom.mjs";
import { closeServer, loadTs } from "./helper.mjs";

// Flujo de TECLADO del componente (happy-dom: simula el DOM pero NO mide; la medición es simulada) y proyección de un pasaje de extremo a extremo
// (backend falso → cliente IPC real → controlador → comando output.showContent → OutputScreen real). Datos sintéticos.
const win = setupDom();
for (const name of ["KeyboardEvent", "MouseEvent", "InputEvent", "HTMLInputElement", "HTMLSelectElement", "HTMLTextAreaElement", "HTMLButtonElement", "HTMLLabelElement"]) {
  if (win[name]) globalThis[name] = win[name];
}
const { act, createElement } = await import("react");
const { createRoot } = await import("react-dom/client");
const { BibleTab } = await loadTs("/src/control/bible/BibleTab.tsx");
const { createBibleController } = await loadTs("/src/control/bible/controller.ts");
const { OutputScreen } = await loadTs("/src/output/OutputScreen.tsx");
after(closeServer);

const fakeFit = (cap) => () => ({ fits: (blocks) => blocks.join(" ").length <= cap, dispose: () => undefined });

async function mount({ cap = 400, backend: backendOptions } = {}) {
  const backend = createFakeBackend(backendOptions);
  const controller = createBibleController({ api: backend.client, createFit: fakeFit(cap), searchDebounceMs: 5, recordDelayMs: 25, sendIntervalMs: 20 });
  const container = win.document.createElement("div");
  win.document.body.appendChild(container);
  const root = createRoot(container);
  await act(async () => { root.render(createElement(BibleTab, { controller, connected: true, onOpenSettings: () => undefined })); });
  await act(async () => { await sleep(30); });
  const q = (sel) => container.querySelector(sel);
  const input = () => q('input[type="search"]');
  const press = async (key, opts = {}, target = win) => {
    await act(async () => {
      target.dispatchEvent(new win.KeyboardEvent("keydown", { key, bubbles: true, cancelable: true, ...opts }));
      await sleep(0);
    });
  };
  const type = async (text) => {
    await act(async () => {
      const set = Object.getOwnPropertyDescriptor(win.HTMLInputElement.prototype, "value").set;
      set.call(input(), text);
      input().dispatchEvent(new win.Event("input", { bubbles: true }));
      await sleep(40);
    });
  };
  const settle = async (ms = 60) => { await act(async () => { await sleep(ms); }); };
  const selectedVerses = () => [...container.querySelectorAll('[role="listbox"] [role="option"][aria-selected="true"][data-active]')].map((n) => n.textContent.slice(0, 2).trim());
  return { backend, controller, container, root, q, input, press, type, settle, selectedVerses, unmount: async () => { await act(async () => { root.unmount(); }); controller.dispose(); } };
}

test("flujo completo con el teclado: «/» enfoca, escribir + Enter proyecta, flechas, Mayús+flecha, Enter, Esc", async () => {
  const m = await mount();
  assert.equal(win.document.activeElement === m.input(), false);
  await m.press("/");
  assert.equal(win.document.activeElement, m.input(), "«/» lleva el foco al cuadro de búsqueda");

  await m.type("alfa 1:2");
  await m.settle();
  await until(() => m.controller.store.getState().built !== null);
  await m.press("Enter", {}, m.input());                  // Enter en el cuadro: resuelve y proyecta
  await until(() => m.backend.sent("output.showContent").length === 1);
  assert.match(m.backend.lastContent().blocks[1], /^Alfa 1:2 /);
  assert.notEqual(win.document.activeElement, m.input(), "tras Enter el foco sale del cuadro para que las flechas muevan los versículos");
  assert.match(m.container.textContent, /EN VIVO/);

  await m.press("ArrowDown");                              // 1:3 (sin «proyectar al navegar»: no manda nada)
  await m.settle(60);
  assert.equal(m.backend.sent("output.showContent").length, 1);
  assert.equal(m.controller.store.getState().active, 2);
  assert.match(m.container.textContent, /LISTO/, "ya no es lo que está en pantalla");
  await m.press("Enter");
  await until(() => m.backend.sent("output.showContent").length === 2);
  assert.match(m.backend.lastContent().blocks[1], /^Alfa 1:3 /);

  await m.press("ArrowDown", { shiftKey: true });          // amplía a 1:3-4
  await m.press("Enter");
  await until(() => m.backend.sent("output.showContent").length === 3);
  assert.match(m.backend.lastContent().blocks[1], /^Alfa 1:3-4 /);
  assert.deepEqual(m.selectedVerses().length, 2);

  await m.press("ArrowUp");                                // colapsa a un versículo (1:3)
  assert.equal(m.controller.store.getState().anchor, m.controller.store.getState().active);

  await m.press("Escape");
  await until(() => m.backend.sent("output.clearContent").length === 1);
  assert.deepEqual(m.backend.sent("output.clearContent")[0].payload, { transitionMs: 280 });
  await m.unmount();
});

test("con el foco en el cuadro de búsqueda las flechas NO mueven versículos y Esc borra el texto sin quitar lo proyectado", async () => {
  const m = await mount();
  await m.type("alfa 1:5");
  await m.settle();
  await until(() => m.controller.store.getState().built !== null);
  m.q('input[type="search"]').focus();
  const before = m.controller.store.getState().active;
  await m.press("ArrowDown", {}, m.input());
  await m.press("ArrowUp", {}, m.input());
  assert.equal(m.controller.store.getState().active, before);
  await m.press("Escape", {}, m.input());
  await m.settle(40);
  assert.equal(m.controller.store.getState().query, "", "Esc en el cuadro borra el texto");
  assert.equal(m.backend.sent("output.clearContent").length, 0, "pero no quita lo proyectado");
  await m.unmount();
});

test("los atajos globales (Ctrl+Alt+Shift+B/C/L/M/R) no activan nada en la pestaña y el panel no los consume", async () => {
  const m = await mount();
  await m.type("alfa 1:1");
  await m.settle();
  await until(() => m.controller.store.getState().built !== null);
  const before = JSON.stringify([m.controller.store.getState().active, m.backend.commands.length]);
  for (const k of ["B", "C", "L", "M", "R", "b", "c", "l", "m", "r"]) {
    await act(async () => {
      const ev = new win.KeyboardEvent("keydown", { key: k, ctrlKey: true, altKey: true, shiftKey: true, bubbles: true, cancelable: true });
      win.dispatchEvent(ev);
      assert.equal(ev.defaultPrevented, false, `Ctrl+Alt+Shift+${k} no debe ser consumida por el panel`);
    });
  }
  await m.settle(40);
  assert.equal(JSON.stringify([m.controller.store.getState().active, m.backend.commands.length]), before);
  await m.unmount();
});

test("versículo largo: el indicador muestra N/M y →/← (y Av Pág/Re Pág) cambian de página y la proyectan", async () => {
  const m = await mount({ cap: 200, backend: { longVerses: new Set(["1:1:3"]) } });
  await m.type("alfa 1:3");
  await m.settle();
  await until(() => m.controller.store.getState().built !== null);
  const total = m.controller.store.getState().built.pages.length;
  assert.ok(total >= 3);
  assert.equal(m.q('[aria-label="Página"]').textContent, `1/${String(total)}`);
  await m.press("ArrowRight");
  await until(() => m.backend.sent("output.showContent").length === 1);
  assert.equal(m.q('[aria-label="Página"]').textContent, `2/${String(total)}`);
  await m.settle(40);
  await m.press("PageDown");
  await m.settle(60);
  assert.equal(m.q('[aria-label="Página"]').textContent, `3/${String(total)}`);
  await m.press("PageUp");
  await m.settle(60);
  await m.press("ArrowLeft");
  await m.settle(60);
  assert.equal(m.q('[aria-label="Página"]').textContent, `1/${String(total)}`);
  assert.equal(m.backend.lastContent().blocks[0], m.controller.store.getState().built.pages[0][0]);
  await m.unmount();
});

test("clic en una cita del historial la vuelve a proyectar", async () => {
  const m = await mount();
  m.backend.state.history.push({ id: 7, bible: "SIN", bookNumber: 3, bookName: "Gamma", chapter: 1, verseStart: 2, verseEnd: 2, label: "Gamma 1:2", citedAt: "2026-01-01T00:00:00Z" });
  await act(async () => { await m.controller.init(); });
  await m.settle(30);
  const button = [...m.container.querySelectorAll("button")].find((b) => b.textContent.includes("Gamma 1:2"));
  assert.ok(button, "la cita aparece en el historial");
  await act(async () => { button.click(); await sleep(60); });
  await until(() => m.backend.sent("output.showContent").length === 1);
  assert.equal(m.backend.lastContent().blocks[1], "Gamma 1:2 · SIN");
  await m.unmount();
});

test("EXTREMO A EXTREMO: un pasaje sintético escrito en el cuadro llega a la salida real (OutputScreen) con versículos, referencia, nombre corto y aviso", async () => {
  const m = await mount({ cap: 600, backend: { attribution: "Aviso inventado de prueba", settings: { showAttribution: true, shortNames: [{ bible: "SIN", shortName: "S.I." }] } } });
  await m.type("alfa 1:1-2");
  await m.settle();
  await until(() => m.controller.store.getState().built !== null);
  await m.press("Enter");
  await until(() => m.backend.sent("output.showContent").length === 1);
  const sent = m.backend.lastContent();

  // La salida recibe un snapshot con ese contenido (en la app real lo arma C#; aquí el mismo contenido).
  const state = {
    schemaVersion: 1, revision: 0, master: "live",
    background: { kind: "color", color: "#102030", mediaUrl: null, fileName: null, muted: true, transitionMs: 0, warning: null },
    content: { slide: { id: "e2e", blocks: sent.blocks.map((text) => ({ text })), theme: sent.theme }, transitionMs: sent.transitionMs },
    overlays: { items: [] },
  };
  const out = win.document.createElement("div");
  win.document.body.appendChild(out);
  const outRoot = createRoot(out, { onCaughtError: () => undefined });
  await act(async () => { outRoot.render(createElement(OutputScreen, { state, seq: 1 })); });
  await act(async () => { await sleep(80); });
  const text = [...out.querySelectorAll(".content-text")].map((n) => n.textContent).join("|");
  assert.ok(text.includes(`¹ ${verseText(1, 1, 1)}`), "primer versículo con su superíndice");
  assert.ok(text.includes(`² ${verseText(1, 1, 2)}`), "segundo versículo con su superíndice");
  assert.ok(text.includes("Alfa 1:1-2 · S.I."), "referencia y nombre corto");
  assert.ok(text.includes("Aviso inventado de prueba"), "aviso de derechos escrito por el operador");
  await act(async () => { outRoot.unmount(); });
  await m.unmount();
});

test("el aviso de derechos VACÍO no muestra nada en la salida aunque la opción esté activada", async () => {
  const m = await mount({ cap: 600, backend: { attribution: "", settings: { showAttribution: true } } });
  await m.type("alfa 1:1");
  await m.settle();
  await until(() => m.controller.store.getState().built !== null);
  await m.press("Enter");
  await until(() => m.backend.sent("output.showContent").length === 1);
  assert.equal(m.backend.lastContent().blocks.length, 2, "solo el texto y la referencia");
  await m.unmount();
});
