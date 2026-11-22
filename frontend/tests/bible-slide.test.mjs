import assert from "node:assert/strict";
import test, { after } from "node:test";
import { closeServer, loadTs } from "./helper.mjs";

const { buildPassage, formatReference, projectionTheme, safeMinFontSize, themeWithFontSize, MAX_BLOCK_CHARS, MAX_TOTAL_CHARS } = await loadTs("/src/control/bible/slide.ts");
const { toSuperscript } = await loadTs("/src/control/bible/superscript.ts");
const { inkBleed, availableHeight, safetyMargin, SAFETY_LINES } = await loadTs("/src/control/bible/measure.ts");
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

test("el tema de proyección activa el ajuste automático como red de seguridad y el mínimo es SIEMPRE menor que el tamaño (H3)", () => {
  const t = projectionTheme({ ...BIBLE_DEFAULT_THEME, autoFit: false, fontSize: 30, minFontSize: 99 });
  assert.equal(t.autoFit, true);
  assert.equal(t.minFontSize, 24);   // floor(30 × 0,8): antes quedaba en 30 y la red de seguridad no podía reducir nada
  assert.equal(projectionTheme(BIBLE_DEFAULT_THEME).fontSize, 64);
});

test("H3: el mínimo de proyección deja margen de reducción para CUALQUIER tamaño y mínimo guardado (incluidos ajustes viejos con mínimo = tamaño)", () => {
  // el caso del hallazgo: tamaño 36 con el mínimo por defecto de 40, y el arrastre «mínimo = tamaño»
  assert.equal(safeMinFontSize(36, 40), 28);
  assert.equal(safeMinFontSize(64, 64), 51);
  assert.equal(safeMinFontSize(64, 40), 40);     // un mínimo menor que el 80 % del operador se respeta
  assert.equal(safeMinFontSize(400, 400), 320);
  assert.equal(safeMinFontSize(8, 8), 8);        // 8 es el mínimo permitido: no hay margen posible
  for (let size = 8; size <= 400; size++) {
    for (const wanted of [1, 8, 20, 40, size, size + 50, 999]) {
      const min = safeMinFontSize(size, wanted);
      assert.ok(min >= 8 && min <= size, `${size}/${wanted} → ${min}`);
      if (size >= 10) assert.ok(min < size, `sin margen de reducción: tamaño ${size}, mínimo ${min}`);
      if (size >= 10) assert.ok(min <= Math.floor(size * 0.8), `${size}/${wanted} → ${min}`);
      const projected = projectionTheme({ ...BIBLE_DEFAULT_THEME, fontSize: size, minFontSize: wanted });
      assert.equal(projected.minFontSize, min);
      assert.equal(projected.autoFit, true);
    }
  }
});

test("H3: bajar el tamaño en los ajustes no arrastra el mínimo hasta igualarlo (y subirlo lo recupera)", () => {
  let theme = { ...BIBLE_DEFAULT_THEME };                       // 64 con mínimo 40
  for (const size of [60, 50, 40, 30, 20, 12, 9, 8]) {
    theme = themeWithFontSize(theme, size, BIBLE_DEFAULT_THEME.minFontSize);
    assert.equal(theme.fontSize, size);
    assert.ok(theme.minFontSize <= theme.fontSize && theme.minFontSize >= 8, `el backend exige 8 ≤ mínimo ≤ tamaño: ${theme.minFontSize}/${size}`);
    if (size >= 10) assert.ok(theme.minFontSize < size, `mínimo ${theme.minFontSize} no es menor que ${size}`);
  }
  theme = themeWithFontSize(theme, 64, BIBLE_DEFAULT_THEME.minFontSize);
  assert.equal(theme.minFontSize, BIBLE_DEFAULT_THEME.minFontSize, "al volver a 64 el mínimo vuelve a ser el por defecto: no queda arrastrado hacia abajo");
});

test("H3: la paginación reserva ≈ 0,25 renglones de holgura de seguridad", () => {
  const none = { ...BIBLE_DEFAULT_THEME, outlineWidth: 0, shadowBlur: 0, shadowOffset: 0 };
  assert.equal(SAFETY_LINES, 0.25);
  assert.equal(safetyMargin(none), Math.ceil(0.25 * none.lineHeight * none.fontSize));
  assert.ok(safetyMargin(none) > 0 && safetyMargin(none) < none.lineHeight * none.fontSize, "menos de un renglón");
  assert.equal(availableHeight(none), 1080 - none.marginTop - none.marginBottom - safetyMargin(none));
  assert.ok(safetyMargin({ ...none, fontSize: 128 }) > safetyMargin(none), "crece con el tamaño de la letra");
});

test("el sangrado de la tinta (contorno + sombra) reduce el alto disponible solo si excede la holgura del interlineado", () => {
  const none = { ...BIBLE_DEFAULT_THEME, outlineWidth: 0, shadowBlur: 0, shadowOffset: 0 };
  assert.equal(inkBleed(none), 0);
  assert.equal(availableHeight(none), 1080 - none.marginTop - none.marginBottom - safetyMargin(none));
  const heavy = { ...none, outlineWidth: 8, shadowBlur: 24, shadowOffset: 8, lineHeight: 1.15, fontSize: 64 };
  assert.equal(inkBleed(heavy), Math.ceil(8 + 24 + 8 - 0.075 * 64));   // 40 - 4,8 = 35,2 → 36
  assert.ok(availableHeight(heavy) < availableHeight(none));
  // con mucho interlineado la holgura absorbe el sangrado
  assert.equal(inkBleed({ ...none, outlineWidth: 2, shadowBlur: 4, shadowOffset: 2, lineHeight: 2, fontSize: 64 }), 0);
});

// ───── H14: las páginas respetan los límites del backend (4000 por bloque, 20000 por slide) ─────

test("H14: con letra diminuta (todo «cabe» por altura) ninguna página supera los 4000 caracteres del backend y no se pierde texto", () => {
  assert.equal(MAX_BLOCK_CHARS, 4000);
  const verses = Array.from({ length: 100 }, (_, i) => v(1, i + 1, Array.from({ length: 60 }, (_, k) => `palabra${(i * 60 + k) % 97}`).join(" ") + "."));
  const total = verses.map((x) => x.text).join(" ").length;
  assert.ok(total > 8 * MAX_BLOCK_CHARS, "la prueba necesita mucho más texto que un bloque");
  const built = buildPassage(verses, opts({ superscript: false }), () => true);   // la altura nunca limita: solo la longitud
  assert.ok(built.pages.length >= 3);
  for (const [i, page] of built.pages.entries()) {
    assert.ok(page[0].length <= MAX_BLOCK_CHARS, `la página ${i} tiene ${page[0].length} caracteres`);
    assert.ok(page.reduce((n, b) => n + b.length, 0) <= MAX_TOTAL_CHARS);
  }
  const joined = built.pages.map((p) => p[0]).join(" ").replace(/\s+/g, " ");
  assert.equal(joined, verses.map((x) => x.text).join(" ").replace(/\s+/g, " "), "no se pierde ni se altera texto");
});

test("H14: un solo versículo enorme y sin espacios también se divide por longitud", () => {
  const built = buildPassage([v(1, 1, "z".repeat(9500))], opts({ superscript: false }), () => true);
  assert.ok(built.pages.length >= 3);
  for (const page of built.pages) assert.ok(page[0].length <= MAX_BLOCK_CHARS);
  assert.equal(built.pages.map((p) => p[0]).join("").length, 9500);
});

test("H14: el límite cuenta también con la referencia y el aviso de la última página (slide completo ≤ 20000)", () => {
  const attribution = "A".repeat(3900);   // el aviso admite hasta 4000 caracteres
  const built = buildPassage([v(1, 1, "palabra ".repeat(1500).trim())], opts({ superscript: false, showAttribution: true, attribution }), () => true);
  for (const page of built.pages) {
    assert.ok(page.every((b) => b.length <= MAX_BLOCK_CHARS), "ningún bloque supera el límite");
    assert.ok(page.reduce((n, b) => n + b.length, 0) <= MAX_TOTAL_CHARS);
  }
  assert.equal(built.pages.at(-1).includes(attribution), true);
});

test("H14: lo que cabe en pantalla y en 4000 caracteres no se parte de más", () => {
  const built = buildPassage([v(1, 1, "a".repeat(1000))], opts({ superscript: false }), () => true);
  assert.equal(built.pages.length, 1);
});