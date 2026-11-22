import assert from "node:assert/strict";
import test, { after } from "node:test";
import { createFakeBackend, tick, until, verseText } from "./bible-fixtures.mjs";
import { closeServer, loadTs } from "./helper.mjs";

// El controlador habla con un backend FALSO a través del cliente IPC REAL (sobre v10). Medición simulada: capacidad en caracteres.
const { createBibleController } = await loadTs("/src/control/bible/controller.ts");
after(closeServer);

const CAP = 160;
const fakeFit = (cap = CAP) => () => ({ fits: (blocks) => blocks.join(" ").length <= cap, dispose: () => undefined });

async function setup({ cap = CAP, backend: backendOptions, controller: controllerOptions } = {}) {
  const backend = createFakeBackend(backendOptions);
  const c = createBibleController({ api: backend.client, createFit: fakeFit(cap), searchDebounceMs: 5, recordDelayMs: 25, sendIntervalMs: 30, ...controllerOptions });
  await c.init();
  return { backend, c, s: () => c.store.getState() };
}
const typeQuery = async (c, s, text) => { c.setQuery(text); await tick(15); await until(() => s().passage !== null || s().suggestions.length > 0 || s().hits.length > 0 || s().message !== null); };

test("init carga Biblias, ajustes e historial; el crossfade por defecto es corto (280 ms) y las consultas van con el sobre v10", async () => {
  const { backend, s } = await setup();
  assert.equal(s().loaded, true);
  assert.equal(s().activeBible, "SIN");
  assert.equal(s().settings.transitionMs, 280);
  assert.ok(s().settings.transitionMs >= 250 && s().settings.transitionMs <= 300);
  assert.deepEqual(backend.queries.map((q) => q.type).sort(), ["bible.getSettings", "bible.history", "bible.list"]);
  assert.ok(backend.queries.every((q) => q.v === 10));
});

test("una referencia con rango navega al capítulo, selecciona el rango y NO proyecta nada hasta Enter", async () => {
  const { backend, c, s } = await setup();
  await typeQuery(c, s, "alfa 1:3-5");
  await until(() => s().built !== null);
  assert.equal(s().passage.verses.length, 12, "la lista es el capítulo entero");
  assert.deepEqual([Math.min(s().anchor, s().active), Math.max(s().anchor, s().active)], [2, 4]);
  assert.equal(s().built.reference, "Alfa 1:3-5");
  assert.equal(backend.sent("output.showContent").length, 0, "navegar al escribir no proyecta");
  c.project();
  await until(() => backend.sent("output.showContent").length === 1);
  const sent = backend.lastContent();
  assert.equal(sent.transitionMs, 280);
  assert.equal(sent.theme.autoFit, true);
  assert.equal(sent.blocks.length, 2);
  assert.match(sent.blocks[0], /^³ .*⁴ .*⁵ /);   // versículos 3, 4 y 5 con su número
  assert.equal(sent.blocks[1], "Alfa 1:3-5 · SIN");
});

test("un capítulo entero («alfa 2») selecciona solo el primer versículo", async () => {
  const { c, s } = await setup();
  await typeQuery(c, s, "alfa 2");
  await until(() => s().built !== null);
  assert.equal(s().passage.verses.length, 4);
  assert.deepEqual([s().anchor, s().active], [0, 0]);
});

test("un prefijo de libro muestra sugerencias; elegir una abre el capítulo 1; 'buscar en el texto' también está disponible", async () => {
  const { c, s } = await setup();
  await typeQuery(c, s, "ga");
  assert.deepEqual(s().suggestions.map((x) => x.name), ["Gamma"]);
  assert.equal(s().canSearchText, true);
  c.pickSuggestion(3);
  await until(() => s().passage !== null);
  assert.equal(s().passage.verses[0].bookName, "Gamma");
  assert.equal(s().suggestions.length, 0);
});

test("lo que no es libro ni referencia se busca como texto libre; abrir un resultado navega a su capítulo y lo selecciona", async () => {
  const { c, s } = await setup();
  await typeQuery(c, s, "tolen");   // no es prefijo de ningún libro
  await until(() => s().hits.length > 0);
  const hit = s().hits[3];
  c.openHit(hit);
  await until(() => s().passage !== null);
  assert.equal(s().passage.verses[s().active].verseNumber, hit.verseNumber);
  assert.equal(s().passage.verses[s().active].chapter, hit.chapter);
  assert.equal(s().hits.length, 0);
});

test("texto con números que el parser no entiende cae a la búsqueda de texto (en vez de dar error)", async () => {
  const { c, s } = await setup();
  await typeQuery(c, s, "40 dias");
  await until(() => s().message !== null || s().hits.length > 0);
  assert.equal(s().passage, null);
  assert.equal(s().message, "Sin resultados.");
});

test("Enter en el cuadro con una referencia la proyecta de inmediato («alfa 1:2» + Enter = en pantalla)", async () => {
  const { backend, c, s } = await setup();
  c.setQuery("alfa 1:2");
  c.submitSearch();
  await until(() => backend.sent("output.showContent").length === 1);
  assert.match(backend.lastContent().blocks[1], /^Alfa 1:2 /);
});

test("flechas: mueven la selección (con límites) y con «proyectar al navegar» apagado no mandan nada a la salida", async () => {
  const { backend, c, s } = await setup();
  await typeQuery(c, s, "alfa 1:1");
  await until(() => s().built !== null);
  c.moveSelection(-1);
  assert.equal(s().active, 0, "no sale por arriba");
  c.moveSelection(1); c.moveSelection(1);
  assert.equal(s().active, 2);
  for (let i = 0; i < 30; i++) c.moveSelection(1);
  assert.equal(s().active, 11, "no sale por abajo");
  await tick(60);
  assert.equal(backend.sent("output.showContent").length, 0);
});

test("Mayús+flecha amplía la selección a varios versículos y se proyectan juntos", async () => {
  const { backend, c, s } = await setup({ cap: 400 });
  await typeQuery(c, s, "alfa 1:4");
  await until(() => s().built !== null);
  c.moveSelection(1, true); c.moveSelection(1, true);
  assert.equal(s().built, null, "H4: mover la selección NO pagina; se pagina al proyectar (o al quedar quieta)");
  c.project();
  await until(() => backend.sent("output.showContent").length === 1);
  assert.equal(s().built.reference, "Alfa 1:4-6");
  assert.equal(backend.lastContent().blocks.length, 2);
  assert.equal(s().built.pages.length, 1);
});

test("NAVEGACIÓN RÁPIDA: 60 cambios muy seguidos con «proyectar al navegar» → pocos mensajes y lo último proyectado es SIEMPRE el último versículo", async () => {
  const { backend, c, s } = await setup();
  await c.saveSettings({ ...s().settings, projectOnNavigate: true });
  await typeQuery(c, s, "alfa 1:1");
  await until(() => s().built !== null);
  backend.commands.length = 0;
  for (let i = 0; i < 60; i++) c.moveSelection(i % 2 === 0 ? 1 : 0 === 1 ? 1 : 1);   // 11 pasos válidos y el resto contra el límite
  await tick(120);
  assert.equal(s().active, 11);
  const sent = backend.sent("output.showContent");
  assert.ok(sent.length >= 1 && sent.length <= 6, `se mandaron ${sent.length} proyecciones`);
  assert.match(sent.at(-1).payload.blocks[1], /^Alfa 1:12 /, "la última proyección es el último versículo");
  assert.equal(s().projected.reference, "Alfa 1:12");
});

test("ir y volver rápido no deja un versículo viejo al final: gana la última pulsación", async () => {
  const { backend, c, s } = await setup();
  await c.saveSettings({ ...s().settings, projectOnNavigate: true });
  await typeQuery(c, s, "alfa 1:6");
  await until(() => s().built !== null);
  backend.commands.length = 0;
  for (const d of [1, 1, 1, -1, -1, -1, -1, 1, -1]) c.moveSelection(d);
  await tick(120);
  // desde el 6: +1 +1 +1 → 9; −1 ×4 → 5; +1 → 6; −1 → 5
  assert.equal(s().passage.verses[s().active].verseNumber, 5);
  assert.match(backend.sent("output.showContent").at(-1).payload.blocks[1], /^Alfa 1:5 /);
});

test("cada proyección se anota en el historial (una consulta con record:true); recorrer versículos deprisa solo anota el último; las páginas no re-anotan", async () => {
  const { backend, c, s } = await setup();
  await c.saveSettings({ ...s().settings, projectOnNavigate: true });
  await typeQuery(c, s, "alfa 1:1");
  await until(() => s().built !== null);
  for (let i = 0; i < 5; i++) c.moveSelection(1);
  await tick(150);
  const recorded = backend.queries.filter((q) => q.type === "bible.lookup" && q.payload.record === true);
  assert.equal(recorded.length, 1);
  assert.equal(recorded[0].payload.reference, "Alfa 1:6");
  await until(() => s().history.length === 1);
  assert.equal(s().history[0].label, "Alfa 1:6");
});

test("VERSÍCULO LARGO: se reparte en páginas medidas, el indicador es N/M, las teclas de página proyectan y no se sale de los límites", async () => {
  const { backend, c, s } = await setup({ cap: 200, backend: { longVerses: new Set(["1:1:3"]) } });
  await typeQuery(c, s, "alfa 1:3");
  await until(() => s().built !== null);
  const built = s().built;
  assert.ok(built.pages.length >= 3, `páginas: ${built.pages.length}`);
  for (const p of built.pages) assert.ok(p.join(" ").length <= 200, "una página no cabe");
  // sin pérdida: el texto del versículo se recompone
  const original = verseText(1, 1, 3, 14);
  assert.equal(built.pages.map((p) => p[0]).join(" ").replace(/^³ /, "").replace(/\s+/g, " "), original);
  assert.equal(s().pageIndex, 0);

  c.pageBy(-1);   // por debajo de la primera: no se mueve ni manda nada
  assert.equal(s().pageIndex, 0);
  await tick(40);
  assert.equal(backend.sent("output.showContent").length, 0);
  c.pageBy(1);
  await until(() => backend.sent("output.showContent").length >= 1);
  assert.equal(s().pageIndex, 1);
  assert.deepEqual(s().projected, { reference: "Alfa 1:3", page: 1, pages: built.pages.length });
  assert.equal(backend.lastContent().blocks[0], built.pages[1][0]);
  for (let i = 0; i < 10; i++) c.pageBy(1);
  assert.equal(s().pageIndex, built.pages.length - 1, "no pasa de la última");
  await tick(100);
  assert.equal(backend.lastContent().blocks[0], built.pages.at(-1)[0]);
  c.moveSelection(1);   // otro versículo: vuelve a la página 1
  assert.equal(s().pageIndex, 0);
});

test("Esc: quita el texto proyectado (clearContent con el crossfade configurado) y cancela lo pendiente", async () => {
  const { backend, c, s } = await setup();
  await c.saveSettings({ ...s().settings, projectOnNavigate: true });
  await typeQuery(c, s, "alfa 1:1");
  await until(() => s().built !== null);
  c.moveSelection(1); c.moveSelection(1);   // queda una proyección pendiente
  c.clearProjection();
  await tick(5);   // el backend falso registra los comandos de forma asíncrona
  const before = backend.sent("output.showContent").length;
  await tick(100);
  assert.equal(backend.sent("output.showContent").length, before, "lo pendiente no se manda tras limpiar");
  assert.deepEqual(backend.sent("output.clearContent").at(-1).payload, { transitionMs: 280 });
  assert.equal(s().projected, null);
});

test("el historial permite volver a proyectar una cita", async () => {
  const { backend, c, s } = await setup();
  backend.state.history.push({ id: 99, bible: "SIN", bookNumber: 2, bookName: "Beta Mayor", chapter: 1, verseStart: 2, verseEnd: 3, label: "Beta Mayor 1:2-3", citedAt: "2026-01-01T00:00:00Z" });
  await c.init();
  assert.equal(s().history[0].id, 99);
  c.reproject(s().history[0]);
  await until(() => backend.sent("output.showContent").length === 1);
  assert.equal(backend.lastContent().blocks[1], "Beta Mayor 1:2-3 · SIN");
});

test("las opciones se aplican: sin superíndice, nombre corto y aviso de derechos (vacío = nada; con texto = solo en la última página)", async () => {
  const { backend, c, s } = await setup({ cap: 400, backend: { attribution: "" } });
  await c.saveSettings({ ...s().settings, superscriptNumbers: false, showAttribution: true, shortNames: [{ bible: "SIN", shortName: "S.1" }] });
  await typeQuery(c, s, "alfa 1:1");
  await until(() => s().built !== null);
  assert.deepEqual(s().built.pages[0].length, 2, "atribución vacía: nada");
  assert.equal(s().built.pages[0][1], "Alfa 1:1 · S.1");
  assert.doesNotMatch(s().built.pages[0][0], /^[¹²³⁴-⁹]/);

  await c.saveAttribution("SIN", "Aviso inventado");
  assert.equal(s().built.pages[0].at(-1), "Aviso inventado");
  await c.saveSettings({ ...s().settings, showAttribution: false });
  assert.equal(s().built.pages[0].length, 2);
  assert.ok(backend.sent("bible.setAttribution").length === 1);
});

test("cambiar el tema vuelve a medir (otra medición) y a paginar; el mismo tema reutiliza lo medido", async () => {
  let created = 0;
  const backend = createFakeBackend({ longVerses: new Set(["1:1:3"]) });
  const c = createBibleController({ api: backend.client, createFit: (theme) => { created++; return { fits: (b) => b.join(" ").length <= (theme.fontSize > 64 ? 80 : 200), dispose: () => undefined }; }, searchDebounceMs: 5, recordDelayMs: 25 });
  await c.init();
  const s = () => c.store.getState();
  await typeQuery(c, s, "alfa 1:3");
  await until(() => s().built !== null);
  const pagesAt64 = s().built.pages.length;
  assert.equal(created, 1);
  c.moveSelection(1); c.moveSelection(-1);
  assert.equal(created, 1, "volver al mismo versículo no vuelve a crear la medición");
  await c.saveSettings({ ...s().settings, theme: { ...s().settings.theme, fontSize: 90 } });
  assert.equal(created, 2);
  assert.ok(s().built.pages.length > pagesAt64, "con letra más grande hacen falta más páginas");
});

test("un fallo del backend se muestra como mensaje y no rompe el controlador", async () => {
  const { c, s } = await setup();
  await typeQuery(c, s, "alfa 9:9");   // capítulo inexistente
  await until(() => s().message !== null);
  assert.match(s().message, /No existe/);
  assert.equal(s().passage, null);
  c.dismissMessage();
  assert.equal(s().message, null);
});

// ───── H4: referencias y selecciones enormes ─────

/** Medición simulada que CUENTA cuántas veces se mide (cada llamada a `fits` es una medición). */
function countingFit(cap = 400) {
  const counter = { calls: 0 };
  return { counter, createFit: () => ({ fits: (blocks) => { counter.calls++; return blocks.join(" ").length <= cap; }, dispose: () => undefined }) };
}
const BIG = "gamma 2:1";   // capítulo de 120 versículos del backend falso

test("H4: mantener Mayús+↓ NO pagina en cada pulsación (0 mediciones durante la ráfaga) y la vista previa se calcula una vez al quedar quieta", async () => {
  const { counter, createFit } = countingFit();
  const { c, s } = await setup({ controller: { createFit, previewDelayMs: 40 } });
  await typeQuery(c, s, BIG);
  await until(() => s().built !== null);
  const baseline = counter.calls;
  for (let i = 0; i < 25; i++) c.moveSelection(1, true);       // la tecla mantenida
  assert.equal(counter.calls, baseline, "ninguna medición durante la ráfaga");
  assert.equal(s().built, null);
  assert.equal(s().active - s().anchor, 25);
  await until(() => s().built !== null);                        // quieta: ahora sí
  assert.equal(s().built.reference, "Gamma 2:1-26");
  assert.ok(counter.calls > baseline);
});

test("H4: el coste de una pulsación no crece con la selección (con la selección en 5 o en 29 versículos, la ráfaga cuesta 0 mediciones)", async () => {
  const { counter, createFit } = countingFit();
  const { c, s } = await setup({ controller: { createFit, previewDelayMs: 5000 } });
  await typeQuery(c, s, BIG);
  const costs = [];
  for (const steps of [5, 24]) {
    const before = counter.calls;
    for (let i = 0; i < steps; i++) c.moveSelection(1, true);
    costs.push(counter.calls - before);
  }
  assert.deepEqual(costs, [0, 0]);
  assert.equal(s().active - s().anchor, 29);
});

test("H4: proyectar pagina en el momento aunque la vista previa aún no estuviera calculada", async () => {
  const { counter, createFit } = countingFit();
  const { backend, c, s } = await setup({ controller: { createFit, previewDelayMs: 5000 } });
  await typeQuery(c, s, BIG);
  for (let i = 0; i < 6; i++) c.moveSelection(1, true);
  assert.equal(s().built, null);
  const before = counter.calls;
  c.project();
  await until(() => backend.sent("output.showContent").length === 1);
  assert.ok(counter.calls > before, "se midió al proyectar");
  assert.match(backend.lastContent().blocks.at(-1), /^Gamma 2:1-7 /);
  // cambiar de página también calcula si hace falta
  c.moveSelection(1, true);
  assert.equal(s().built, null);
  c.pageBy(1);                                                  // no hay otra página: no pasa nada, pero no falla
  assert.ok(s().built !== null);
});

test("H4: la selección con Mayús+flechas se detiene en 30 versículos y avisa (rango demasiado grande, acótalo)", async () => {
  const { c, s } = await setup({ controller: { previewDelayMs: 5 } });
  await typeQuery(c, s, BIG);
  for (let i = 0; i < 100; i++) c.moveSelection(1, true);
  assert.equal(s().active - s().anchor + 1, 30);
  assert.match(s().message, /Rango demasiado grande/);
  assert.match(s().message, /acótalo/i);
  await until(() => s().built !== null);
  assert.equal(s().built.reference, "Gamma 2:1-30");
  // hacia arriba también, y el clic con Mayús (selectIndex extend) respeta el mismo tope
  c.selectIndex(100);
  c.selectIndex(0, true);
  assert.equal(Math.abs(s().active - s().anchor) + 1, 30);
  // una selección normal (sin Mayús) quita el aviso
  c.selectIndex(3);
  assert.equal(s().message, null);
  assert.equal(s().anchor, s().active);
});

test("H4: un rango escrito de más de 30 versículos selecciona los 30 primeros, avisa, y Enter proyecta lo seleccionado", async () => {
  const { backend, c, s } = await setup({ controller: { previewDelayMs: 5 } });
  c.setQuery("gamma 2:1-100");
  await tick(15);
  await until(() => s().passage !== null);
  assert.equal(s().passage.verses.length, 120);
  assert.equal(Math.min(s().anchor, s().active), 0);
  assert.equal(Math.max(s().anchor, s().active), 29);
  assert.match(s().message, /Rango demasiado grande \(100 versículos\)/);
  c.submitSearch();
  await until(() => backend.sent("output.showContent").length === 1);
  assert.match(backend.lastContent().blocks.at(-1), /^Gamma 2:1-30 /, "se proyectan los 30 seleccionados");
});

test("H4: «sal 1-150» (rango de capítulos enorme) responde al instante con el aviso del backend, sin tocar la lista ni proyectar", async () => {
  const { backend, c, s } = await setup();
  await typeQuery(c, s, "alfa 1:2");                            // ya hay un pasaje mostrado
  const before = s().passage;
  const t0 = Date.now();
  c.setQuery("gamma 1-150");
  await until(() => s().message !== null);
  assert.ok(Date.now() - t0 < 500, "no se bloquea");
  assert.match(s().message, /Rango demasiado grande/);
  assert.match(s().message, /Acótalo/);
  assert.equal(s().passage, before, "la lista anterior no se toca");
  assert.equal(backend.sent("output.showContent").length, 0);
  assert.deepEqual(s().hits, [], "y no se cae a una búsqueda de texto");
  c.submitSearch();                                             // Enter con el rango enorme: tampoco proyecta
  await tick(60);
  assert.equal(backend.sent("output.showContent").length, 0);
});

test("H4: defensa por si el backend devolviera más de 200 versículos: aviso y no se muestra la lista gigante", async () => {
  const { c, s } = await setup();
  c.setQuery("gamma 3:1");
  await tick(15);
  await until(() => s().passage !== null);
  const shown = s().passage;
  c.setQuery("gamma 3");                                        // capítulo de 250 versículos
  await tick(20);
  await until(() => s().message !== null);
  assert.match(s().message, /250 versículos/);
  assert.equal(s().passage, shown);
});

test("H4: volver a una selección ya paginada usa la memoria (ida y vuelta sin medir)", async () => {
  const { counter, createFit } = countingFit();
  const { c, s } = await setup({ controller: { createFit, previewDelayMs: 10 } });
  await typeQuery(c, s, BIG);
  await until(() => s().built !== null);
  c.moveSelection(1);
  await until(() => s().built !== null && s().built.reference === "Gamma 2:2");
  const measured = counter.calls;
  c.moveSelection(-1);
  assert.equal(s().built.reference, "Gamma 2:1", "ya estaba calculada: aparece al instante");
  c.moveSelection(1);
  assert.equal(s().built.reference, "Gamma 2:2");
  assert.equal(counter.calls, measured);
});

test("refreshBibles vuelve a pedir la lista (tras importar una Biblia) y conserva la Biblia activa", async () => {
  const { backend, c, s } = await setup();
  assert.equal(s().bibles.length, 1);
  backend.state.bibles.push({ id: 2, abbreviation: "NUE", name: "Biblia nueva sintética", language: "es", attribution: "", numberingProfile: null, books: 1, verses: 3 });
  await c.refreshBibles();
  assert.deepEqual(s().bibles.map((b) => b.abbreviation), ["SIN", "NUE"]);
  assert.equal(s().activeBible, "SIN", "no cambia la Biblia que se estaba usando");
});