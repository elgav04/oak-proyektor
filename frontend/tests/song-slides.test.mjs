import assert from "node:assert/strict";
import test, { after } from "node:test";
import { closeServer, loadTs } from "./helper.mjs";

const { splitChunks, paginateChunk, deriveSlides, uniqueChunks, warmChunks } = await loadTs("/src/control/songs/slides.ts");
const { windowRange } = await loadTs("/src/control/songs/windowRange.ts");
after(closeServer);

// Medición SIMULADA (happy-dom no mide): cabe si tiene como mucho `maxLines` renglones de `width` caracteres (una línea larga ocupa varios renglones).
const makeFits = (maxLines = 4, width = 40) => {
  const fits = (text) => text.split("\n").reduce((n, l) => n + Math.max(1, Math.ceil(l.length / width)), 0) <= maxLines;
  fits.calls = 0;
  const counted = (text) => { counted.calls++; return fits(text); };
  counted.calls = 0;
  return counted;
};
const flat = (pages) => pages.join("\n").split(/\s+/).filter(Boolean).join(" ");
const L = (n) => `linea sintetica numero ${n}`;

test("los cortes manuales (línea en blanco) separan los trozos y nunca se unen", () => {
  assert.deepEqual(splitChunks("a\nb\n\nc\n\n\nd"), ["a\nb", "c", "d"]);
  assert.deepEqual(splitChunks("\n\nsolo\n\n"), ["solo"]);
  assert.deepEqual(splitChunks(""), []);
  assert.deepEqual(splitChunks("a\r\n\r\nb"), ["a", "b"]);
});

test("un trozo que cabe es una sola diapositiva y uno que no cabe se corta por LÍNEAS", () => {
  const fits = makeFits(4);
  assert.deepEqual(paginateChunk([L(1), L(2)].join("\n"), fits), [[L(1), L(2)].join("\n")]);
  const pages = paginateChunk([1, 2, 3, 4, 5, 6, 7, 8, 9].map(L).join("\n"), makeFits(4));
  assert.ok(pages.length >= 3);
  for (const p of pages) assert.ok(!p.includes("\n\n") && p.split("\n").every((l) => l.startsWith("linea sintetica")), "cada página son líneas enteras");
  assert.equal(flat(pages), flat([1, 2, 3, 4, 5, 6, 7, 8, 9].map(L)), "no se pierde ni se reordena texto");
});

test("el corte automático se REPARTE por igual (3+2 en vez de 4+1)", () => {
  const pages = paginateChunk([1, 2, 3, 4, 5].map(L).join("\n"), makeFits(4));
  assert.deepEqual(pages.map((p) => p.split("\n").length), [3, 2]);
  const nine = paginateChunk([1, 2, 3, 4, 5, 6, 7, 8, 9].map(L).join("\n"), makeFits(4));
  assert.deepEqual(nine.map((p) => p.split("\n").length), [3, 3, 3]);
});

test("una línea que no cabe sola se divide por palabra y no se pierde texto", () => {
  const long = Array.from({ length: 30 }, (_, i) => `palabra${i}`).join(" ");
  const pages = paginateChunk(`${L(1)}\n${long}\n${L(2)}`, makeFits(3, 40));
  assert.ok(pages.length >= 3);
  assert.equal(flat(pages), flat([L(1), long, L(2)]));
});

test("sin hueco para ni un carácter se avanza igualmente (nunca se cuelga) y respeta el límite de 4000 caracteres por bloque", () => {
  const tiny = (text) => text.length <= 1;
  const pages = paginateChunk("abc\ndef", tiny);
  assert.equal(pages.join("").replace(/\s/g, ""), "abcdef");
  const huge = "x".repeat(9000);
  const split = paginateChunk(huge, () => true);   // «cabe» siempre, pero el backend rechaza bloques de más de 4000
  assert.ok(split.every((p) => p.length <= 4000));
  assert.equal(split.join(""), huge);
});

test("derivación: orden con repeticiones, límites de sección/manual/automático y claves estables", () => {
  const sources = [
    { uid: "v1", label: "Verso 1", text: [1, 2, 3, 4, 5].map(L).join("\n") },
    { uid: "c", label: "Coro", text: `${L(11)}\n${L(12)}\n\n${L(13)}` },
  ];
  const fits = makeFits(4);
  const cache = new Map();
  const pagesOf = (chunk) => { if (!cache.has(chunk)) cache.set(chunk, paginateChunk(chunk, fits)); return cache.get(chunk); };
  const slides = deriveSlides(sources, [0, 1, 0, 1, 1], pagesOf);

  assert.deepEqual(slides.map((s) => s.sectionLabel), ["Verso 1", "Verso 1", "Coro", "Coro", "Verso 1", "Verso 1", "Coro", "Coro", "Coro", "Coro"]);
  assert.deepEqual(slides.slice(0, 4).map((s) => s.boundary), ["section", "auto", "section", "manual"], "primera de la sección / continuación automática / corte manual");
  assert.deepEqual(slides.slice(0, 4).map((s) => s.key), ["0:0:0", "0:0:1", "1:0:0", "1:1:0"]);
  assert.equal(new Set(slides.map((s) => s.key)).size, slides.length, "claves únicas aunque el coro se repita");
  assert.equal(slides[2].text, slides[6].text, "el coro repetido es el MISMO texto (una sola sección)");
  // El coro repetido tres veces se midió UNA vez (la memoria es por trozo).
  assert.equal(cache.size, 3);
});

test("uniqueChunks da cada trozo una vez aunque el orden repita la sección", () => {
  const sources = [{ uid: "a", label: "A", text: "uno\n\ndos" }, { uid: "b", label: "B", text: "dos\n\ntres" }];
  assert.deepEqual(uniqueChunks(sources, [0, 1, 0, 0]), ["uno", "dos", "tres"]);
});

test("propiedad: para textos aleatorios nada se pierde, cada página cabe y no hay páginas vacías", () => {
  let seed = 7;
  const rand = () => { seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0; return seed / 4294967296; };
  for (let n = 0; n < 300; n++) {
    const lines = Array.from({ length: 1 + Math.floor(rand() * 12) }, (_, i) => Array.from({ length: 1 + Math.floor(rand() * 14) }, (_, k) => `p${i}${k}`).join(" "));
    const maxLines = 2 + Math.floor(rand() * 5);
    const fits = makeFits(maxLines, 30);
    const pages = paginateChunk(lines.join("\n"), fits);
    assert.equal(flat(pages), flat(lines), `caso ${n}: se perdió o se reordenó texto`);
    assert.ok(pages.every((p) => p.trim().length > 0), `caso ${n}: página vacía`);
    // Salvo una palabra que ni sola cabe, cada página cabe.
    assert.ok(pages.every((p) => makeFits(maxLines, 30)(p) || p.split(/\s+/).length === 1), `caso ${n}: una página no cabe`);
  }
});

test("calentar la memoria cede el hilo en tandas y se puede cancelar", async () => {
  let clock = 0;
  let yields = 0;
  const chunks = Array.from({ length: 50 }, (_, i) => `trozo ${i}`);
  const measured = [];
  const done = await warmChunks(chunks, (c) => { measured.push(c); clock += 5; }, {
    budgetMs: 12, now: () => clock, yieldNow: async () => { yields++; }, cancelled: () => false,
  });
  assert.equal(done, true);
  assert.equal(measured.length, 50);
  assert.ok(yields >= 15 && yields <= 25, `debió ceder el hilo cada ~12 ms de trabajo (cedió ${yields} veces)`);

  let cancelAfter = 0;
  const stopped = await warmChunks(chunks, () => { cancelAfter++; }, { budgetMs: 12, now: () => 0, yieldNow: async () => undefined, cancelled: () => cancelAfter >= 10 });
  assert.equal(stopped, false);
  assert.equal(cancelAfter, 10);
});

test("lista virtualizada: solo las filas visibles más un margen", () => {
  assert.deepEqual(windowRange(0, 400, 50, 5000), { start: 0, end: 14 });
  assert.deepEqual(windowRange(10_000, 400, 50, 5000, 2), { start: 198, end: 210 });
  assert.deepEqual(windowRange(0, 0, 50, 5000), { start: 0, end: 40 }, "sin layout: un primer bloque razonable");
  assert.deepEqual(windowRange(0, 400, 50, 0), { start: 0, end: 0 });
  const w = windowRange(999_999_999, 400, 50, 100);
  assert.ok(w.start <= w.end && w.end <= 100);
});
