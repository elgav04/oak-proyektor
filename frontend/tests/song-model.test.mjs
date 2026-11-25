import assert from "node:assert/strict";
import test, { after } from "node:test";
import { closeServer, loadTs } from "./helper.mjs";

const m = await loadTs("/src/control/songs/textModel.ts");
after(closeServer);

let n = 0;
const uid = () => `n${++n}`;
const sec = (u, label, text = "texto", serverId = null) => ({ uid: u, serverId, kind: "other", label, text });
const dto = (label, text = "texto") => ({ id: null, kind: "other", label, text });
const entry = (s) => ({ uid: s.uid, label: s.label });

test("la clave de una etiqueta ignora mayúsculas, tildes, espacios y guiones (como el backend)", () => {
  assert.equal(m.labelKey("Pre-coro 2"), "precoro2");
  assert.equal(m.labelKey("INTRODUCCIÓN"), "introduccion");
  assert.equal(m.labelKey("  Verso   1 "), m.labelKey("verso1"));
});

test("el texto del editor lleva cada sección UNA vez y sin línea «Orden:»", () => {
  assert.equal(m.formatSectionsText([{ label: "Verso 1", text: "a\nb" }, { label: "Coro", text: "c" }]), "[Verso 1]\na\nb\n\n[Coro]\nc");
  assert.equal(m.formatSectionsText([]), "");
});

test("reconciliar: por etiqueta se conserva la identidad y el id del servidor", () => {
  const before = [sec("a", "Verso 1", "x", 11), sec("b", "Coro", "y", 12)];
  const after = m.reconcileSections([dto("Coro", "y2"), dto("Verso 1", "x2")], before, uid);
  assert.deepEqual(after.map((s) => [s.uid, s.serverId, s.text]), [["b", 12, "y2"], ["a", 11, "x2"]]);
});

test("reconciliar: al renombrar UNA etiqueta se empareja por posición y el orden no se rompe", () => {
  const before = [sec("a", "Verso 1", "x", 11), sec("b", "Coro", "y", 12), sec("c", "Puente", "z", 13)];
  const after = m.reconcileSections([dto("Verso 1"), dto("Estribillo"), dto("Puente")], before, uid);
  assert.deepEqual(after.map((s) => [s.uid, s.serverId, s.label]), [["a", 11, "Verso 1"], ["b", 12, "Estribillo"], ["c", 13, "Puente"]]);
});

test("reconciliar: una sección nueva recibe identidad nueva y sin id de servidor", () => {
  const before = [sec("a", "Verso 1", "x", 11)];
  const after = m.reconcileSections([dto("Verso 1"), dto("Puente")], before, uid);
  assert.equal(after[0].uid, "a");
  assert.equal(after[1].serverId, null);
  assert.notEqual(after[1].uid, "a");
});

test("reconciliar: si desaparece una y se renombra otra a la vez no se adivina (identidad nueva)", () => {
  const before = [sec("a", "Verso 1", "x", 11), sec("b", "Coro", "y", 12), sec("c", "Puente", "z", 13)];
  const after = m.reconcileSections([dto("Verso 1"), dto("Final")], before, uid);
  assert.equal(after[0].uid, "a");
  assert.equal(after[1].serverId, null, "«Final» no se empareja con ninguna de las dos que quedaron sin pareja");
});

test("el orden NATURAL sigue siendo natural: una sección nueva entra donde está en el texto", () => {
  const before = [sec("a", "Verso 1"), sec("b", "Coro")];
  const order = m.naturalOrder(before);
  const after = [before[0], sec("p", "Puente"), before[1]];
  assert.deepEqual(m.reconcileOrder(order, before, after).map((e) => e.uid), ["a", "p", "b"]);
});

test("un orden con repeticiones conserva cada elemento y marca como SIN RESOLVER el de una sección que desapareció", () => {
  const before = [sec("a", "Verso 1"), sec("b", "Coro")];
  const order = [entry(before[0]), entry(before[1]), entry(before[0]), entry(before[1])];
  assert.equal(m.isNaturalOrder(order, before), false);

  const after = [before[0]];                                        // el coro se borró del texto
  const reconciled = m.reconcileOrder(order, before, after);
  assert.deepEqual(m.unresolvedOrder(reconciled, after), [1, 3]);
  assert.equal(m.orderIndexes(reconciled, after), null, "no se puede guardar con referencias sin resolver");
});

test("una referencia sin resolver se reengancha sola si el texto vuelve a tener esa etiqueta (p. ej. al borrar y reescribir un corchete)", () => {
  const before = [sec("a", "Verso 1"), sec("b", "Coro")];
  const order = [entry(before[0]), entry(before[1]), entry(before[1])];
  const mid = m.reconcileOrder(order, before, [before[0]]);                     // «[Coro» a medio escribir: la sección desaparece
  const back = [before[0], sec("zzz", "Coro")];                                  // …y vuelve con identidad nueva
  const relinked = m.reconcileOrder(mid, [before[0]], back);
  assert.deepEqual(relinked.map((e) => e.uid), ["a", "zzz", "zzz"]);
  assert.deepEqual(m.orderIndexes(relinked, back), [0, 1, 1]);
});

test("renombrar una sección actualiza la etiqueta de TODAS sus apariciones en el orden", () => {
  const before = [sec("a", "Coro")];
  const order = [entry(before[0]), entry(before[0])];
  const after = [sec("a", "Estribillo")];
  assert.deepEqual(m.reconcileOrder(order, before, after).map((e) => e.label), ["Estribillo", "Estribillo"]);
});

test("secciones sin usar y cuántas veces aparece cada una", () => {
  const sections = [sec("a", "Verso 1"), sec("b", "Coro"), sec("c", "Puente")];
  const order = [{ uid: "a", label: "Verso 1" }, { uid: "b", label: "Coro" }, { uid: "b", label: "Coro" }];
  assert.deepEqual(m.unusedSections(order, sections).map((s) => s.label), ["Puente"]);
  assert.equal(m.occurrences("b", order), 2);
  assert.equal(m.occurrences("c", order), 0);
});

const song = (over = {}) => ({
  title: "Canción", author: "", copyright: "", ccli: "", notes: "", background: null,
  sections: [sec("a", "Verso 1", "uno", 1), sec("b", "Coro", "dos", 2)], order: [], ...over,
});

test("«sin guardar»: la huella cambia con cualquier dato editable y no con lo que no cuenta", () => {
  const base = song({ order: [{ uid: "a", label: "Verso 1" }, { uid: "b", label: "Coro" }] });
  const print = m.fingerprint(base);
  assert.equal(m.fingerprint({ ...base }), print);
  for (const changed of [
    { title: "Otra" }, { author: "x" }, { copyright: "x" }, { ccli: "1" }, { notes: "n" },
    { background: { kind: "color", path: null, color: "#112233", muted: true } },
    { sections: [sec("a", "Verso 1", "UNO", 1), base.sections[1]] },
    { sections: [sec("a", "Estrofa", "uno", 1), base.sections[1]] },
    { order: [{ uid: "b", label: "Coro" }, { uid: "a", label: "Verso 1" }] },
    { order: [...base.order, { uid: "b", label: "Coro" }] },
  ]) assert.notEqual(m.fingerprint({ ...base, ...changed }), print, JSON.stringify(changed));
  // La identidad de la vista (uid) y el id del servidor NO cuentan: solo el contenido.
  assert.equal(m.fingerprint({ ...base, sections: base.sections.map((s) => ({ ...s, uid: s.uid + "x", serverId: null })), order: base.order.map((e) => ({ ...e, uid: e.uid + "x" })) }), print);
});

test("el botón de guardar dice cuántas veces se repite la sección EDITADA («Coro ×3»)", () => {
  const baseline = [sec("a", "Verso 1", "uno", 1), sec("b", "Coro", "dos", 2)];
  const order = [{ uid: "a", label: "Verso 1" }, { uid: "b", label: "Coro" }, { uid: "a", label: "Verso 1" }, { uid: "b", label: "Coro" }, { uid: "b", label: "Coro" }];

  // Sin cambios: nada que avisar.
  assert.deepEqual(m.repeatedEdits(baseline, order, baseline), []);
  assert.equal(m.saveButtonLabel(false, []), "Guardar en la canción");

  // Se edita el coro (aparece 3 veces) y el verso 1 (2 veces).
  const edited = [sec("a", "Verso 1", "uno editado", 1), sec("b", "Coro", "dos editado", 2)];
  const repeated = m.repeatedEdits(edited, order, baseline);
  assert.deepEqual(repeated, [{ label: "Verso 1", count: 2 }, { label: "Coro", count: 3 }]);
  assert.equal(m.saveButtonLabel(false, repeated), "Guardar en la canción (Verso 1 ×2, Coro ×3)");

  // Solo el coro.
  assert.equal(m.saveButtonLabel(false, m.repeatedEdits([baseline[0], sec("b", "Coro", "otro", 2)], order, baseline)), "Guardar en la canción (Coro ×3)");
  // Una sección editada que NO se repite no se menciona.
  const single = [sec("a", "Verso 1", "uno", 1), sec("b", "Coro", "dos", 2), sec("p", "Puente", "nuevo", null)];
  assert.deepEqual(m.repeatedEdits(single, [...order, { uid: "p", label: "Puente" }], baseline), []);
  // Renombrar también es editar.
  assert.deepEqual(m.repeatedEdits([baseline[0], sec("b", "Estribillo", "dos", 2)], order, baseline), [{ label: "Estribillo", count: 3 }]);
  // Canción nueva y más de tres secciones.
  assert.equal(m.saveButtonLabel(true, []), "Crear la canción");
  assert.equal(m.saveButtonLabel(false, [1, 2, 3, 4].map((i) => ({ label: `S${i}`, count: 2 }))), "Guardar en la canción (S1 ×2, S2 ×2, S3 ×2, …)");
});

test("el cuerpo de song.save lleva los ids del servidor, el orden como posiciones y vacíos como null", () => {
  const sections = [sec("a", "Verso 1", "uno", 5), sec("b", "Coro", "dos", null)];
  const order = [{ uid: "a", label: "Verso 1" }, { uid: "b", label: "Coro" }, { uid: "a", label: "Verso 1" }];
  const payload = m.buildSavePayload(9, 4, song({ sections, order, author: "  ", notes: "una nota" }), "save");
  assert.deepEqual(payload, {
    id: 9, expectedRevision: 4, title: "Canción", author: null, copyright: null, ccliNumber: null, notes: "una nota", background: null,
    sections: [{ id: 5, kind: "other", label: "Verso 1", text: "uno" }, { id: null, kind: "other", label: "Coro", text: "dos" }],
    arrangement: [0, 1, 0], reason: "save",
  });
  assert.equal(m.buildSavePayload(null, 4, song({ sections, order }), "import").expectedRevision, null, "una canción nueva no manda revisión");
  assert.equal(m.buildSavePayload(9, 4, song({ sections, order: [{ uid: "zz", label: "Huérfana" }] }), "save"), null, "con una referencia sin resolver no hay nada que guardar");
});
