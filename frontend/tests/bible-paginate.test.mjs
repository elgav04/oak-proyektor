import assert from "node:assert/strict";
import test, { after } from "node:test";
import { closeServer, loadTs } from "./helper.mjs";

// La paginación es una función pura: aquí la «medición» es SIMULADA (capacidad en caracteres). Cuánto cabe de verdad lo comprueba la prueba de layout
// real (tools/bible-layout-test.ps1). Todo el texto es inventado.
const { paginate, splitToFit } = await loadTs("/src/control/bible/paginate.ts");
const { toSuperscript } = await loadTs("/src/control/bible/superscript.ts");
after(closeServer);

const sup = toSuperscript;
const strip = (t) => t.replace(/[⁰¹²³⁴-⁹]/g, "").replace(/\s+/g, "");
const cap = (n, attr = 0) => (text, last) => text.length + (last ? attr : 0) <= n;

function rng(seed) { let s = seed >>> 0; return () => { s = (Math.imul(s, 1664525) + 1013904223) >>> 0; return s / 4294967296; }; }
function words(rand, n) {
  const ws = [];
  for (let i = 0; i < n; i++) {
    let w = "";
    for (let k = 0, m = 1 + Math.floor(rand() * 4); k < m; k++) w += ["ba", "lu", "mo", "ren", "ti", "sa"][Math.floor(rand() * 6)];
    const r = rand();
    ws.push(r < 0.1 ? w + "." : r < 0.25 ? w + "," : w);
  }
  return ws.join(" ");
}

test("si todo cabe: una sola página con los versículos unidos en un párrafo", () => {
  const pages = paginate([`${sup(16)} Uno dos.`, `${sup(17)} Tres cuatro.`], cap(200));
  assert.deepEqual(pages, [`${sup(16)} Uno dos. ${sup(17)} Tres cuatro.`]);
});

test("varios versículos se agrupan por páginas SIN partir ningún versículo que quepa entero", () => {
  const verses = [1, 2, 3, 4, 5].map((n) => `${sup(n)} ${"palabra ".repeat(4).trim()}.`);   // 26 caracteres cada uno
  const pages = paginate(verses, cap(60));
  assert.ok(pages.length >= 2);
  for (const p of pages) assert.ok(p.length <= 60, p);
  // cada versículo aparece entero en alguna página
  for (const v of verses) assert.ok(pages.some((p) => p.includes(v)), `se partió ${v}`);
});

test("un versículo largo se divide en límite de FRASE antes que de palabra, y nunca se pierde texto", () => {
  const text = `${sup(1)} Primera frase de prueba aquí. Segunda frase de prueba allá. Tercera frase de prueba más. Cuarta frase final de prueba.`;
  const pages = paginate([text], cap(70));
  assert.ok(pages.length >= 2);
  assert.equal(strip(pages.join(" ")), strip(text));
  for (const p of pages.slice(0, -1)) assert.match(p, /[.]$/, `la página «${p}» no termina en final de frase`);
  for (const p of pages) assert.ok(p.length <= 70);
});

/** ¿Las páginas contienen exactamente las mismas PALABRAS enteras, en orden? Una palabra partida por la mitad produce una palabra de más y falla. */
const wordsIntact = (pages, text) => JSON.stringify(pages.flatMap((p) => p.split(" "))) === JSON.stringify(text.split(" "));

test("el comprobador de palabras enteras detecta una palabra partida (control negativo de la prueba de abajo)", () => {
  const text = "uno dos tres";
  assert.equal(wordsIntact(["uno dos", "tres"], text), true);
  assert.equal(wordsIntact(["uno d", "os tres"], text), false, "«dos» partida en «d» + «os»");
  assert.equal(wordsIntact(["uno dos tr", "es"], text), false);
});

test("sin final de frase cercano, corta en cláusula (coma) y si no en palabra; nunca a mitad de palabra si hay espacios", () => {
  const text = "uno dos tres, cuatro cinco seis, siete ocho nueve, diez once doce trece catorce quince dieciseis diecisiete";
  const pages = paginate([text], cap(40));
  assert.equal(pages.join(" "), text);
  for (const p of pages) assert.ok(p.length <= 40);
  // «nunca a mitad de palabra»: cada palabra del original aparece ENTERA en una página (ninguna se partió)
  assert.ok(wordsIntact(pages, text), `alguna palabra quedó partida: ${JSON.stringify(pages)}`);
  assert.ok(pages.slice(0, -1).some((p) => p.endsWith(",")), "debería preferir la coma como límite");
});

test("nunca parte una palabra con ninguna capacidad de página mientras la palabra más larga quepa", () => {
  const text = "uno dos tres, cuatro cinco seis, siete ocho nueve, diez once doce trece catorce quince dieciseis diecisiete. Otra frase mas, con varias palabras sueltas y algun punto.";
  const longest = Math.max(...text.split(" ").map((w) => w.length));
  for (let capacity = longest; capacity <= 90; capacity++) {
    const pages = paginate([text], cap(capacity));
    assert.ok(wordsIntact(pages, text), `capacidad ${capacity}: ${JSON.stringify(pages)}`);
    for (const p of pages) assert.ok(p.length <= capacity, `capacidad ${capacity}: página de ${p.length}`);
  }
});

test("una palabra más larga que la página se parte por caracteres (nunca se recorta ni se cuelga)", () => {
  const pages = paginate(["x".repeat(250)], cap(60));
  assert.equal(pages.join(""), "x".repeat(250));
  for (const p of pages) assert.ok(p.length <= 60);
  assert.equal(pages.length, 5);
});

test("el número de versículo en superíndice nunca queda solo al final de una página", () => {
  const pages = paginate([`${sup(163)} ${"alfa ".repeat(30).trim()}`], cap(37));
  for (const p of pages.slice(0, -1)) assert.doesNotMatch(p, /^[⁰¹²³⁴-⁹]+$/);
  assert.match(pages[0], /^[¹⁶³]+ alfa/);
});

test("la última página no queda con una sola palabra suelta: se reparte con la anterior", () => {
  // 9 palabras de 5 + espacio: sin reparto la 2.ª página tendría 1 palabra
  const text = "aaaaa bbbbb ccccc ddddd eeeee fffff ggggg hhhhh iiiii jjjjj kkkkk";
  const pages = paginate([text], cap(50));
  assert.equal(pages.length, 2);
  assert.ok(pages[1].length >= 15, `huérfana: «${pages[1]}»`);
  assert.equal(pages.join(" "), text);
});

test("el aviso de derechos solo va en la última página: si no cabe con él, esa página se divide otra vez", () => {
  const attr = 25;
  const text = `${sup(1)} ${"alfa beta gamma. ".repeat(4).trim()}`;
  const pages = paginate([text], cap(75, attr));
  assert.equal(strip(pages.join(" ")), strip(text));
  assert.ok(pages[pages.length - 1].length + attr <= 75, "la última página con el aviso no cabe");
  for (const p of pages.slice(0, -1)) assert.ok(p.length <= 75);
  // sin aviso cabría en una página menos o igual
  assert.ok(paginate([text], cap(75, 0)).length <= pages.length);
});

test("área minúscula (ni un carácter cabe): avanza de uno en uno en vez de colgarse", () => {
  const pages = paginate(["abc def"], () => false);
  assert.equal(pages.join("").replace(/\s/g, ""), "abcdef");
});

test("textos vacíos o en blanco no generan páginas", () => {
  assert.deepEqual(paginate([], cap(50)), []);
  assert.deepEqual(paginate(["   ", ""], cap(50)), []);
});

test("coste acotado: un versículo de 4000 caracteres (el máximo de un bloque) se pagina con pocas mediciones y en pocos ms (CPU pura, sin DOM)", () => {
  const text = words(rng(7), 1200).slice(0, 4000);
  let calls = 0;
  const t0 = performance.now();
  const pages = paginate([text], (t) => { calls++; return t.length <= 300; });
  const ms = performance.now() - t0;
  assert.ok(pages.length >= 12);
  assert.ok(calls < pages.length * 40, `${calls} mediciones para ${pages.length} páginas`);
  assert.ok(ms < 100, `${ms.toFixed(1)} ms de cálculo`);
});

test("PROPIEDAD: 400 textos y capacidades al azar → sin pérdida, todo cabe, sin páginas vacías", () => {
  const rand = rng(12345);
  for (let i = 0; i < 400; i++) {
    const verseCount = 1 + Math.floor(rand() * 5);
    const verses = Array.from({ length: verseCount }, (_, k) => `${sup(10 + k)} ${words(rand, 1 + Math.floor(rand() * 60))}${rand() < 0.2 ? " " + "q".repeat(20 + Math.floor(rand() * 80)) : ""}`);
    const capacity = 25 + Math.floor(rand() * 200);
    const attr = rand() < 0.4 ? Math.floor(rand() * 20) : 0;
    const pages = paginate(verses, cap(capacity, attr));
    assert.equal(strip(pages.join("")), strip(verses.join("")), `pérdida en el caso ${i}`);
    pages.forEach((p, idx) => {
      assert.ok(p.length > 0, `página vacía (caso ${i})`);
      assert.ok(p.length + (idx === pages.length - 1 ? attr : 0) <= capacity, `no cabe (caso ${i}, página ${idx}, ${p.length}/${capacity})`);
    });
  }
});

test("splitToFit: un texto que cabe se devuelve tal cual", () => {
  assert.deepEqual(splitToFit("hola mundo", (t) => t.length < 20), ["hola mundo"]);
});
