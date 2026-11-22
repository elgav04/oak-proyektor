import assert from "node:assert/strict";
import test, { after } from "node:test";
import { setupDom, sleep } from "./dom.mjs";
import { closeServer, loadTs } from "./helper.mjs";

// Importación de Biblias desde el panel: validación del formulario, controlador (eventos) y la vista. Todo con datos sintéticos (nunca texto bíblico).
const win = setupDom();
for (const name of ["KeyboardEvent", "MouseEvent", "InputEvent", "HTMLInputElement", "HTMLSelectElement", "HTMLTextAreaElement", "HTMLButtonElement", "HTMLLabelElement"]) {
  if (win[name]) globalThis[name] = win[name];
}
const { act, createElement } = await import("react");
const { createRoot } = await import("react-dom/client");
const { createStore } = await import("zustand/vanilla");
const { checkImportForm, createImportController, EMPTY_FORM, NUMBERING_PROFILES } = await loadTs("/src/control/bible/importController.ts");
const { BibleImportSection } = await loadTs("/src/control/bible/BibleImport.tsx");
after(closeServer);

const bible = (abbreviation, over = {}) => ({ id: 1, abbreviation, name: `Biblia ${abbreviation}`, language: "es", attribution: "", numberingProfile: null, books: 66, verses: 31102, ...over });
const good = (over = {}) => ({ ...EMPTY_FORM, path: "C:\\Biblias\\nueva.json", abbreviation: "NVI", name: "Nueva", language: "es", ...over });

/** Cliente IPC falso: registra lo enviado y permite emitir eventos. */
function fakeApi() {
  const sent = [];
  const handlers = new Map();
  return {
    sent,
    api: {
      send: (type, payload) => sent.push({ type, payload }),
      subscribe: (type, handler) => { const set = handlers.get(type) ?? new Set(); set.add(handler); handlers.set(type, set); return () => set.delete(handler); },
    },
    emit: (type, payload) => { for (const h of handlers.get(type) ?? []) h(payload); },
  };
}

// ───── validación del formulario ─────

test("un formulario completo y con una abreviatura nueva se puede importar", () => {
  const c = checkImportForm(good(), [bible("RVR1960")]);
  assert.deepEqual(c.errors, []);
  assert.equal(c.existing, undefined);
});

test("faltan datos: ruta, abreviatura, nombre e idioma se piden, y la ruta debe ser completa", () => {
  const c = checkImportForm({ ...EMPTY_FORM, language: "" }, []);
  assert.equal(c.errors.length, 4);
  assert.match(checkImportForm(good({ path: "version.json" }), []).errors[0], /ruta debe ser completa/);
  assert.match(checkImportForm(good({ path: "\\\\servidor\\x.json" }), []).errors[0], /ruta debe ser completa/);
  assert.deepEqual(checkImportForm(good({ path: "\"C:\\Biblias\\con comillas.json\"" }), []).errors, [], "las comillas pegadas se quitan");
  assert.equal(checkImportForm(good({ abbreviation: "X".repeat(21) }), []).errors.length, 1);
  assert.equal(checkImportForm(good({ path: "C:\\x\\biblia.xml" }), []).warnings.length, 1, "otra extensión: aviso, no bloqueo");
});

test("PROTECCIÓN: una abreviatura ya instalada exige confirmar la actualización", () => {
  const installed = [bible("RVR1960")];
  const sin = checkImportForm(good({ abbreviation: "RVR1960" }), installed);
  assert.equal(sin.existing?.abbreviation, "RVR1960");
  assert.ok(sin.errors.some((e) => /confirmación/.test(e)));
  assert.deepEqual(checkImportForm(good({ abbreviation: "RVR1960", confirmUpdate: true }), installed).errors, []);
});

test("PROTECCIÓN: una abreviatura que solo difiere en mayúsculas se rechaza aunque se confirme (crearía una Biblia gemela)", () => {
  const c = checkImportForm(good({ abbreviation: "rvr1960", confirmUpdate: true }), [bible("RVR1960")]);
  assert.ok(c.errors.some((e) => /Ya existe «RVR1960»/.test(e)));
});

test("cambiar el perfil de numeración de una Biblia instalada avisa", () => {
  const installed = [bible("RVR1960", { numberingProfile: "merge-ps47-3jn" })];
  const c = checkImportForm(good({ abbreviation: "RVR1960", confirmUpdate: true, numberingProfile: "none" }), installed);
  assert.equal(c.warnings.length, 1);
  assert.deepEqual(checkImportForm(good({ abbreviation: "RVR1960", confirmUpdate: true, numberingProfile: "merge-ps47-3jn" }), installed).warnings, []);
});

test("los perfiles de numeración ofrecidos son los del backend", () => {
  assert.deepEqual(NUMBERING_PROFILES.map((p) => p.id), ["none", "merge-ps47-3jn"]);
  assert.match(checkImportForm(good({ numberingProfile: "inventado" }), []).errors[0], /Perfil de numeración desconocido/);
});

// ───── controlador ─────

test("start manda bible.import con la ruta limpia, perfil null si es «none» y aviso null si está vacío; solo una a la vez", () => {
  const { api, sent } = fakeApi();
  const c = createImportController(api);
  assert.equal(c.start(good({ path: "  \"C:\\x\\v.json\"  ", abbreviation: " NVI ", name: " Nueva ", attribution: "   " })), true);
  assert.deepEqual(sent, [{ type: "bible.import", payload: { path: "C:\\x\\v.json", abbreviation: "NVI", name: "Nueva", language: "es", numberingProfile: null, attribution: null } }]);
  assert.equal(c.store.getState().status, "running");
  assert.equal(c.start(good()), false, "una segunda mientras corre no se manda");
  assert.equal(sent.length, 1);
  c.dispose();
});

test("el perfil y el aviso escritos viajan tal cual", () => {
  const { api, sent } = fakeApi();
  const c = createImportController(api);
  c.start(good({ numberingProfile: "merge-ps47-3jn", attribution: "Aviso inventado" }));
  assert.equal(sent[0].payload.numberingProfile, "merge-ps47-3jn");
  assert.equal(sent[0].payload.attribution, "Aviso inventado");
  c.dispose();
});

test("el avance y el resultado llegan por eventos; al terminar bien se avisa (para refrescar la lista)", () => {
  const { api, emit } = fakeApi();
  const finished = [];
  const c = createImportController(api, (r) => finished.push(r));
  c.start(good());
  emit("bible.importProgress", { abbreviation: "NVI", phase: "saving", done: 500, total: 2000 });
  assert.deepEqual([c.store.getState().phase, c.store.getState().done, c.store.getState().total], ["saving", 500, 2000]);
  const result = { abbreviation: "NVI", ok: true, errorCode: null, errorMessage: null, report: { created: true } };
  emit("bible.importFinished", result);
  assert.equal(c.store.getState().status, "finished");
  assert.equal(c.store.getState().result, result);
  assert.equal(finished.length, 1);
  c.dismiss();
  assert.equal(c.store.getState().status, "idle");
  c.start(good());                                             // y se puede importar otra
  assert.equal(c.store.getState().status, "running");
  c.dispose();
});

test("un avance que llega sin importación en curso se ignora", () => {
  const { api, emit } = fakeApi();
  const c = createImportController(api);
  emit("bible.importProgress", { abbreviation: "NVI", phase: "saving", done: 1, total: 2 });
  assert.equal(c.store.getState().status, "idle");
  c.dispose();
});

test("si el backend rechaza el comando (p. ej. ya hay una importación) se muestra como importación fallida", () => {
  const { api, emit } = fakeApi();
  const c = createImportController(api);
  c.start(good());
  emit("command.failed", { commandId: "x", commandType: "output.open", error: { code: "otro", message: "ajeno" } });   // de otro comando: se ignora
  assert.equal(c.store.getState().status, "running");
  emit("command.failed", { commandId: "y", commandType: "bible.import", error: { code: "bible.importBusy", message: "Ya hay una importación de Biblia en curso." } });
  const s = c.store.getState();
  assert.equal(s.status, "finished");
  assert.deepEqual([s.result.ok, s.result.errorCode, s.result.abbreviation], [false, "bible.importBusy", "NVI"]);
  c.dispose();
});

// ───── la vista ─────

async function mountSection({ installed = [bible("RVR1960")], connected = true } = {}) {
  const f = fakeApi();
  const importer = createImportController(f.api);
  const store = createStore(() => ({ bibles: installed }));
  const container = win.document.createElement("div");
  win.document.body.appendChild(container);
  const root = createRoot(container);
  await act(async () => { root.render(createElement(BibleImportSection, { importer, bibles: { store }, connected })); });
  const q = (sel) => container.querySelector(sel);
  const fill = async (placeholderOrLabel, value) => {
    const el = [...container.querySelectorAll("input, textarea")].find((n) => n.getAttribute("placeholder") === placeholderOrLabel || n.closest("label")?.textContent.startsWith(placeholderOrLabel));
    assert.ok(el, `campo «${placeholderOrLabel}»`);
    await act(async () => {
      const proto = el.tagName === "TEXTAREA" ? win.HTMLTextAreaElement.prototype : win.HTMLInputElement.prototype;
      Object.getOwnPropertyDescriptor(proto, "value").set.call(el, value);
      el.dispatchEvent(new win.Event("input", { bubbles: true }));
    });
  };
  const importButton = () => [...container.querySelectorAll("button")].find((b) => b.textContent.trim() === "Importar");
  return { ...f, importer, container, q, fill, importButton, unmount: async () => { await act(async () => { root.unmount(); }); importer.dispose(); } };
}

test("VISTA: el botón Importar se habilita solo con el formulario válido y manda el comando", async () => {
  const m = await mountSection();
  assert.equal(m.importButton().disabled, true);
  await m.fill("C:\\Biblias\\version.json", "C:\\Biblias\\nueva.json");
  await m.fill("NVI", "NVI");
  await m.fill("Nueva Versión Internacional", "Nueva versión sintética");
  assert.equal(m.importButton().disabled, false);
  await act(async () => { m.importButton().click(); });
  assert.equal(m.sent.length, 1);
  assert.deepEqual(m.sent[0].payload, { path: "C:\\Biblias\\nueva.json", abbreviation: "NVI", name: "Nueva versión sintética", language: "es", numberingProfile: null, attribution: null });
  assert.ok(m.q('[data-testid="import-progress"]'), "se ve el avance");
  assert.equal(m.importButton().disabled, true, "mientras corre no se puede lanzar otra");
  await m.unmount();
});

test("VISTA PROTECCIÓN: con una abreviatura instalada hay que marcar la confirmación; se parte del perfil y los datos de la Biblia existente", async () => {
  const m = await mountSection({ installed: [bible("RVR1960", { name: "Reina-Valera sintética", numberingProfile: "merge-ps47-3jn" })] });
  await m.fill("C:\\Biblias\\version.json", "C:\\Biblias\\otra.json");
  await m.fill("NVI", "RVR1960");
  assert.ok(m.q('[data-testid="confirm-update"]'), "aparece el recuadro de confirmación");
  assert.equal(m.importButton().disabled, true, "sin confirmar no se puede");
  const box = m.q('[data-testid="confirm-update"] input');
  await act(async () => { box.click(); });
  assert.equal(m.importButton().disabled, false);
  await act(async () => { m.importButton().click(); });
  const p = m.sent[0].payload;
  assert.equal(p.numberingProfile, "merge-ps47-3jn", "reimportar CONSERVA el perfil de numeración guardado (no lo cambia sin pedirlo)");
  assert.equal(p.name, "Reina-Valera sintética", "nombre tomado de la Biblia instalada si no se escribió otro");
  await m.unmount();
});

test("VISTA: cambiar la abreviatura después de confirmar quita la confirmación", async () => {
  const m = await mountSection();
  await m.fill("C:\\Biblias\\version.json", "C:\\Biblias\\otra.json");
  await m.fill("NVI", "RVR1960");
  await act(async () => { m.q('[data-testid="confirm-update"] input').click(); });
  assert.equal(m.importButton().disabled, false);
  await m.fill("NVI", "RVR1960x");
  await m.fill("NVI", "RVR1960");
  assert.equal(m.q('[data-testid="confirm-update"] input').checked, false);
  assert.equal(m.importButton().disabled, true);
  await m.unmount();
});

test("VISTA: el informe de una importación terminada muestra conteos, índice de búsqueda y avisos; y se puede cerrar", async () => {
  const m = await mountSection();
  await m.fill("C:\\Biblias\\version.json", "C:\\Biblias\\nueva.json");
  await m.fill("NVI", "NVI");
  await m.fill("Nueva Versión Internacional", "Nueva");
  await act(async () => { m.importButton().click(); });
  await act(async () => { m.emit("bible.importProgress", { abbreviation: "NVI", phase: "saving", done: 1000, total: 4000 }); });
  assert.match(m.q('[data-testid="import-progress"]').textContent, /Guardando en la base de datos.*25%/);
  const report = { bibleId: 2, created: true, numberingProfile: "none", sourceVerses: 31104, inserted: 31104, updated: 0, unchanged: 0, deleted: 0, cleanedVerses: 3, joinedVerses: 1, books: 66, chapters: 1189, verses: 31104, emptyVerses: 0, verseTotalDifference: 2, ftsIntegrityOk: true, affectedItems: [], warnings: ["Aviso sintético de la importación."], elapsedMs: 1800 };
  await act(async () => { m.emit("bible.importFinished", { abbreviation: "NVI", ok: true, errorCode: null, errorMessage: null, report }); });
  const text = m.q('[data-testid="import-result"]').textContent;
  assert.match(text, /Importación de «NVI» terminada/);
  assert.match(text, /Biblia nueva/);
  assert.match(text, /31\.?104 versículos en 66 libros y 1189 capítulos/);
  assert.match(text, /\+2/);
  assert.match(text, /Índice de búsqueda: correcto/);
  assert.match(text, /Aviso sintético de la importación/);
  assert.equal(m.q('[data-testid="import-progress"]'), null);
  await act(async () => { [...m.container.querySelectorAll("button")].find((b) => b.textContent.includes("Cerrar informe")).click(); });
  assert.equal(m.q('[data-testid="import-result"]'), null);
  await m.unmount();
});

test("VISTA: una importación fallida lo dice con el motivo y que no se cambió nada; un índice con problemas se marca", async () => {
  const m = await mountSection();
  await m.fill("C:\\Biblias\\version.json", "C:\\Biblias\\rota.json");
  await m.fill("NVI", "NVI");
  await m.fill("Nueva Versión Internacional", "Nueva");
  await act(async () => { m.importButton().click(); });
  await act(async () => { m.emit("bible.importFinished", { abbreviation: "NVI", ok: false, errorCode: "bible.sourceInvalid", errorMessage: "No se pudo leer el archivo como «youversion-json».", report: null }); });
  const text = m.q('[data-testid="import-result"]').textContent;
  assert.match(text, /falló\. No se cambió nada/);
  assert.match(text, /No se pudo leer el archivo/);
  assert.match(text, /bible\.sourceInvalid/);
  await m.unmount();
});

test("VISTA: desconectado, el formulario y el botón están desactivados", async () => {
  const m = await mountSection({ connected: false });
  assert.equal(m.q("fieldset").disabled, true);
  assert.equal(m.importButton().disabled, true);
  await m.unmount();
});
