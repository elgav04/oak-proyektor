import assert from "node:assert/strict";
import test, { after } from "node:test";
import { closeServer, loadTs } from "./helper.mjs";

// Funciones puras del modo EDITAR: orden mostrado, arrastre, fechas, pasaje de la Biblia y mensajes. Datos sintéticos.
const { orderedItems, movedOrder, dropIndex, addDays, nextSunday, suggestedCopyDate, passageForList, itemAsNew, describeEditError } = await loadTs("/src/control/lists/editModel.ts");
after(closeServer);

const it = (id, over = {}) => ({ id, itemType: "song", title: `Elemento ${id}`, songId: id, isMissing: false, bookNumber: null, chapter: null, verseStart: null, verseEnd: null, bibleAbbreviation: null, note: null, clearOnActivate: false, ...over });

test("el orden mostrado: el de la lista o el optimista; los ids que ya no están se omiten y los nuevos van al final", () => {
  const items = [it(1), it(2), it(3)];
  assert.deepEqual(orderedItems(items, null).map((i) => i.id), [1, 2, 3]);
  assert.deepEqual(orderedItems(items, [3, 1, 2]).map((i) => i.id), [3, 1, 2]);
  assert.deepEqual(orderedItems(items, [3, 99, 1]).map((i) => i.id), [3, 1, 2], "el 99 ya no existe; el 2 (nuevo para el orden local) va al final");
  assert.deepEqual(orderedItems([], [1]).map((i) => i.id), []);
  assert.deepEqual(orderedItems(items, [1, 3], [2]).map((i) => i.id), [1, 3], "un elemento recién quitado se esconde aunque la lista aún lo traiga");
  assert.deepEqual(orderedItems(items, null, [1]).map((i) => i.id), [2, 3]);
});

test("mover un id: acota el destino, no cambia nada si ya está ahí y nunca pierde ni repite elementos", () => {
  assert.deepEqual(movedOrder([1, 2, 3, 4], 1, 2), [2, 3, 1, 4]);
  assert.deepEqual(movedOrder([1, 2, 3, 4], 4, 0), [4, 1, 2, 3]);
  assert.deepEqual(movedOrder([1, 2, 3, 4], 2, 99), [1, 3, 4, 2], "acotado al final");
  assert.deepEqual(movedOrder([1, 2, 3, 4], 2, -5), [2, 1, 3, 4], "acotado al principio");
  assert.deepEqual(movedOrder([1, 2, 3], 2, 1), [1, 2, 3]);
  assert.deepEqual(movedOrder([1, 2, 3], 99, 0), [1, 2, 3], "un id desconocido no cambia nada");
  for (let from = 1; from <= 5; from++) for (let to = 0; to < 5; to++) assert.deepEqual([...movedOrder([1, 2, 3, 4, 5], from, to)].sort(), [1, 2, 3, 4, 5]);
});

test("el destino de un arrastre es la posición del elemento sobre el que se suelta; soltar sobre sí mismo o sobre algo desconocido no hace nada", () => {
  assert.equal(dropIndex([10, 20, 30], 10, 30), 2);
  assert.equal(dropIndex([10, 20, 30], 30, 10), 0);
  assert.equal(dropIndex([10, 20, 30], 20, 20), null);
  assert.equal(dropIndex([10, 20, 30], 20, 99), null);
  assert.equal(dropIndex([10, 20, 30], 99, 20), null);
});

test("fechas: una semana después de la original; sin fecha (plantilla) el próximo domingo; sin que el horario de verano las mueva", () => {
  assert.equal(addDays("2026-11-29", 7), "2026-12-06");
  assert.equal(addDays("2026-12-28", 7), "2027-01-04", "cambio de año");
  assert.equal(addDays("2028-02-25", 7), "2028-03-03", "año bisiesto");
  assert.equal(addDays("2026-03-28", 2), "2026-03-30", "alrededor del cambio de hora de marzo");
  assert.equal(nextSunday("2026-11-25"), "2026-11-29", "un miércoles → el domingo siguiente");
  assert.equal(nextSunday("2026-11-29"), "2026-12-06", "un domingo → el de la semana que viene");
  assert.equal(nextSunday("2026-11-28"), "2026-11-29");
  assert.equal(suggestedCopyDate("2026-11-22", "2026-11-25"), "2026-11-29");
  assert.equal(suggestedCopyDate(null, "2026-11-25"), "2026-11-29");
});

const verse = (n, over = {}) => ({ bookNumber: 43, bookName: "Libro sintético", chapter: 3, verseNumber: n, text: `texto sintético ${n}`, ...over });
const passage = { bible: "TST", verses: [verse(1), verse(2), verse(3), verse(4)] };

test("el pasaje de la Biblia como elemento: referencia canónica, rango contiguo y título solo con la referencia", () => {
  const one = passageForList(passage, 1, 1, "TST");
  assert.equal(one.ok, true);
  assert.deepEqual(one.item, { itemType: "passage", songId: null, bookNumber: 43, chapter: 3, verseStart: 2, verseEnd: null, bibleAbbreviation: "TST", title: "Libro sintético 3:2 · TST", note: null, clearOnActivate: false });
  const range = passageForList(passage, 3, 1, "TST");   // anclaje y activo en cualquier orden
  assert.equal(range.ok, true);
  assert.deepEqual([range.item.verseStart, range.item.verseEnd], [2, 4]);
  assert.equal(range.label, "Libro sintético 3:2-4 · TST");
  assert.ok(!JSON.stringify(range.item).includes("texto sintético"), "el elemento no lleva texto bíblico");
});

test("un pasaje que cruza de capítulo o ausente no se añade y dice por qué", () => {
  const crossing = { bible: "TST", verses: [verse(30), verse(1, { chapter: 4 })] };
  const r = passageForList(crossing, 0, 1, "TST");
  assert.equal(r.ok, false);
  assert.match(r.message, /cruza de capítulo/);
  assert.equal(passageForList(null, 0, 0, "TST").ok, false);
  assert.equal(passageForList({ bible: "TST", verses: [] }, 0, 0, "TST").ok, false);
});

test("lo que se quitó se puede volver a poner: canción, pasaje y nota; una canción faltante no", () => {
  assert.deepEqual(itemAsNew(it(5)), { itemType: "song", songId: 5, bookNumber: null, chapter: null, verseStart: null, verseEnd: null, bibleAbbreviation: null, title: null, note: null, clearOnActivate: false });
  assert.equal(itemAsNew(it(5, { isMissing: true })), null);
  const passageItem = itemAsNew(it(6, { itemType: "passage", songId: null, bookNumber: 43, chapter: 3, verseStart: 1, verseEnd: 2, bibleAbbreviation: "TST" }));
  assert.deepEqual([passageItem.itemType, passageItem.bookNumber, passageItem.verseEnd, passageItem.bibleAbbreviation], ["passage", 43, 2, "TST"]);
  const note = itemAsNew(it(7, { itemType: "note", songId: null, note: "recordatorio", clearOnActivate: true }));
  assert.deepEqual([note.itemType, note.title, note.note, note.clearOnActivate], ["note", "Elemento 7", "recordatorio", true]);
});

test("los errores del backend se dicen en español: un conflicto explica qué pasó y que lo escrito sigue ahí", () => {
  const conflict = describeEditError("playlist.conflict", "x");
  assert.equal(conflict.kind, "conflict");
  assert.match(conflict.text, /NO se aplicó/);
  assert.match(conflict.text, /lo que estabas escribiendo sigue aquí/);
  assert.equal(describeEditError("playlist.itemNotFound", "x").kind, "conflict");
  assert.match(describeEditError("playlist.busy", "x").text, /ocupada/);
  assert.match(describeEditError("timeout", "x").text, /no confirmó/);
  assert.equal(describeEditError("playlist.full", "La lista ya tiene 100 elementos.").text, "La lista ya tiene 100 elementos.");
  assert.equal(describeEditError("otra", "mensaje del backend").text, "mensaje del backend");
});
