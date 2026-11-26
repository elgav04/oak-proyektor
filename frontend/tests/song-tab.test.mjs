import assert from "node:assert/strict";
import test, { after } from "node:test";
import { body, createSongBackend, line, memoryDraftStorage, tick, until } from "./song-fixtures.mjs";
import { setupDom, sleep } from "./dom.mjs";
import { closeServer, loadTs } from "./helper.mjs";

// Flujo de TECLADO y de pantalla de la pestaña (happy-dom: simula el DOM pero NO mide; la medición es simulada) de extremo a extremo:
// backend falso (protocolo v12) → cliente IPC real → controlador real → componentes reales. Todo con datos sintéticos.
const win = setupDom();
for (const name of ["KeyboardEvent", "MouseEvent", "InputEvent", "PointerEvent", "HTMLInputElement", "HTMLSelectElement", "HTMLTextAreaElement", "HTMLButtonElement", "HTMLLabelElement", "HTMLLIElement", "HTMLUListElement", "HTMLOListElement"]) {
  if (win[name]) globalThis[name] = win[name];
}
globalThis.requestAnimationFrame = (cb) => setTimeout(cb, 0);
globalThis.cancelAnimationFrame = (id) => clearTimeout(id);
const { act, createElement } = await import("react");
const { createRoot } = await import("react-dom/client");
const { SongsTab } = await loadTs("/src/control/songs/SongsTab.tsx");
const { createSongController, isDirty } = await loadTs("/src/control/songs/controller.ts");
const { safeMinFontSize } = await loadTs("/src/control/bible/slide.ts");
after(closeServer);

const fakeFit = () => ({ fits: (blocks) => blocks[0].split("\n").reduce((n, l) => n + Math.max(1, Math.ceil(l.length / 40)), 0) <= 4, dispose: () => undefined });

const SONG = {
  title: "Canción sintética", author: "Autora Ficticia",
  sections: [{ label: "Verso 1", text: body(1, 3) }, { label: "Coro", text: body(2, 2) }, { label: "Verso 2", text: body(3, 2) }],
  arrangement: [0, 1, 2, 1, 1],
};

async function mount({ songs = [SONG], usedIn = {}, connected = true, storage = null } = {}) {
  const backend = createSongBackend({ songs, usedIn });
  const controller = createSongController({ api: backend.client, createFit: fakeFit, storage, sendIntervalMs: 20, selectDelayMs: 10, searchDebounceMs: 5, parseDebounceMs: 5, slidesDelayMs: 5, draftDelayMs: 5, saveTimeoutMs: 400 });
  const container = win.document.createElement("div");
  win.document.body.appendChild(container);
  const root = createRoot(container);
  let output = null;
  const render = async () => { await act(async () => { root.render(createElement(SongsTab, { controller, connected, output })); }); };
  await render();
  await act(async () => { await sleep(40); });
  /** Lo que CONFIRMA el backend (`output.stateChanged`): de aquí sale la insignia, no de lo que el panel envió. */
  const confirm = async ({ blocks = null, master = "live", isOpen = true, isPaused = false } = {}) => {
    output = { seq: (output?.seq ?? 0) + 1, state: { master, content: { slide: blocks ? { id: "s", blocks: blocks.map((text) => ({ text })), theme: {} } : null, transitionMs: 0 } }, window: { isOpen, isPaused } };
    await render();
  };
  const q = (sel) => container.querySelector(sel);
  const qa = (sel) => [...container.querySelectorAll(sel)];
  const settle = async (ms = 80) => { await act(async () => { await sleep(ms); }); };
  const press = async (key, opts = {}, target = win) => {
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
      await sleep(50);
    });
  };
  const click = async (element) => { await act(async () => { element.dispatchEvent(new win.MouseEvent("click", { bubbles: true, cancelable: true })); await sleep(0); }); };
  const buttonByText = (re) => qa("button").find((b) => re.test(b.textContent));
  const text = () => container.textContent;
  const editorReady = async () => { await until(() => controller.store.getState().editor?.slides); await settle(60); };
  return { backend, confirm, controller, container, q, qa, settle, press, typeInto, click, buttonByText, text, editorReady, unmount: async () => { await act(async () => { root.unmount(); }); controller.dispose(); } };
}

test("la lista muestra las canciones; «/» busca, ↓ elige y Ctrl+E abre la canción con el teclado", async () => {
  const m = await mount({ songs: [SONG, { title: "Otra pieza", sections: [{ label: "Verso 1", text: "nada que ver aquí" }] }] });
  try {
    assert.match(m.text(), /Canción sintética/);
    assert.match(m.text(), /Otra pieza/);
    assert.equal(m.qa('[role="option"]').length, 2);

    await m.press("/");
    assert.ok(win.document.activeElement === m.q('input[type="search"]'), "«/» lleva el foco al cuadro de búsqueda");   // assert.ok: si falla no intenta imprimir nodos del DOM
    await m.typeInto(m.q('input[type="search"]'), "canci");
    await m.settle();
    assert.equal(m.backend.queries.some((x) => x.type === "song.search"), true);
    assert.deepEqual(m.controller.store.getState().results.map((s) => s.title), ["Canción sintética"], "la búsqueda deja solo la que coincide");

    // Se borra la búsqueda (Esc en el cuadro) y con el foco fuera, ↓ baja a la segunda y Enter la abre.
    await m.press("Escape", {}, m.q('input[type="search"]'));
    await m.settle();
    assert.equal(m.controller.store.getState().results, null);
    win.document.activeElement.blur?.();
    await m.press("ArrowDown", {}, win.document.body);
    assert.equal(m.controller.store.getState().listIndex, 1);
    await m.press("e", { ctrlKey: true }, win.document.body);
    await until(() => m.controller.store.getState().mode === "editor");
    await m.editorReady();
    assert.equal(m.q('input[aria-label="Título de la canción"]').value, "Otra pieza", "↓ bajó a la segunda canción de la lista y Ctrl+E la abrió");
  } finally {
    await m.unmount();
  }
});
test("el editor: título, texto, orden y diapositivas con el tema; «sin guardar» visible y Ctrl+S guarda", async () => {
  const m = await mount();
  await act(async () => { m.controller.openSong(1); });
  await m.editorReady();

  assert.equal(m.q('input[aria-label="Título de la canción"]').value, "Canción sintética");
  assert.match(m.q('[data-testid="dirty-badge"]').textContent, /Guardada/);
  assert.equal(m.q('textarea[aria-label="Texto de la canción"]').value.startsWith("[Verso 1]"), true);
  assert.equal(m.qa('[aria-label="Orden de reproducción"] [data-order-row]').length, 5);
  assert.ok(m.qa('[data-testid="slide-canvas"]').length >= 5, "una miniatura por diapositiva derivada");
  assert.ok(m.qa('li[data-boundary="manual"], li[data-boundary="auto"], li[data-boundary="section"]').length >= 5);

  const area = m.q('textarea[aria-label="Texto de la canción"]');
  await m.typeInto(area, area.value.replace(line(2, 0), "linea nueva del coro"));
  await m.settle(120);
  assert.match(m.q('[data-testid="dirty-badge"]').textContent, /SIN GUARDAR/);
  assert.match(m.text(), /Guardar en la canción \(Coro ×3\) · Ctrl\+S/, "el botón dice cuántas veces se repite lo editado");

  const event = await m.press("s", { ctrlKey: true }, area);
  assert.equal(event.defaultPrevented, true, "Ctrl+S es del panel (no abre el guardado del navegador)");
  await until(() => m.backend.sent("song.save").length === 1);
  await until(() => !isDirty(m.controller.store.getState().editor));
  await m.settle(60);
  assert.match(m.q('[data-testid="dirty-badge"]').textContent, /Guardada/);
  assert.equal(m.backend.sent("song.save")[0].payload.expectedRevision, 1);
  await m.unmount();
});

test("orden con el teclado: Alt+↓ mueve, Ctrl+D repite sin duplicar el texto, Supr quita, flechas cambian de fila", async () => {
  const m = await mount();
  await act(async () => { m.controller.openSong(1); });
  await m.editorReady();
  const rows = () => m.qa("[data-order-row]");
  const labels = () => rows().map((r) => r.getAttribute("aria-label").replace(/^\d+\.\s*/, ""));
  assert.deepEqual(labels(), ["Verso 1", "Coro", "Verso 2", "Coro", "Coro"]);
  const sectionsBefore = JSON.stringify(m.controller.store.getState().editor.sections);

  await m.press("ArrowDown", { altKey: true }, rows()[0]);
  assert.deepEqual(labels(), ["Coro", "Verso 1", "Verso 2", "Coro", "Coro"]);
  await m.press("d", { ctrlKey: true }, rows()[1]);
  assert.deepEqual(labels(), ["Coro", "Verso 1", "Verso 1", "Verso 2", "Coro", "Coro"]);
  await m.press("Delete", {}, rows()[2]);
  assert.deepEqual(labels(), ["Coro", "Verso 1", "Verso 2", "Coro", "Coro"]);
  await m.press("ArrowUp", { altKey: true }, rows()[3]);
  assert.deepEqual(labels(), ["Coro", "Verso 1", "Coro", "Verso 2", "Coro"]);
  assert.equal(JSON.stringify(m.controller.store.getState().editor.sections), sectionsBefore, "mover y repetir no tocan el texto");

  // Con los botones (alternativa sin ratón ni atajos): subir, bajar, repetir, quitar.
  await m.click(m.buttonByText(/^↓$/));
  assert.equal(m.controller.store.getState().editor.order.length, 5);
  assert.equal(m.qa('[data-order-row] span').some((s) => /×3/.test(s.textContent)), true, "la insignia ×N dice cuántas veces aparece la sección");
  await m.unmount();
});

test("un atajo sobre el cuadro de texto no se cuela: Supr y las flechas escriben, no tocan el orden ni la lista", async () => {
  const m = await mount();
  await act(async () => { m.controller.openSong(1); });
  await m.editorReady();
  const before = JSON.stringify(m.controller.store.getState().editor.order);
  const area = m.q('textarea[aria-label="Texto de la canción"]');
  for (const k of ["Delete", "ArrowUp", "ArrowDown", "Escape"]) {
    const ev = await m.press(k, {}, area);
    assert.equal(ev.defaultPrevented, false, `${k} en el texto no debe interceptarse`);
  }
  assert.equal(JSON.stringify(m.controller.store.getState().editor.order), before);
  assert.equal(m.controller.store.getState().mode, "editor", "Esc en el texto no cierra el editor");
  await m.unmount();
});

test("Esc con cambios sin guardar pregunta en la pantalla; «Seguir editando» conserva todo y «Descartar» sale", async () => {
  const m = await mount();
  await act(async () => { m.controller.openSong(1); });
  await m.editorReady();
  await m.typeInto(m.q('input[aria-label="Título de la canción"]'), "Título cambiado");
  await m.press("Escape", {}, win.document.body);
  assert.ok(m.q('[data-testid="leave-bar"]'), "aparece la barra «Hay cambios sin guardar»");
  assert.match(m.q('[data-testid="leave-bar"]').textContent, /Guardar y salir/);

  await m.click(m.buttonByText(/Seguir editando/));
  assert.equal(m.q('[data-testid="leave-bar"]'), null);
  assert.equal(m.q('input[aria-label="Título de la canción"]').value, "Título cambiado");

  await m.press("Escape", {}, win.document.body);
  await m.click(m.buttonByText(/Descartar los cambios/));
  await m.settle();
  assert.equal(m.controller.store.getState().mode, "list");
  assert.equal(m.backend.sent("song.save").length, 0);
  await m.unmount();
});

test("borrar desde la lista: la barra dice «usada en N servicios» y no borra hasta confirmar", async () => {
  const m = await mount({ usedIn: { 1: 2 } });
  await m.click(m.buttonByText(/^Borrar$/));
  await until(() => m.q('[data-testid="delete-ask"]'));
  assert.match(m.q('[data-testid="delete-ask"]').textContent, /usada en 2 servicio\(s\)/);
  assert.match(m.q('[data-testid="delete-ask"]').textContent, /Falta/);
  assert.equal(m.backend.sent("song.delete").length, 0);
  await m.click(m.buttonByText(/^Cancelar$/));
  assert.equal(m.q('[data-testid="delete-ask"]'), null);

  await m.click(m.buttonByText(/^Borrar$/));
  await until(() => m.q('[data-testid="delete-ask"]'));
  await m.click(m.buttonByText(/^Borrar la canción$/));
  await until(() => m.backend.sent("song.delete").length === 1);
  await until(() => m.controller.store.getState().list.length === 0);
  await m.unmount();
});

test("importar pegando texto: vista previa con secciones y orden, sin guardar; «Usar en el editor» lo lleva al editor", async () => {
  const m = await mount({ songs: [] });
  await m.click(m.buttonByText(/Importar un \.txt/));
  assert.ok(m.q('[aria-label="Importar texto"]'));
  await m.typeInto(m.q('input[aria-label="Título"]'), "Pegada");
  await m.typeInto(m.q('textarea[aria-label="Texto de la canción"]'), `[Verso 1]\n${line(1)}\n\n[Coro]\n${line(2)}\n\n[Coro]\n`);
  await until(() => m.controller.store.getState().importDraft?.parsed);
  await m.settle(60);
  const preview = m.q('[data-testid="import-preview"]').textContent;
  assert.match(preview, /etiquetas entre corchetes/);
  assert.match(preview, /2 sección\(es\)/);
  assert.match(preview, /Orden: Verso 1 → Coro → Coro/);
  assert.equal(m.backend.sent("song.save").length, 0);

  await m.click(m.buttonByText(/Usar en el editor/));
  await m.settle(80);
  assert.equal(m.controller.store.getState().mode, "editor");
  assert.equal(m.q('input[aria-label="Título de la canción"]').value, "Pegada");
  assert.match(m.q('[data-testid="dirty-badge"]').textContent, /SIN GUARDAR/);
  await m.unmount();
});

test("arrastrar y soltar un .txt sobre la pantalla: se lee, se muestra la codificación detectada y se puede cambiar", async () => {
  const m = await mount({ songs: [] });
  const latin = Uint8Array.from([...Buffer.from("[Verso 1]\nLumar"), 0xed, ...Buffer.from(" se"), 0xf1, ...Buffer.from("or")]);
  const file = { name: "viejo.txt", size: latin.length, arrayBuffer: async () => latin.buffer };
  const zone = m.q("div[class]");
  await act(async () => {
    const drop = new win.Event("drop", { bubbles: true, cancelable: true });
    drop.dataTransfer = { files: [file], types: ["Files"] };
    m.container.firstElementChild.firstElementChild.dispatchEvent(drop);
    await sleep(80);
  });
  assert.ok(zone);
  await until(() => m.controller.store.getState().importDraft?.encoding === "windows-1252");
  await m.settle(60);
  assert.match(m.q('[data-testid="file-info"]').textContent, /Windows-1252/);
  assert.match(m.text(), /no está en UTF-8/);
  assert.equal(m.q('select[aria-label="Codificación"]').value, "auto");
  await m.unmount();
});

test("un borrador sin guardar de una sesión anterior se ofrece y se puede recuperar o descartar", async () => {
  const storage = memoryDraftStorage();
  storage.set(JSON.stringify({ v: 1, songId: null, revision: null, fromImport: false, title: "Borrador previo", author: "", copyright: "", ccli: "", notes: "", background: null, text: "[Verso 1]\nuna linea", sections: [{ serverId: null, kind: "verse", label: "Verso 1", text: "una linea" }], order: [0] }));
  const m = await mount({ songs: [], storage });
  assert.match(m.q('[data-testid="recovered"]').textContent, /Borrador previo/);
  await m.click(m.buttonByText(/Recuperarlo/));
  await m.settle(120);
  assert.equal(m.controller.store.getState().mode, "editor");
  assert.equal(m.q('input[aria-label="Título de la canción"]').value, "Borrador previo");
  assert.equal(m.q('[data-testid="recovered"]'), null);
  await m.unmount();
});

test("los atajos globales (Ctrl+Alt+Shift+B/C/L/M/R) no activan nada en la pestaña y el panel no los consume", async () => {
  const m = await mount();
  await act(async () => { m.controller.openSong(1); });
  await m.editorReady();
  const before = JSON.stringify([m.controller.store.getState().editor.order, m.backend.commands.length]);
  for (const k of ["B", "C", "L", "M", "R", "b", "c", "l", "m", "r"]) {
    for (const target of [win, m.q("textarea"), m.qa("[data-order-row]")[0]]) {
      const ev = await m.press(k, { ctrlKey: true, altKey: true, shiftKey: true }, target);
      assert.equal(ev.defaultPrevented, false, `Ctrl+Alt+Shift+${k} no debe ser consumida por el panel`);
    }
  }
  assert.equal(JSON.stringify([m.controller.store.getState().editor.order, m.backend.commands.length]), before);
  await m.unmount();
});

test("tema de canciones: el mínimo del ajuste automático es SIEMPRE menor que el tamaño y se guarda por song.setSettings", async () => {
  const m = await mount();
  await m.click(m.buttonByText(/Tema de las canciones/));
  assert.match(m.text(), /Tema de las canciones/);
  assert.ok(m.q('[data-testid="slide-canvas"]'), "vista previa con el tema");

  const size = m.q('input[aria-label="Tamaño"]');
  for (const value of [30, 12, 8, 100, 400]) {
    await m.typeInto(size, String(value));
    const shown = Number(/(\d+) px/.exec(m.q('[data-testid="min-font"]').textContent)[1]);
    assert.ok(value === 8 ? shown <= value : shown < value, `tamaño ${value}: mínimo ${shown} debe ser menor`);
    assert.equal(shown, safeMinFontSize(value, 36 > value ? value : 36));
  }
  await m.typeInto(size, "40");
  await m.click(m.buttonByText(/Guardar el tema/));
  await until(() => m.backend.sent("song.setSettings").length === 1);
  const sent = m.backend.sent("song.setSettings")[0].payload;
  assert.equal(sent.theme.fontSize, 40);
  assert.ok(sent.theme.minFontSize < sent.theme.fontSize);
  assert.equal(sent.theme.autoFit, true);

  // Un color inválido bloquea el guardado en la propia pantalla.
  const shadow = m.qa("input").find((i) => i.value === "#000000CC");
  await m.typeInto(shadow, "rojo");
  assert.equal(m.buttonByText(/Guardar el tema/).disabled, true);
  assert.match(m.text(), /colores deben ser/);
  await m.click(m.buttonByText(/Volver a las canciones/));
  assert.equal(m.controller.store.getState().screen, "main");
  await m.unmount();
});

test("sin conexión con el programa los controles de escritura están desactivados", async () => {
  const m = await mount({ connected: false });
  assert.equal(m.q('input[type="search"]').disabled, true);
  assert.equal(m.buttonByText(/Canción nueva/).disabled, true);
  await m.unmount();
});

test("extremo a extremo: canción nueva escrita desde cero → se guarda → se reabre desde la lista con las mismas secciones y el mismo orden", async () => {
  const m = await mount({ songs: [] });
  await m.press("n", { ctrlKey: true }, win.document.body);
  await until(() => m.controller.store.getState().mode === "editor");
  await m.typeInto(m.q('input[aria-label="Título de la canción"]'), "Extremo a extremo");
  await m.typeInto(m.q('textarea[aria-label="Texto de la canción"]'), `[Verso 1]\n${body(1, 3)}\n\n[Coro]\n${body(2, 2)}\n\n${body(5, 1)}`);
  await m.settle(150);
  const buttons = m.container.querySelectorAll('[aria-label="Añadir una sección al orden"] button');
  assert.equal(buttons.length, 2);
  await m.click(buttons[1]);   // el coro otra vez
  await m.press("s", { ctrlKey: true }, m.q("textarea"));
  await until(() => m.backend.sent("song.save").length === 1);
  await until(() => m.controller.store.getState().editor?.songId === 1);
  const p = m.backend.sent("song.save")[0].payload;
  assert.deepEqual([p.id, p.title, p.sections.map((s) => s.label), p.arrangement], [null, "Extremo a extremo", ["Verso 1", "Coro"], [0, 1, 1]]);
  assert.ok(p.sections[1].text.includes("\n\n"), "el corte manual (línea en blanco) llega al backend");

  await m.press("Escape", {}, win.document.body);
  await m.settle(60);
  assert.equal(m.controller.store.getState().mode, "list");
  await m.click(m.buttonByText(/^Editar$/));
  await m.editorReady();
  const e = m.controller.store.getState().editor;
  assert.deepEqual(e.order.map((o) => o.label), ["Verso 1", "Coro", "Coro"]);
  assert.equal(isDirty(e), false);
  // Las diapositivas: el coro tiene un corte MANUAL (línea en blanco) y se repite.
  assert.ok(e.slides.some((s) => s.boundary === "manual"));
  await m.unmount();
});

// ───── proyección desde la lista ─────
const { OutputScreen } = await loadTs("/src/output/OutputScreen.tsx");

test("PROYECCIÓN: clic en la canción muestra su tira; Enter proyecta; la insignia EN VIVO sale solo de lo que confirma el backend", async () => {
  const m = await mount();
  try {
    await m.click(m.q('[role="option"]'));
    await until(() => m.controller.store.getState().projection?.slides);
    await m.settle(60);
    assert.equal(m.qa('[aria-label="Diapositivas derivadas"] li').length, 5, "una miniatura por diapositiva, en el orden de reproducción (V1 C V2 C C)");
    assert.equal(m.q('[data-testid="live-badge"]').textContent, "LISTO");
    assert.equal(m.backend.sent("output.showContent").length, 0, "elegir la canción no proyecta");

    const event = await m.press("Enter", {}, win.document.body);
    assert.equal(event.defaultPrevented, true);
    await until(() => m.backend.sent("output.showContent").length === 1);
    await m.settle(40);
    assert.equal(m.q('[data-testid="live-badge"]').textContent, "LISTO", "el panel envió, pero el backend todavía no confirmó: NO es «en vivo» (H5)");

    const sent = m.backend.sent("output.showContent")[0].payload;
    await m.confirm({ blocks: sent.blocks });
    assert.equal(m.q('[data-testid="live-badge"]').textContent, "EN VIVO");
    assert.equal(m.qa('li[data-live="true"]').length, 1);

    await m.confirm({ blocks: sent.blocks, master: "black" });
    assert.match(m.q('[data-testid="live-badge"]').textContent, /OCULTO · Negro/, "un control maestro oculta lo proyectado y la insignia lo dice");
    await m.confirm({ blocks: sent.blocks, master: "live" });
    assert.equal(m.q('[data-testid="live-badge"]').textContent, "EN VIVO", "volver a Live (atajo global) lo deja EN VIVO otra vez");

    await m.confirm({ blocks: ["otro texto que ya estaba"] });
    assert.equal(m.q('[data-testid="live-badge"]').textContent, "LISTO");
    assert.equal(m.qa('li[data-live="true"]').length, 0);
  } finally {
    await m.unmount();
  }
});

test("PROYECCIÓN: → ← Av Pág Re Pág navegan, el final marca FIN sin vaciar la pantalla, Esc quita el texto; el coro repetido marca SOLO la diapositiva pedida", async () => {
  const m = await mount();
  try {
    await m.click(m.q('[role="option"]'));
    await until(() => m.controller.store.getState().projection?.slides);
    await m.press("Enter", {}, win.document.body);
    const texts = () => m.backend.sent("output.showContent").map((c) => c.payload.blocks[0]);
    const slides = m.controller.store.getState().projection.slides.map((s) => s.text);
    await until(() => texts().length === 1);
    for (const key of ["ArrowRight", "PageDown", "ArrowRight"]) { await m.press(key, {}, win.document.body); await m.settle(30); }
    await until(() => texts().at(-1) === slides[3]);
    await m.press("ArrowLeft", {}, win.document.body); await m.settle(30);
    await m.press("PageUp", {}, win.document.body); await m.settle(30);
    await until(() => texts().at(-1) === slides[1]);

    // Va al final y pulsa de más.
    await m.press("ArrowRight", {}, win.document.body); await m.settle(30);
    await m.press("ArrowRight", {}, win.document.body); await m.settle(30);
    await m.press("ArrowRight", {}, win.document.body); await m.settle(30);
    await until(() => texts().at(-1) === slides[4]);
    const sent = texts().length;
    await m.confirm({ blocks: [slides[4]] });
    await m.press("ArrowRight", {}, win.document.body);
    await m.settle(60);
    assert.equal(texts().length, sent, "una pulsación de más no manda nada");
    assert.ok(m.q('[data-testid="end-marker"]'), "se ve el indicador de fin");
    assert.equal(m.qa('li[data-live="true"]').length, 1, "el coro se repite 3 veces con el mismo texto y solo la pedida está marcada");
    assert.equal(m.qa("li")[4].dataset.live, "true");

    await m.press("Escape", {}, win.document.body);
    await until(() => m.backend.sent("output.clearContent").length === 1);
    await m.confirm({ blocks: null });
    assert.equal(m.q('[data-testid="live-badge"]').textContent, "LISTO");
    assert.equal(m.q('[data-testid="end-marker"]'), null);
  } finally {
    await m.unmount();
  }
});

test("EXTREMO A EXTREMO: una canción sintética elegida en la lista y proyectada con Enter llega a la salida real (OutputScreen) con su letra y su tema", async () => {
  const m = await mount();
  try {
    await m.click(m.q('[role="option"]'));
    await until(() => m.controller.store.getState().projection?.slides);
    await m.press("Enter", {}, win.document.body);
    await m.press("ArrowRight", {}, win.document.body);
    await until(() => m.backend.sent("output.showContent").length === 2);
    const sent = m.backend.sent("output.showContent")[1].payload;
    const state = {
      schemaVersion: 3, revision: 0, master: "live",
      background: { kind: "color", color: "#102030", mediaUrl: null, fileName: null, muted: true, transitionMs: 0, warning: null },
      content: { slide: { id: "e2e", blocks: sent.blocks.map((text) => ({ text })), theme: sent.theme }, transitionMs: sent.transitionMs },
      overlays: { alert: null },
    };
    const out = win.document.createElement("div");
    win.document.body.appendChild(out);
    const outRoot = createRoot(out, { onCaughtError: () => undefined });
    await act(async () => { outRoot.render(createElement(OutputScreen, { state, seq: 1 })); });
    await act(async () => { await sleep(80); });
    const shown = [...out.querySelectorAll(".content-text")].map((n) => n.textContent).join("|");
    assert.equal(sent.blocks.length, 1, "una diapositiva = un bloque");
    assert.ok(shown.includes(line(2, 0)) && shown.includes(line(2, 1)), "se ve la letra del coro (sintética)");
    assert.equal(sent.theme.fontSize, 60, "el tema de canciones");
    await act(async () => { outRoot.unmount(); });
  } finally {
    await m.unmount();
  }
});