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
const { allowKindFor, checkImportForm, createImportController, EMPTY_FORM, NUMBERING_PROFILES } = await loadTs("/src/control/bible/importController.ts");
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

test("una abreviatura que solo difiere en mayúsculas es la MISMA Biblia: exige la misma confirmación y no crea una gemela", () => {
  const installed = [bible("RVR1960")];
  const sin = checkImportForm(good({ abbreviation: "rvr1960" }), installed);
  assert.equal(sin.existing?.abbreviation, "RVR1960", "se identifica la Biblia guardada");
  assert.ok(sin.errors.some((e) => /confirmación/.test(e)));
  assert.deepEqual(checkImportForm(good({ abbreviation: "rvr1960", confirmUpdate: true }), installed).errors, []);
  assert.deepEqual(checkImportForm(good({ abbreviation: "  Rvr1960 ", confirmUpdate: true }), installed).errors, [], "los espacios de los extremos no cuentan");
});

test("allowKindFor: cada rechazo del backend se salta con SU indicador; los demás errores no se pueden saltar", () => {
  assert.equal(allowKindFor("bible.tooManyChanges"), "change");
  assert.equal(allowKindFor("bible.tooManyDeletions"), "deletion");
  assert.equal(allowKindFor("bible.booksWouldDisappear"), "deletion");
  for (const code of ["bible.fileMissing", "bible.sourceInvalid", "bible.importBusy", "bible.importFailed", "bible.invalidRequest", "internal", "", null, undefined]) assert.equal(allowKindFor(code), null, String(code));
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

test("start manda bible.import con la ruta limpia, el perfil SIEMPRE explícito (también «none»), aviso null si está vacío y sin indicadores; solo una a la vez", () => {
  const { api, sent } = fakeApi();
  const c = createImportController(api);
  assert.equal(c.start(good({ path: "  \"C:\\x\\v.json\"  ", abbreviation: " NVI ", name: " Nueva ", attribution: "   " })), true);
  assert.deepEqual(sent, [{ type: "bible.import", payload: { path: "C:\\x\\v.json", abbreviation: "NVI", name: "Nueva", language: "es", numberingProfile: "none", attribution: null, allowLargeDeletion: false, allowLargeChange: false } }]);
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
  assert.deepEqual(m.sent[0].payload, { path: "C:\\Biblias\\nueva.json", abbreviation: "NVI", name: "Nueva versión sintética", language: "es", numberingProfile: "none", attribution: null, allowLargeDeletion: false, allowLargeChange: false });
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

// ───── protecciones de la reimportación (v11) ─────

test("un rechazo que se puede saltar ofrece repetir con SU indicador; el resto de errores no", () => {
  const { api, sent, emit } = fakeApi();
  const c = createImportController(api);
  c.start(good({ abbreviation: "RVR1960", confirmUpdate: true, numberingProfile: "merge-ps47-3jn" }));
  emit("bible.importFinished", { abbreviation: "RVR1960", ok: false, errorCode: "bible.tooManyChanges", errorMessage: "sobrescribiría", report: null });
  assert.equal(c.store.getState().canRetryAllowing, true);

  assert.equal(c.retryAllowing(), true);
  assert.equal(sent.length, 2);
  assert.deepEqual([sent[1].payload.allowLargeChange, sent[1].payload.allowLargeDeletion], [true, false], "solo el indicador que corresponde");
  assert.equal(sent[1].payload.numberingProfile, "merge-ps47-3jn", "y el MISMO formulario (perfil incluido)");
  assert.equal(c.store.getState().status, "running");
  assert.equal(c.store.getState().canRetryAllowing, false);

  emit("bible.importFinished", { abbreviation: "RVR1960", ok: false, errorCode: "bible.booksWouldDisappear", errorMessage: "desaparecería", report: null });
  assert.equal(c.retryAllowing(), true);
  assert.deepEqual([sent[2].payload.allowLargeChange, sent[2].payload.allowLargeDeletion], [false, true]);

  emit("bible.importFinished", { abbreviation: "RVR1960", ok: false, errorCode: "bible.sourceInvalid", errorMessage: "no es UTF-8", report: null });
  assert.equal(c.store.getState().canRetryAllowing, false);
  assert.equal(c.retryAllowing(), false, "un archivo ilegible no se «fuerza»");
  assert.equal(sent.length, 3);
  c.dispose();
});

test("sin una importación rechazada no hay nada que repetir, y mientras corre otra tampoco", () => {
  const { api, sent, emit } = fakeApi();
  const c = createImportController(api);
  assert.equal(c.retryAllowing(), false);
  c.start(good());
  assert.equal(c.retryAllowing(), false);
  emit("bible.importFinished", { abbreviation: "NVI", ok: true, errorCode: null, errorMessage: null, report: { created: true } });
  assert.equal(c.retryAllowing(), false, "una importación que salió bien no se repite");
  assert.equal(sent.length, 1);
  c.dispose();
});

test("VISTA: tras un rechazo aparece el recuadro de protección; el botón exige marcar la confirmación y manda el indicador", async () => {
  const m = await mountSection({ installed: [bible("RVR1960", { name: "Reina-Valera sintética" })] });
  await m.fill("C:\\Biblias\\version.json", "C:\\Biblias\\otra.json");
  await m.fill("NVI", "RVR1960");
  await act(async () => { m.q('[data-testid="confirm-update"] input').click(); });
  await act(async () => { m.importButton().click(); });
  await act(async () => { m.emit("bible.importFinished", { abbreviation: "RVR1960", ok: false, errorCode: "bible.tooManyChanges", errorMessage: "Esta importación sobrescribiría RVR1960: cambiaría el texto de 31.102 versículos.", report: null }); });

  assert.match(m.q('[data-testid="import-result"]').textContent, /No se cambió nada/);
  const box = m.q('[data-testid="override-box"]');
  assert.ok(box, "recuadro de protección");
  const forceButton = () => [...box.querySelectorAll("button")].find((b) => b.textContent.includes("Importar de todos modos"));
  assert.equal(forceButton().disabled, true, "sin confirmar no se puede");
  await act(async () => { box.querySelector('input[type="checkbox"]').click(); });
  assert.equal(forceButton().disabled, false);
  await act(async () => { forceButton().click(); });
  assert.equal(m.sent.length, 2);
  assert.deepEqual([m.sent[1].payload.allowLargeChange, m.sent[1].payload.allowLargeDeletion], [true, false]);
  assert.equal(m.sent[1].payload.abbreviation, "RVR1960");
  assert.ok(m.q('[data-testid="import-progress"]'));
  await m.unmount();
});

test("VISTA: un error que no se puede saltar (archivo ilegible) NO ofrece forzar", async () => {
  const m = await mountSection();
  await m.fill("C:\\Biblias\\version.json", "C:\\Biblias\\latin1.json");
  await m.fill("NVI", "NVI");
  await m.fill("Nueva Versión Internacional", "Nueva");
  await act(async () => { m.importButton().click(); });
  await act(async () => { m.emit("bible.importFinished", { abbreviation: "NVI", ok: false, errorCode: "bible.sourceInvalid", errorMessage: "El archivo no está en UTF-8.", report: null }); });
  assert.equal(m.q('[data-testid="override-box"]'), null);
  assert.match(m.q('[data-testid="import-result"]').textContent, /no está en UTF-8/);
  await m.unmount();
});

test("VISTA: la abreviatura en minúsculas de una Biblia instalada muestra el recuadro de confirmación (misma Biblia)", async () => {
  const m = await mountSection({ installed: [bible("RVR1960", { numberingProfile: "merge-ps47-3jn" })] });
  await m.fill("C:\\Biblias\\version.json", "C:\\Biblias\\otra.json");
  await m.fill("NVI", "rvr1960");
  assert.ok(m.q('[data-testid="confirm-update"]'));
  assert.equal(m.importButton().disabled, true);
  await act(async () => { m.q('[data-testid="confirm-update"] input').click(); });
  await act(async () => { m.importButton().click(); });
  assert.equal(m.sent[0].payload.abbreviation, "rvr1960");
  assert.equal(m.sent[0].payload.numberingProfile, "merge-ps47-3jn", "se parte del perfil de la Biblia guardada");
  await m.unmount();
});

test("VISTA: el informe de una reimportación muestra los textos modificados, la copia previa y el perfil conservado", async () => {
  const m = await mountSection({ installed: [bible("RVR1960")] });
  await m.fill("C:\\Biblias\\version.json", "C:\\Biblias\\version.json");
  await m.fill("NVI", "RVR1960");
  await act(async () => { m.q('[data-testid="confirm-update"] input').click(); });
  await act(async () => { m.importButton().click(); });
  const report = { bibleId: 1, created: false, numberingProfile: "merge-ps47-3jn", sourceVerses: 31102, inserted: 0, updated: 12, unchanged: 31090, deleted: 0, cleanedVerses: 0, joinedVerses: 0, books: 66, chapters: 1189, verses: 31102, emptyVerses: 0, verseTotalDifference: 0, ftsIntegrityOk: true, affectedItems: [], warnings: [], elapsedMs: 900,
    previousVerses: 31102, backupPath: "C:\\datos\\backups\\oak-proyektor.bible-import.20261124T000000000Z.RVR1960.db", numberingProfileKept: true, discardedVerses: 2, duplicateChaptersDifferent: 1 };
  await act(async () => { m.emit("bible.importFinished", { abbreviation: "RVR1960", ok: true, errorCode: null, errorMessage: null, report }); });
  assert.match(m.q('[data-testid="modified-count"]').textContent, /Textos modificados: 12 de los 31\.?102 que había \(0,04 %\)/);
  assert.match(m.q('[data-testid="backup-path"]').textContent, /oak-proyektor\.bible-import\.20261124T000000000Z\.RVR1960\.db/);
  assert.match(m.q('[data-testid="profile-line"]').textContent, /merge-ps47-3jn \(conservado/);
  assert.match(m.q('[data-testid="import-result"]').textContent, /Descartados \(numeración inválida o fuera del canon\): 2/);
  assert.match(m.q('[data-testid="import-result"]').textContent, /1 capítulo\(s\) repetido\(s\) con texto DISTINTO/);
  await m.unmount();
});

test("VISTA: sin copia previa el informe lo dice", async () => {
  const m = await mountSection();
  await m.fill("C:\\Biblias\\version.json", "C:\\Biblias\\nueva.json");
  await m.fill("NVI", "NVI");
  await m.fill("Nueva Versión Internacional", "Nueva");
  await act(async () => { m.importButton().click(); });
  const report = { bibleId: 2, created: true, numberingProfile: "none", sourceVerses: 8, inserted: 8, updated: 0, unchanged: 0, deleted: 0, cleanedVerses: 0, joinedVerses: 0, books: 2, chapters: 2, verses: 8, emptyVerses: 0, verseTotalDifference: -31094, ftsIntegrityOk: true, affectedItems: [], warnings: [], elapsedMs: 100,
    previousVerses: 0, backupPath: null, numberingProfileKept: false, discardedVerses: 0, duplicateChaptersDifferent: 0 };
  await act(async () => { m.emit("bible.importFinished", { abbreviation: "NVI", ok: true, errorCode: null, errorMessage: null, report }); });
  assert.match(m.q('[data-testid="backup-path"]').textContent, /no hizo falta/);
  assert.equal(m.q('[data-testid="modified-count"]'), null, "una Biblia nueva no tiene «textos modificados»");
  await m.unmount();
});