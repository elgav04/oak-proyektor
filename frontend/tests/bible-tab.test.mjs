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
const { BibleSettingsView } = await loadTs("/src/control/bible/BibleSettings.tsx");
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
  let output = null;
  const paint = async () => { await act(async () => { root.render(createElement(BibleTab, { controller, connected: true, output, onOpenSettings: () => undefined })); }); };
  await paint();
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
  /** Simula el snapshot que el backend CONFIRMA (`output.stateChanged`): la insignia sale de aquí, no de lo que el panel envió. */
  const confirm = async ({ blocks, master = "live", isOpen = true, isPaused = false } = {}) => {
    const slide = blocks ? { id: "s1", blocks: blocks.map((text) => ({ text })), theme: {} } : null;
    output = { seq: (output?.seq ?? 0) + 1, state: { master, content: { slide, transitionMs: 280 } }, window: { isOpen, isPaused } };
    await paint();
  };
  const badge = () => q('[data-testid="live-badge"]').textContent;
  return { backend, controller, container, root, q, input, press, type, settle, selectedVerses, confirm, badge, unmount: async () => { await act(async () => { root.unmount(); }); controller.dispose(); } };
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
  assert.equal(m.badge(), "LISTO", "el panel NO da por hecho que está en pantalla: espera la confirmación del backend");
  await m.confirm({ blocks: m.backend.lastContent().blocks });
  assert.equal(m.badge(), "EN VIVO");

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

// ───── H5: la insignia sale del estado REAL que confirma el backend ─────

async function projectOne(m, text = "alfa 1:2") {
  await m.type(text);
  await m.settle();
  await until(() => m.controller.store.getState().built !== null);
  await m.press("Enter", {}, m.input());
  await until(() => m.backend.sent("output.showContent").length === 1);
  return m.backend.lastContent().blocks;
}

test("H5: tras un output.invalidContent (el backend rechaza y el estado no cambia) la insignia NO dice EN VIVO", async () => {
  const m = await mount();
  await projectOne(m);                       // el panel envió el comando...
  await m.confirm({ blocks: ["otro texto que ya estaba"] });   // ...pero el backend lo rechazó: el estado sigue con lo anterior
  assert.equal(m.badge(), "LISTO");
  await m.confirm({ blocks: null });         // o no había nada
  assert.equal(m.badge(), "LISTO");
  await m.unmount();
});

test("H5: Negro, Limpiar texto, Logo y Congelar (también por los atajos globales, que cambian el modo maestro en C#) dejan de decir EN VIVO y dicen por qué", async () => {
  const m = await mount();
  const blocks = await projectOne(m);
  await m.confirm({ blocks });
  assert.equal(m.badge(), "EN VIVO");
  for (const [master, why] of [["black", "Negro"], ["clearText", "Texto limpio"], ["logo", "Logo"], ["freeze", "Congelado"]]) {
    await m.confirm({ blocks, master });
    assert.equal(m.badge(), `OCULTO · ${why}`, master);
  }
  await m.confirm({ blocks, master: "live" });   // pulsar el atajo otra vez vuelve a Live
  assert.equal(m.badge(), "EN VIVO");
  await m.unmount();
});

test("H5: salida en pausa o cerrada: no es EN VIVO aunque el slide sea el actual", async () => {
  const m = await mount();
  const blocks = await projectOne(m);
  await m.confirm({ blocks, isPaused: true });
  assert.equal(m.badge(), "OCULTO · Salida en pausa");
  await m.confirm({ blocks, isOpen: false });
  assert.equal(m.badge(), "OCULTO · Salida cerrada");
  await m.confirm({ blocks });
  assert.equal(m.badge(), "EN VIVO");
  await m.unmount();
});

test("H5: texto enviado desde Diagnóstico o quitado con clearContent: la insignia vuelve a LISTO", async () => {
  const m = await mount();
  const blocks = await projectOne(m);
  await m.confirm({ blocks });
  assert.equal(m.badge(), "EN VIVO");
  await m.confirm({ blocks: ["Texto de prueba de Diagnóstico", "segundo bloque"] });   // otro slide sustituyó al del versículo
  assert.equal(m.badge(), "LISTO");
  await m.confirm({ blocks });
  await m.confirm({ blocks: null });                                                      // output.clearContent: no queda slide
  assert.equal(m.badge(), "LISTO");
  await m.unmount();
});

test("H5: otra página del mismo pasaje no cuenta como EN VIVO (se compara el contenido exacto de la página)", async () => {
  const m = await mount({ cap: 200, backend: { longVerses: new Set(["1:1:3"]) } });
  await m.type("alfa 1:3");
  await m.settle();
  await until(() => m.controller.store.getState().built !== null);
  await m.press("Enter", {}, m.input());
  await until(() => m.backend.sent("output.showContent").length === 1);
  const first = m.backend.lastContent().blocks;
  await m.confirm({ blocks: first });
  assert.equal(m.badge(), "EN VIVO");
  await m.press("ArrowRight");                           // página 2: se proyecta y el backend aún no lo confirmó
  await until(() => m.backend.sent("output.showContent").length === 2);
  assert.equal(m.badge(), "LISTO");
  await m.confirm({ blocks: m.backend.lastContent().blocks });
  assert.equal(m.badge(), "EN VIVO");
  await m.unmount();
});

test("H5: bibleLiveStatus (función pura) sin estado o sin página es LISTO", async () => {
  const { bibleLiveStatus } = await loadTs("/src/control/bible/liveStatus.ts");
  assert.deepEqual(bibleLiveStatus(null, ["x"]), { kind: "ready" });
  assert.deepEqual(bibleLiveStatus({ state: { master: "live", content: { slide: { blocks: [{ text: "x" }] } } }, window: { isOpen: true, isPaused: false } }, null), { kind: "ready" });
  assert.deepEqual(bibleLiveStatus({ state: { master: "live", content: { slide: { blocks: [{ text: "x" }] } } }, window: { isOpen: true, isPaused: false } }, ["x"]), { kind: "live" });
  assert.deepEqual(bibleLiveStatus({ state: { master: "live", content: { slide: { blocks: [{ text: "x" }] } } }, window: { isOpen: true, isPaused: false } }, ["x", "y"]), { kind: "ready" });
});

// ───── H12: casillas y selector de Biblia ─────

test("H12: tras pulsar la casilla «Proyectar al navegar» los atajos siguen funcionando (↓, Enter y «/»)", async () => {
  const m = await mount();
  await m.type("alfa 1:2");
  await m.settle();
  await until(() => m.controller.store.getState().built !== null);
  const box = m.q('input[type="checkbox"]');
  await act(async () => { box.focus(); box.click(); await sleep(20); });
  assert.equal(win.document.activeElement, box, "el foco se queda en la casilla (es lo que hace el navegador al pulsarla)");
  const before = m.controller.store.getState().active;
  await m.press("ArrowDown", {}, box);
  assert.equal(m.controller.store.getState().active, before + 1, "↓ mueve el versículo aunque el foco esté en la casilla");
  await m.press("Enter", {}, box);
  await until(() => m.backend.sent("output.showContent").length >= 1);
  await m.press("/", {}, box);
  assert.equal(win.document.activeElement, m.input(), "«/» lleva el foco a la búsqueda");
  await m.unmount();
});

test("H12: al elegir en el selector de Biblia el foco sale del selector (↓ ya no cambia de Biblia)", async () => {
  const m = await mount();
  const select = m.q("select");
  await act(async () => { select.focus(); });
  assert.equal(win.document.activeElement, select);
  await act(async () => { select.value = "SIN"; select.dispatchEvent(new win.Event("change", { bubbles: true })); await sleep(10); });
  assert.notEqual(win.document.activeElement, select, "el selector soltó el foco tras elegir");
  await m.unmount();
});

test("H12: con el foco en el selector (sin haber elegido) los atajos no se interpretan: las flechas son del selector", async () => {
  const m = await mount();
  await m.type("alfa 1:2");
  await m.settle();
  await until(() => m.controller.store.getState().built !== null);
  const select = m.q("select");
  const before = m.controller.store.getState().active;
  await act(async () => { select.focus(); });
  await m.press("ArrowDown", {}, select);
  assert.equal(m.controller.store.getState().active, before);
  await m.unmount();
});

// ───── H11: guardar la configuración no pisa lo cambiado después de abrirla ─────

test("H11: guardar la pantalla de configuración conserva el cambio hecho FUERA después de abrirla («Proyectar al navegar») y aplica solo lo cambiado aquí", async () => {
  const backend = createFakeBackend();
  const controller = createBibleController({ api: backend.client, createFit: fakeFit(400), searchDebounceMs: 5, recordDelayMs: 25, sendIntervalMs: 20 });
  await controller.init();
  const container = win.document.createElement("div");
  win.document.body.appendChild(container);
  const root = createRoot(container);
  await act(async () => { root.render(createElement(BibleSettingsView, { controller, connected: true, onClose: () => undefined })); });

  // 1. fuera de esta pantalla (la casilla de la pestaña) se activa «proyectar al navegar» y se guarda
  assert.equal(controller.store.getState().settings.projectOnNavigate, false);
  await act(async () => { await controller.saveSettings({ ...controller.store.getState().settings, projectOnNavigate: true }); });
  assert.equal(backend.state.settings.projectOnNavigate, true);

  // 2. en la pantalla (cuyo borrador se tomó al abrirla, con la casilla en false) se cambia SOLO el crossfade
  const input = [...container.querySelectorAll('input[type="number"]')].find((el) => el.value === "280");
  assert.ok(input, "campo del crossfade");
  await act(async () => {
    const set = Object.getOwnPropertyDescriptor(win.HTMLInputElement.prototype, "value").set;
    set.call(input, "400");
    input.dispatchEvent(new win.Event("input", { bubbles: true }));
  });
  const save = [...container.querySelectorAll("button")].find((b) => b.textContent.includes("Guardar configuración"));
  await act(async () => { save.click(); await sleep(40); });

  const saved = backend.sent("bible.setSettings").at(-1).payload;
  assert.equal(saved.transitionMs, 400, "lo cambiado aquí se guarda");
  assert.equal(saved.projectOnNavigate, true, "lo cambiado fuera NO se pisa con el borrador viejo");
  assert.equal(backend.state.settings.projectOnNavigate, true);
  await act(async () => { root.unmount(); });
  controller.dispose();
});

test("H11: mergeSettings aplica solo lo que cambió el operador; el resto conserva el valor actual (tema, nombres cortos y opciones)", async () => {
  const { mergeSettings } = await loadTs("/src/control/bible/settingsMerge.ts");
  const base = {
    defaultBible: null, projectOnNavigate: false, superscriptNumbers: true, showAttribution: false, transitionMs: 280,
    theme: { fontFamily: "Arial", fontSize: 64, color: "#FFFFFF", outlineWidth: 2, outlineColor: "#000000", shadowBlur: 8, shadowOffset: 3, shadowColor: "#000000CC", align: "center", verticalAlign: "middle", lineHeight: 1.3, marginTop: 70, marginRight: 120, marginBottom: 70, marginLeft: 120, autoFit: true, minFontSize: 40 },
    shortNames: [{ bible: "AAA", shortName: "A" }],
  };
  // sin cambios en el borrador: se queda con lo ACTUAL tal cual
  const current = { ...base, projectOnNavigate: true, transitionMs: 500, theme: { ...base.theme, fontSize: 80 }, shortNames: [{ bible: "AAA", shortName: "Z" }, { bible: "BBB", shortName: "B" }] };
  assert.deepEqual(mergeSettings(base, base, current), current);
  // el operador cambió el color y el nombre corto de AAA y quitó el de otra: solo eso se aplica
  const draft = { ...base, theme: { ...base.theme, color: "#FFFF00" }, shortNames: [{ bible: "AAA", shortName: "Mi nombre" }] };
  const merged = mergeSettings(base, draft, current);
  assert.equal(merged.theme.color, "#FFFF00");
  assert.equal(merged.theme.fontSize, 80, "el tamaño que cambió fuera se conserva");
  assert.equal(merged.projectOnNavigate, true);
  assert.equal(merged.transitionMs, 500);
  assert.deepEqual(merged.shortNames.find((n) => n.bible === "AAA"), { bible: "AAA", shortName: "Mi nombre" });
  assert.deepEqual(merged.shortNames.find((n) => n.bible === "BBB"), { bible: "BBB", shortName: "B" }, "otra Biblia cambiada fuera se conserva");
  // borrar un nombre corto en la pantalla lo borra también del resultado
  const cleared = mergeSettings(base, { ...base, shortNames: [] }, current);
  assert.equal(cleared.shortNames.find((n) => n.bible === "AAA"), undefined);
  // la Biblia por defecto
  assert.equal(mergeSettings(base, { ...base, defaultBible: "AAA" }, current).defaultBible, "AAA");
  assert.equal(mergeSettings(base, base, { ...current, defaultBible: "CCC" }).defaultBible, "CCC");
});