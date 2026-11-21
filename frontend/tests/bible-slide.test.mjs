import assert from "node:assert/strict";
import test, { after } from "node:test";
import { closeServer, loadTs } from "./helper.mjs";

const { buildPassage, formatReference, projectionTheme } = await loadTs("/src/control/bible/slide.ts");
const { toSuperscript } = await loadTs("/src/control/bible/superscript.ts");
const { inkBleed, availableHeight } = await loadTs("/src/control/bible/measure.ts");
const { BIBLE_DEFAULT_THEME } = await loadTs("/src/control/bible/defaults.ts");
after(closeServer);

const v = (chapter, n, text = `texto inventado ${n}.`, book = { number: 1, name: "Alfa" }) => ({ bookNumber: book.number, bookName: book.name, chapter, verseNumber: n, text });
const opts = (over = {}) => ({ bibleName: "SIN", attribution: "", showAttribution: false, superscript: true, ...over });
const always = () => true;

test("superíndices Unicode", () => {
  assert.equal(toSuperscript(16), "¹⁶");
  assert.equal(toSuperscript("1230"), "¹²³⁰");
  assert.equal(toSuperscript(7), "⁷");
});

test("formatReference: un versículo, rango, no contiguos, varios capítulos y libros", () => {
  assert.equal(formatReference([v(3, 16)]), "Alfa 3:16");
  assert.equal(formatReference([v(3, 16), v(3, 17), v(3, 18)]), "Alfa 3:16-18");
  assert.equal(formatReference([v(3, 16), v(3, 18)]), "Alfa 3:16, 18");
  assert.equal(formatReference([v(1, 31), v(2, 1), v(2, 3)]), "Alfa 1:31-2:3");
  assert.equal(formatReference([v(1, 1), v(1, 2, "x", { number: 2, name: "Beta" })]), "Alfa 1:1; Beta 1:2");
  assert.equal(formatReference([]), "");
});

test("la página lleva el versículo con su número en superíndice, la referencia y el nombre corto de la Biblia", () => {
  const built = buildPassage([v(3, 16, "Hola mundo.")], opts({ bibleName: "RV60" }), always);
  assert.equal(built.reference, "Alfa 3:16");
  assert.deepEqual(built.pages, [["¹⁶ Hola mundo.", "Alfa 3:16 · RV60"]]);
});

test("sin superíndice: el texto va sin número", () => {
  const built = buildPassage([v(3, 16, "Hola mundo.")], opts({ superscript: false }), always);
  assert.equal(built.pages[0][0], "Hola mundo.");
});

test("la atribución VACÍA no muestra nada aunque la opción esté activada; con texto va solo en la última página", () => {
  assert.deepEqual(buildPassage([v(1, 1)], opts({ showAttribution: true, attribution: "" }), always).pages[0].length, 2);
  assert.deepEqual(buildPassage([v(1, 1)], opts({ showAttribution: true, attribution: "   " }), always).pages[0].length, 2);
  assert.equal(buildPassage([v(1, 1)], opts({ showAttribution: false, attribution: "Aviso inventado" }), always).pages[0].length, 2);

  const verses = Array.from({ length: 6 }, (_, i) => v(1, i + 1, "palabra ".repeat(6).trim() + "."));
  const fits = (blocks) => blocks.join(" ").length <= 140;
  const built = buildPassage(verses, opts({ showAttribution: true, attribution: "Aviso inventado de prueba" }), fits);
  assert.ok(built.pages.length >= 2);
  built.pages.forEach((p, i) => {
    const last = i === built.pages.length - 1;
    assert.equal(p.includes("Aviso inventado de prueba"), last, `atribución en la página ${i}`);
    assert.ok(fits(p), `la página ${i} (con sus bloques de referencia/aviso) no cabe`);
  });
});

test("todas las páginas llevan la referencia y la medición incluye la referencia (no solo el texto)", () => {
  const verses = [v(3, 16, "a".repeat(30)), v(3, 17, "b".repeat(30))];
  // cap 80: el texto de los dos (67 con números) cabría, pero con la referencia (18 más) no
  const fits = (blocks) => blocks.join("").length <= 80;
  const built = buildPassage(verses, opts(), fits);
  assert.equal(built.pages.length, 2);
  for (const p of built.pages) assert.match(p[p.length - 1], /Alfa 3:16-17 · SIN/);
});

test("un versículo largo se reparte en páginas y la referencia se repite; el número solo va en la primera", () => {
  const long = Array.from({ length: 10 }, (_, i) => `Frase número ${i + 1} de relleno inventado.`).join(" ");
  const built = buildPassage([v(1, 5, long)], opts(), (blocks) => blocks.join(" ").length <= 150);
  assert.ok(built.pages.length >= 3);
  assert.ok(built.pages[0][0].startsWith("⁵ "));
  for (const p of built.pages.slice(1)) assert.doesNotMatch(p[0], /^[⁰¹²³⁴-⁹]/);
  assert.equal(built.pages.map((p) => p[0]).join(" ").replace(/⁵ /, ""), long);
});

test("el tema de proyección activa el ajuste automático como red de seguridad y el mínimo nunca supera el tamaño", () => {
  const t = projectionTheme({ ...BIBLE_DEFAULT_THEME, autoFit: false, fontSize: 30, minFontSize: 99 });
  assert.equal(t.autoFit, true);
  assert.equal(t.minFontSize, 30);
  assert.equal(projectionTheme(BIBLE_DEFAULT_THEME).fontSize, 64);
});

test("el sangrado de la tinta (contorno + sombra) reduce el alto disponible solo si excede la holgura del interlineado", () => {
  const none = { ...BIBLE_DEFAULT_THEME, outlineWidth: 0, shadowBlur: 0, shadowOffset: 0 };
  assert.equal(inkBleed(none), 0);
  assert.equal(availableHeight(none), 1080 - none.marginTop - none.marginBottom);
  const heavy = { ...none, outlineWidth: 8, shadowBlur: 24, shadowOffset: 8, lineHeight: 1.15, fontSize: 64 };
  assert.equal(inkBleed(heavy), Math.ceil(8 + 24 + 8 - 0.075 * 64));   // 40 - 4,8 = 35,2 → 36
  assert.ok(availableHeight(heavy) < availableHeight(none));
  // con mucho interlineado la holgura absorbe el sangrado
  assert.equal(inkBleed({ ...none, outlineWidth: 2, shadowBlur: 4, shadowOffset: 2, lineHeight: 2, fontSize: 64 }), 0);
});
