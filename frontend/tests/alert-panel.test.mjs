import assert from "node:assert/strict";
import test, { after } from "node:test";
import { ATHEME, createAlertBackend, simulatedHeight } from "./alert-fixtures.mjs";
import { setupDom, sleep } from "./dom.mjs";
import { closeServer, loadTs } from "./helper.mjs";

// Barra de alertas del panel con DOM simulado (happy-dom NO mide: la medición es SIMULADA), backend falso con el protocolo real, cliente IPC real y controlador real.
// Todo con avisos inventados («AVISO DE PRUEBA …»).
const win = setupDom();
for (const name of ["KeyboardEvent", "MouseEvent", "InputEvent", "HTMLInputElement", "HTMLSelectElement", "HTMLButtonElement", "HTMLLIElement"]) {
  if (win[name]) globalThis[name] = win[name];
}
const { act, createElement } = await import("react");
const { createRoot } = await import("react-dom/client");
const model = await loadTs("/src/control/alerts/model.ts");
const shortcuts = await loadTs("/src/control/alerts/alertShortcuts.ts");
const { createAlertController } = await loadTs("/src/control/alerts/controller.ts");
const { AlertBar } = await loadTs("/src/control/alerts/AlertBar.tsx");
const { OutputScreen } = await loadTs("/src/output/OutputScreen.tsx");
const { RESERVED_GLOBAL_SHORTCUTS, BIBLE_SHORTCUTS } = await loadTs("/src/control/shortcuts.ts");
after(closeServer);

const simulatedMeasurer = { fit: (text, theme) => model.evaluateFit(theme, simulatedHeight(text)), dispose() {} };
const NOW = Date.parse("2026-11-29T10:00:00.000Z");
const outputOf = (alert, extra = {}) => ({ seq: 1, state: { schemaVersion: 3, revision: 0, master: "live", background: { kind: "none", color: "#000000" }, content: { slide: null, transitionMs: 0 }, overlays: { alert }, ...(extra.state ?? {}) }, window: { isOpen: true, isPaused: false, ...(extra.window ?? {}) } });
const alertOf = (over = {}) => ({ id: "a1", text: "AVISO DE PRUEBA 1", mode: "band", theme: ATHEME, sticky: false, expiresAt: "2026-11-29T10:00:30.000Z", ...over });
const memoryStorage = () => { let v = null; return { get: () => v, set: (x) => { v = x; }, peek: () => v }; };

// ───────────────────────── modelo puro ─────────────────────────

test("la insignia se DERIVA del estado confirmado: en pantalla, oculta por el modo maestro o la salida, y Freeze NO la oculta", () => {
  assert.deepEqual(model.alertLiveStatus(null), { kind: "none" });
  assert.deepEqual(model.alertLiveStatus(outputOf(null)), { kind: "none" });
  assert.equal(model.alertLiveStatus(outputOf(alertOf())).kind, "live");
  for (const [master, why] of [["black", "Negro"], ["clearText", "Texto limpio"], ["logo", "Logo"]]) {
    const s = model.alertLiveStatus(outputOf(alertOf(), { state: { master } }));
    assert.deepEqual([s.kind, s.why], ["hidden", why]);
  }
  assert.equal(model.alertLiveStatus(outputOf(alertOf(), { state: { master: "freeze" } })).kind, "live", "la alerta no se congela");
  assert.equal(model.alertLiveStatus(outputOf(alertOf(), { window: { isOpen: false } })).why, "Salida cerrada");
  assert.equal(model.alertLiveStatus(outputOf(alertOf(), { window: { isPaused: true } })).why, "Salida en pausa");
});

test("cuenta atrás: segundos restantes (nunca negativos), formato m:ss y antigüedad de las guardadas", () => {
  assert.equal(model.remainingSeconds({ expiresAt: "2026-11-29T10:00:30.000Z" }, NOW), 30);
  assert.equal(model.remainingSeconds({ expiresAt: "2026-11-29T10:00:30.000Z" }, NOW + 29_500), 1);
  assert.equal(model.remainingSeconds({ expiresAt: "2026-11-29T10:00:30.000Z" }, NOW + 99_000), 0);
  assert.equal(model.remainingSeconds({ expiresAt: "no es una fecha" }, NOW), null);
  assert.equal(model.formatRemaining(23), "0:23");
  assert.equal(model.formatRemaining(725), "12:05");
  assert.equal(model.ageLabel(null, NOW), "nunca mostrada");
  assert.equal(model.ageLabel("2026-11-29T09:59:30.000Z", NOW), "hace un momento");
  assert.equal(model.ageLabel("2026-11-29T09:30:00.000Z", NOW), "hace 30 min");
  assert.equal(model.ageLabel("2026-11-29T05:00:00.000Z", NOW), "hace 5 h");
  assert.equal(model.ageLabel("2026-11-26T10:00:00.000Z", NOW), "hace 3 d");
});

test("medición: cabe, cabe reduciendo la letra, no cabe (la cinta crecería) o no cabe ni creciendo; el desplazamiento se sugiere en vez de cortar", () => {
  const fit = (n, theme = ATHEME) => model.evaluateFit(theme, simulatedHeight("x".repeat(n)));
  assert.deepEqual(fit(40), { state: "fits", fontSize: 52 });
  const shrunk = fit(140);
  assert.equal(shrunk.state, "shrunk");
  assert.ok(shrunk.fontSize < 52 && shrunk.fontSize >= 31);
  assert.deepEqual(fit(300), { state: "grows", fontSize: 31 });   // 3 renglones al mínimo: no cabe en 2, pero la cinta crece sin pasar de su tope
  assert.equal(fit(140, { ...ATHEME, autoFit: false }).state, "grows", "sin ajuste automático no se reduce la letra: la cinta crece");
  assert.equal(fit(300, { ...ATHEME, autoFit: false }).state, "blocked", "y si ni creciendo cabe, se bloquea");
  assert.equal(fit(300, { ...ATHEME, paddingY: 120 }).state, "blocked", "con tanto relleno ni creciendo cabe: se bloquea (nunca se corta)");
  assert.match(model.fitMessage(fit(300), ATHEME), /Usa el desplazamiento/);
  assert.match(model.fitMessage(fit(140), ATHEME), /reduciendo la letra a \d+ px/);
  assert.equal(model.fitMessage(fit(40), ATHEME), null);
});

test("el mínimo del ajuste es SIEMPRE menor que el tamaño, también al cambiar el tamaño, y la validación pantalla rechaza lo evidentemente inválido", () => {
  for (const size of [16, 20, 40, 52, 100, 200]) {
    for (const min of [1, 12, 31, 52, 500]) assert.ok(model.alertMinFontSize(size, min) < size, `size ${size} min ${min}`);
  }
  assert.equal(model.themeWithAlertFontSize(ATHEME, 100).minFontSize, 31, "se conserva el mínimo si sigue siendo menor");
  assert.equal(model.themeWithAlertFontSize(ATHEME, 20).minFontSize, 12, "si el tamaño baja del mínimo, se propone el 60 % (con suelo de 12) y queda por debajo");
  assert.equal(model.themeWithAlertFontSize({ ...ATHEME, minFontSize: 50 }, 30).minFontSize, 18);
  const ok = { mode: "band", durationSec: 30, theme: ATHEME, tickerEnabled: true };
  assert.equal(model.alertSettingsProblem(ok), null);
  assert.ok(model.alertSettingsProblem({ ...ok, theme: { ...ATHEME, bandColor: "negro" } }));
  assert.ok(model.alertSettingsProblem({ ...ok, theme: { ...ATHEME, minFontSize: 52 } }), "mínimo igual al tamaño");
  assert.ok(model.alertSettingsProblem({ ...ok, theme: { ...ATHEME, maxLines: 4 } }));
  assert.ok(model.alertSettingsProblem({ ...ok, durationSec: 3 }));
  assert.ok(model.alertSettingsProblem({ ...ok, mode: "ticker", tickerEnabled: false }));
});

// ───────────────────────── atajos configurables ─────────────────────────

const key = (k, mods = {}) => ({ key: k, ctrlKey: false, altKey: false, metaKey: false, shiftKey: false, ...mods });

test("atajos: por defecto Ctrl+Mayús+K y Ctrl+Mayús+H; coinciden con modificadores EXACTOS y en mayúscula o minúscula (teclado español)", () => {
  const b = shortcuts.DEFAULT_ALERT_SHORTCUTS;
  assert.deepEqual(b, { focus: "Ctrl+Shift+K", hide: "Ctrl+Shift+H" });
  assert.equal(shortcuts.matchAlertShortcut(key("K", { ctrlKey: true, shiftKey: true }), b), "focus");
  assert.equal(shortcuts.matchAlertShortcut(key("k", { ctrlKey: true, shiftKey: true }), b), "focus");
  assert.equal(shortcuts.matchAlertShortcut(key("H", { ctrlKey: true, shiftKey: true }), b), "hide");
  assert.equal(shortcuts.matchAlertShortcut(key("K", { ctrlKey: true }), b), null, "sin Mayús");
  assert.equal(shortcuts.matchAlertShortcut(key("K", { shiftKey: true }), b), null, "sin Ctrl");
  assert.equal(shortcuts.matchAlertShortcut(key("K", { ctrlKey: true, shiftKey: true, altKey: true }), b), null, "Ctrl+Alt+Mayús+K es de los globales: nunca");
  assert.equal(shortcuts.matchAlertShortcut(key("H", { ctrlKey: true, shiftKey: true, metaKey: true }), b), null);
});

test("atajos: no pueden pisar los globales (M, B, C, L, R, A), los del panel (Biblia, Canciones, Lista), los reservados del navegador ni repetirse", () => {
  for (const g of RESERVED_GLOBAL_SHORTCUTS) assert.equal(shortcuts.parseCombo(g), null, `${g} no es una combinación admitida (lleva Alt)`);
  assert.ok(RESERVED_GLOBAL_SHORTCUTS.includes("Ctrl+Alt+Shift+A"));
  // Los atajos de las alertas no coinciden con ninguno del mapa de la Biblia.
  for (const s of BIBLE_SHORTCUTS) {
    const hit = shortcuts.matchAlertShortcut({ key: s.key, ctrlKey: s.ctrl === true, shiftKey: s.shift === true, altKey: false, metaKey: false }, shortcuts.DEFAULT_ALERT_SHORTCUTS);
    assert.equal(hit, null, `choca con ${s.label}`);
  }
  assert.match(shortcuts.validateCombo("Ctrl+Shift+A"), /panel/);
  assert.match(shortcuts.validateCombo("Ctrl+Shift+Q"), /navegador/);
  assert.match(shortcuts.validateCombo("Ctrl+Alt+Shift+M"), /Alt/);
  assert.match(shortcuts.validateCombo("Ctrl+K"), /Mayús/);
  assert.match(shortcuts.validateCombo("Shift+K"), /Ctrl/);
  assert.match(shortcuts.validateCombo("Ctrl+Shift+H", ["Ctrl+Shift+H"]), /otra acción/);
  assert.equal(shortcuts.validateCombo("Ctrl+Shift+Y"), null);
});

test("atajos configurables: se guardan, se recuperan y un valor guardado inválido o repetido vuelve al de fábrica", () => {
  const storage = memoryStorage();
  const b = createAlertBackend();
  const c = createAlertController({ api: b.client, shortcutStorage: storage });
  assert.equal(c.setShortcut("focus", "Ctrl+Shift+Y"), null);
  assert.equal(c.store.getState().shortcuts.focus, "Ctrl+Shift+Y");
  assert.match(c.setShortcut("hide", "Ctrl+Shift+Y"), /otra acción/, "no se puede repetir");
  assert.match(c.setShortcut("hide", "Ctrl+Shift+Q"), /navegador/);
  assert.equal(c.store.getState().shortcuts.hide, "Ctrl+Shift+H", "lo rechazado no cambia nada");
  c.dispose();

  const again = createAlertController({ api: b.client, shortcutStorage: storage });
  assert.equal(again.store.getState().shortcuts.focus, "Ctrl+Shift+Y", "se recuperó tras «recargar»");
  again.resetShortcuts();
  assert.deepEqual(again.store.getState().shortcuts, shortcuts.DEFAULT_ALERT_SHORTCUTS);
  again.dispose();

  const corrupt = { get: () => JSON.stringify({ focus: "Ctrl+Alt+Shift+M", hide: "Ctrl+Shift+Y" }), set() {} };
  const fixed = shortcuts.loadBindings(corrupt);
  assert.deepEqual(fixed, { focus: "Ctrl+Shift+K", hide: "Ctrl+Shift+Y" }, "el inválido vuelve a su valor; el válido se conserva");
  assert.deepEqual(shortcuts.loadBindings({ get: () => "no es json", set() {} }), shortcuts.DEFAULT_ALERT_SHORTCUTS);
});

// ───────────────────────── controlador ─────────────────────────

test("controlador: init lee ajustes y lista; escribir NO manda nada; cargar una fila NO muestra; mostrar manda alert.show con el modo y la duración elegidos", async () => {
  const b = createAlertBackend();
  b.seed("AVISO FIJO DE PRUEBA", { pinned: true });
  b.seed("AVISO RECIENTE DE PRUEBA", { daysAgo: 1 });
  const c = createAlertController({ api: b.client, measurer: simulatedMeasurer, listRefreshDelayMs: 10 });
  await c.init();
  assert.equal(c.store.getState().settings.durationSec, 30);
  assert.deepEqual(c.store.getState().pinned.map((a) => a.text), ["AVISO FIJO DE PRUEBA"]);
  assert.deepEqual(c.store.getState().recent.map((a) => a.text), ["AVISO RECIENTE DE PRUEBA"]);

  c.setDraft("AVISO DE PRUEBA 1");
  c.setDuration(60);
  await sleep(20);
  assert.equal(b.commands.length, 0, "escribir y elegir no manda nada");

  c.loadFrom(c.store.getState().pinned[0]);
  assert.equal(c.store.getState().draft, "AVISO FIJO DE PRUEBA");
  await sleep(20);
  assert.equal(b.sent("alert.show").length, 0, "cargar una fila NO la muestra");

  assert.equal(c.show(), true);
  await sleep(20);
  assert.deepEqual(b.sent("alert.show").map((x) => x.payload), [{ text: "AVISO FIJO DE PRUEBA", mode: "band", durationSec: 60 }]);
  c.dispose();
});

test("controlador: no manda lo que no se puede mostrar (vacío, más de 300, desplazamiento desactivado, no cabe ni creciendo) y dice por qué", async () => {
  const b = createAlertBackend({ tickerEnabled: false });
  const c = createAlertController({ api: b.client, measurer: simulatedMeasurer });
  await c.init();
  assert.match(c.showProblem(), /Escribe/);
  c.setDraft("x".repeat(301));
  assert.match(c.showProblem(), /300/);
  assert.equal(c.show(), false);
  c.setDraft("AVISO DE PRUEBA");
  assert.equal(c.showProblem(), null);
  c.setMode("ticker");
  assert.match(c.showProblem(), /desactivado/);
  assert.equal(c.show(), false);
  c.setMode("band");
  c.store.setState({ settings: { ...c.store.getState().settings, theme: { ...ATHEME, paddingY: 120 } } });
  c.setDraft("y".repeat(300));
  assert.equal(c.store.getState().fit.state, "blocked");
  assert.match(c.showProblem(), /No cabe/);
  assert.equal(b.commands.length, 0);
  c.dispose();
});

test("controlador: fijar, desfijar y borrar; el límite de 30 fijadas se respeta en el panel y se informa", async () => {
  const b = createAlertBackend();
  const rows = Array.from({ length: 30 }, (_, i) => b.seed(`AVISO FIJO ${String(i + 1).padStart(2, "0")}`, { pinned: true }));
  const loose = b.seed("AVISO SUELTO DE PRUEBA");
  const c = createAlertController({ api: b.client, listRefreshDelayMs: 10 });
  await c.init();
  assert.equal(c.store.getState().pinned.length, 30);

  c.pin(c.store.getState().recent[0], true);
  assert.match(c.store.getState().listError, /30/);
  assert.equal(b.sent("alert.save").length, 0, "ni siquiera se manda");

  c.pin(c.store.getState().pinned[0], false);
  await sleep(30);
  assert.equal(c.store.getState().pinned.length, 29);
  c.pin(c.store.getState().recent.find((a) => a.id === loose.id), true);
  await sleep(30);
  assert.equal(c.store.getState().pinned.length, 30);

  c.remove(c.store.getState().pinned[0]);
  await sleep(30);
  assert.equal(c.store.getState().pinned.length, 29);
  assert.equal(rows.length, 30);
  c.dispose();
});

test("controlador: nada se marca «en pantalla» al enviar; solo el estado que CONFIRMA el backend lo hace, y una alerta quitada desde fuera (atajo global) deja de verse", async () => {
  const b = createAlertBackend();
  const c = createAlertController({ api: b.client });
  await c.init();
  c.setDraft("AVISO DE PRUEBA 1");
  c.show();
  assert.equal(model.alertLiveStatus(b.output()).kind, "none", "recién enviado, todavía no confirmado");
  await sleep(10);
  assert.equal(model.alertLiveStatus(b.output()).kind, "live");
  b.externalHide();
  assert.equal(model.alertLiveStatus(b.output()).kind, "none", "quitada con Ctrl+Alt+Shift+A: el panel lo sabe por el estado, no por ninguna marca propia");
  // «Recargar el panel»: un controlador y una réplica NUEVOS recuperan la alerta del estado (output.getState) sin haber enviado nada.
  c.setDraft("AVISO DE PRUEBA 2"); c.show(); await sleep(10);
  const reloaded = createAlertController({ api: b.client });
  assert.equal(model.alertLiveStatus(b.output()).alert.text, "AVISO DE PRUEBA 2");
  assert.equal(reloaded.store.getState().draft, "");
  reloaded.dispose(); c.dispose();
});

// ───────────────────────── la barra (DOM simulado) ─────────────────────────

async function mountBar({ backend = createAlertBackend(), measurer = simulatedMeasurer, storage = null } = {}) {
  const controller = createAlertController({ api: backend.client, measurer, shortcutStorage: storage, listRefreshDelayMs: 15 });
  const container = win.document.createElement("div");
  win.document.body.appendChild(container);
  const root = createRoot(container);
  let connected = true;
  const render = async () => { await act(async () => { root.render(createElement(AlertBar, { controller, connected, output: backend.output() })); }); };
  await render();
  await act(async () => { await sleep(40); });
  await render();
  const q = (sel) => container.querySelector(sel);
  const qa = (sel) => [...container.querySelectorAll(sel)];
  const type = async (text) => {
    const input = q("#alert-input");
    await act(async () => {
      const setter = Object.getOwnPropertyDescriptor(win.HTMLInputElement.prototype, "value").set;
      setter.call(input, text);
      input.dispatchEvent(new win.Event("input", { bubbles: true }));
      await sleep(0);
    });
  };
  const press = async (k, opts = {}, target = win.document.body) => {
    let ev;
    await act(async () => { ev = new win.KeyboardEvent("keydown", { key: k, bubbles: true, cancelable: true, ...opts }); target.dispatchEvent(ev); await sleep(0); });
    return ev;
  };
  const click = async (el) => { await act(async () => { el.dispatchEvent(new win.MouseEvent("click", { bubbles: true, cancelable: true })); await sleep(0); }); };
  const settle = async (ms = 50) => { await act(async () => { await sleep(ms); }); await render(); };
  const buttonByText = (re) => qa("button").find((x) => re.test(x.textContent ?? ""));
  return { backend, controller, container, q, qa, type, press, click, settle, render, buttonByText, setConnected: (v) => { connected = v; }, unmount: async () => { await act(async () => { root.unmount(); }); controller.dispose(); container.remove(); } };
}

test("barra: contador n / 300, ▶ Mostrar y Enter muestran lo escrito, y tras Enter el foco sale del cuadro (las teclas de la pestaña siguen funcionando)", async () => {
  const m = await mountBar();
  assert.equal(m.q('[data-testid="alert-counter"]').textContent, "0 / 300");
  assert.equal(m.buttonByText(/Mostrar/).disabled, true, "sin texto no se puede mostrar");
  await m.type("AVISO DE PRUEBA 1");
  assert.equal(m.q('[data-testid="alert-counter"]').textContent, "17 / 300");
  assert.equal(m.backend.sent("alert.show").length, 0, "escribir no muestra");

  m.q("#alert-input").focus();
  assert.equal(win.document.activeElement, m.q("#alert-input"));
  const ev = await m.press("Enter", {}, m.q("#alert-input"));
  assert.equal(ev.defaultPrevented, true);
  await m.settle();
  assert.deepEqual(m.backend.sent("alert.show").map((c) => c.payload), [{ text: "AVISO DE PRUEBA 1", mode: "band", durationSec: 30 }]);
  assert.notEqual(win.document.activeElement, m.q("#alert-input"), "el foco volvió a la pestaña");

  await m.type("AVISO DE PRUEBA 2");
  await m.click(m.buttonByText(/Mostrar/));
  await m.settle();
  assert.equal(m.backend.sent("alert.show").length, 2, "el botón ▶ también muestra");
  await m.unmount();
});

test("barra: pasado el límite de 300 el contador se pone en rojo y NO se puede mostrar", async () => {
  const m = await mountBar();
  await m.type("z".repeat(301));
  assert.match(m.q('[data-testid="alert-counter"]').className, /red/);
  assert.equal(m.buttonByText(/Mostrar/).disabled, true);
  await m.press("Enter", {}, m.q("#alert-input"));
  assert.equal(m.backend.sent("alert.show").length, 0);
  await m.unmount();
});

test("barra: si no cabe en la cinta sugiere el desplazamiento (en vez de cortar) y un clic cambia de modo; si cabe reduciendo la letra lo dice", async () => {
  const m = await mountBar();
  await m.type("a".repeat(140));
  assert.match(m.q('[data-testid="alert-fit"]').textContent, /reduciendo la letra/);
  assert.equal(m.buttonByText(/Usar desplazamiento/), undefined, "reduciendo la letra no hace falta cambiar de modo");

  await m.type("b".repeat(300));
  assert.match(m.q('[data-testid="alert-fit"]').textContent, /Usa el desplazamiento/);
  await m.click(m.buttonByText(/Usar desplazamiento/));
  assert.equal(m.controller.store.getState().mode, "ticker");
  assert.equal(m.q('[data-testid="alert-fit"]'), null, "en desplazamiento no hay nada que medir");
  await m.press("Enter", {}, m.q("#alert-input"));
  await m.settle();
  assert.equal(m.backend.sent("alert.show")[0].payload.mode, "ticker");
  await m.unmount();
});

test("barra: con el desplazamiento desactivado en los ajustes no se ofrece ni se sugiere", async () => {
  const m = await mountBar({ backend: createAlertBackend({ tickerEnabled: false }) });
  const radios = m.qa('input[name="alert-mode"]');
  assert.equal(radios.find((r) => r.checked)?.nextSibling?.textContent ?? "Cinta", "Cinta");
  assert.equal(radios[1].disabled, true);
  await m.type("c".repeat(300));
  assert.equal(m.buttonByText(/Usar desplazamiento/), undefined);
  assert.match(m.q('[data-testid="alert-fit"]').textContent, /No cabe/);
  await m.unmount();
});

test("barra: indicación visible de que hay una alerta EN PANTALLA con el tiempo restante, derivada del estado confirmado; Quitar manda alert.hide", async () => {
  const m = await mountBar();
  assert.equal(m.q('[data-testid="alert-status"]'), null, "sin alerta no hay indicación");
  await m.type("AVISO DE PRUEBA 1");
  await m.press("Enter", {}, m.q("#alert-input"));
  await m.settle(80);
  const status = m.q('[data-testid="alert-status"]');
  assert.match(status.textContent, /ALERTA EN PANTALLA/);
  assert.match(m.q('[data-testid="alert-remaining"]').textContent, /Quedan · 0:(2\d|30)/);
  assert.match(m.q('[data-testid="alert-onscreen-text"]').textContent, /AVISO DE PRUEBA 1/);

  await m.type("AVISO DE PRUEBA 2");
  assert.match(m.q('[data-testid="alert-bar"]').textContent, /reemplazará/);

  await m.click(m.qa('[data-testid="alert-status"] button')[0]);
  assert.equal(m.backend.sent("alert.hide").length, 1);
  await m.settle();
  assert.equal(m.q('[data-testid="alert-status"]'), null, "la indicación desaparece cuando el backend confirma que ya no está");
  await m.unmount();
});

test("barra: oculta por Negro / Texto limpio / Logo / salida cerrada lo dice; Freeze no la oculta; se recupera sola tras recargar el panel (barra nueva sobre el mismo estado)", async () => {
  const b = createAlertBackend();
  const m = await mountBar({ backend: b });
  await m.type("AVISO DE PRUEBA 1");
  await m.press("Enter", {}, m.q("#alert-input"));
  await m.settle(60);
  for (const [master, re] of [["black", /OCULTA · Negro/], ["clearText", /OCULTA · Texto limpio/], ["logo", /OCULTA · Logo/], ["freeze", /EN PANTALLA/], ["live", /EN PANTALLA/]]) {
    b.setMaster(master); await m.settle(10);
    assert.match(m.q('[data-testid="alert-status"]').textContent, re, master);
  }
  b.setWindow({ isOpen: false }); await m.settle(10);
  assert.match(m.q('[data-testid="alert-status"]').textContent, /Salida cerrada/);
  b.setWindow({ isOpen: true }); await m.settle(10);
  await m.unmount();

  const reloaded = await mountBar({ backend: b });   // Ctrl+Alt+Shift+R: página nueva, controlador nuevo, nada enviado
  assert.match(reloaded.q('[data-testid="alert-status"]').textContent, /ALERTA EN PANTALLA/);
  assert.equal(reloaded.backend.sent("alert.show").length, 1, "la barra recién cargada no envió nada");
  await reloaded.unmount();
});

test("barra: la lista — un clic en una fila CARGA el texto y NO lo muestra; ▶ lo muestra; fijar/desfijar/borrar; antigüedad visible; límite de filas", async () => {
  const b = createAlertBackend();
  b.seed("AVISO FIJO DE PRUEBA", { pinned: true });
  b.seed("AVISO VIEJO DE PRUEBA", { daysAgo: 3 });
  b.seed("AVISO DEMASIADO VIEJO", { daysAgo: 20 });   // fuera de los 14 días: no se ofrece
  const m = await mountBar({ backend: b });
  await m.click(m.buttonByText(/▾ Alertas/));
  await m.settle(40);
  assert.equal(m.qa('[data-testid="alert-pinned"] [data-testid="alert-row"]').length, 1);
  const recentRows = m.qa('[data-testid="alert-recent"] [data-testid="alert-row"]');
  assert.equal(recentRows.length, 1);
  assert.match(recentRows[0].textContent, /hace 3 d/);

  await m.click(recentRows[0].querySelector("button"));   // clic en el texto
  assert.equal(m.q("#alert-input").value, "AVISO VIEJO DE PRUEBA");
  await m.settle(30);
  assert.equal(b.sent("alert.show").length, 0, "cargar NO muestra");

  await m.click(recentRows[0].querySelector('button[aria-label^="Mostrar ahora"]'));
  await m.settle(30);
  assert.deepEqual(b.sent("alert.show").map((c) => c.payload.text), ["AVISO VIEJO DE PRUEBA"]);

  await m.click(m.qa('[data-testid="alert-row"] button[aria-label^="Fijar"]')[0]);
  await m.settle(60);
  assert.equal(m.qa('[data-testid="alert-pinned"] [data-testid="alert-row"]').length, 2, "pasó a fijadas");
  await m.click(m.qa('[data-testid="alert-row"] button[aria-label^="Borrar"]')[0]);
  await m.settle(60);
  assert.equal(b.saved.size, 2);
  await m.unmount();
});

test("teclado: Ctrl+Mayús+K lleva el foco al cuadro desde cualquier sitio y Ctrl+Mayús+H quita la alerta, también con el foco escribiendo; con otros modificadores no hacen nada", async () => {
  const m = await mountBar();
  const other = win.document.createElement("input");
  win.document.body.appendChild(other);
  other.focus();
  const k = await m.press("K", { ctrlKey: true, shiftKey: true }, other);
  assert.equal(k.defaultPrevented, true);
  await m.settle(10);
  assert.equal(win.document.activeElement, m.q("#alert-input"), "el foco fue al cuadro de alertas");

  await m.type("AVISO DE PRUEBA 1");
  await m.press("Enter", {}, m.q("#alert-input"));
  await m.settle(40);
  assert.equal(m.backend.state.alert?.text, "AVISO DE PRUEBA 1");
  m.q("#alert-input").focus();
  await m.press("H", { ctrlKey: true, shiftKey: true }, m.q("#alert-input"));
  await m.settle(30);
  assert.equal(m.backend.sent("alert.hide").length, 1);
  assert.equal(m.backend.state.alert, null);

  // Con Alt (los globales) o sin Mayús no se interpreta nada.
  await m.press("H", { ctrlKey: true, shiftKey: true, altKey: true });
  await m.press("H", { ctrlKey: true });
  await m.press("K", { ctrlKey: true, altKey: true, shiftKey: true });
  assert.equal(m.backend.sent("alert.hide").length, 1);
  other.remove();
  await m.unmount();
});

test("teclado configurable: tras reasignar, vale el nuevo y deja de valer el viejo", async () => {
  const m = await mountBar({ storage: memoryStorage() });
  assert.equal(m.controller.setShortcut("hide", "Ctrl+Shift+Y"), null);
  await m.press("Y", { ctrlKey: true, shiftKey: true });
  await m.settle(20);
  assert.equal(m.backend.sent("alert.hide").length, 1);
  await m.press("H", { ctrlKey: true, shiftKey: true });
  assert.equal(m.backend.sent("alert.hide").length, 1, "el atajo viejo ya no hace nada");
  await m.unmount();
});

test("la barra sin conexión se desactiva (sin enviar nada)", async () => {
  const m = await mountBar();
  await m.type("AVISO DE PRUEBA 1");
  m.setConnected(false);
  await m.render();
  assert.equal(m.q("#alert-input").disabled, true);
  assert.equal(m.buttonByText(/Mostrar/).disabled, true);
  await m.unmount();
});

// ───────────────────────── extremo a extremo hasta la salida ─────────────────────────

test("extremo a extremo: escribir + Enter en la barra → el backend confirma → la salida REAL (OutputScreen) pinta la cinta con ese texto; Ctrl+Mayús+H la quita", async () => {
  const b = createAlertBackend();
  const m = await mountBar({ backend: b });
  const out = win.document.createElement("div");
  win.document.body.appendChild(out);
  const outRoot = createRoot(out, { onCaughtError: () => undefined });
  const paint = async () => { const o = b.output(); await act(async () => { outRoot.render(createElement(OutputScreen, { state: o?.state ?? null, seq: o?.seq ?? null })); }); };
  try {
    await m.type("AVISO DE PRUEBA EXTREMO A EXTREMO");
    await m.press("Enter", {}, m.q("#alert-input"));
    await m.settle(40);
    await paint();
    await act(async () => { await sleep(60); });
    assert.equal(out.querySelector(".alert-text")?.textContent, "AVISO DE PRUEBA EXTREMO A EXTREMO");
    assert.equal(out.querySelector(".alert-band").style.bottom, "0px");
    assert.match(m.q('[data-testid="alert-status"]').textContent, /EN PANTALLA/);

    await m.press("H", { ctrlKey: true, shiftKey: true });
    await m.settle(40);
    await paint();
    await act(async () => { await sleep(350); });
    assert.equal(out.querySelector(".alert-band"), null, "quitada de la salida");
    assert.equal(m.q('[data-testid="alert-status"]'), null);
  } finally {
    await act(async () => { outRoot.unmount(); });
    out.remove();
    await m.unmount();
  }
});

test("higiene del panel: la barra no usa alert/confirm/prompt ni bucles de espera, y teclear 300 caracteres seguidos mide pocas veces y rápido", async () => {
  let measures = 0;
  const counting = { fit: (text, theme) => { measures++; return model.evaluateFit(theme, simulatedHeight(text)); }, dispose() {} };
  const m = await mountBar({ measurer: counting });
  const t0 = performance.now();
  for (let n = 1; n <= 300; n += 25) await m.type("w".repeat(n));
  const elapsed = performance.now() - t0;
  assert.ok(measures <= 13 * 2, `una medición por pulsación como mucho (${measures})`);
  assert.ok(elapsed < 1500, `${elapsed} ms`);
  await m.unmount();
  const fs = await import("node:fs");
  for (const f of ["AlertBar.tsx", "AlertSettings.tsx", "controller.ts", "model.ts"]) {
    const src = fs.readFileSync(new URL(`../src/control/alerts/${f}`, import.meta.url), "utf8");
    assert.doesNotMatch(src, /\b(window\.)?(alert|confirm|prompt)\(/, `${f} usa un diálogo del sistema`);
    assert.doesNotMatch(src, /while\s*\(\s*true\s*\)/, `${f} tiene un bucle infinito`);
  }
});
