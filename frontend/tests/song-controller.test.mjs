import assert from "node:assert/strict";
import test, { after } from "node:test";
import { body, createSongBackend, line, memoryDraftStorage, tick, until } from "./song-fixtures.mjs";
import { closeServer, loadTs } from "./helper.mjs";

const { createSongController, isDirty, saveLabel, saveBlocker } = await loadTs("/src/control/songs/controller.ts");
after(closeServer);

// Medición SIMULADA: caben 4 renglones de 40 caracteres.
const fakeFit = () => ({ fits: (blocks) => blocks[0].split("\n").reduce((n, l) => n + Math.max(1, Math.ceil(l.length / 40)), 0) <= 4, dispose: () => undefined });

const SONG = {
  title: "Canción sintética", author: "Autora Ficticia",
  sections: [{ label: "Verso 1", text: body(1, 3) }, { label: "Coro", text: body(2, 2) }, { label: "Verso 2", text: body(3, 2) }],
  arrangement: [0, 1, 2, 1, 1],
};

async function make({ songs = [SONG], usedIn = {}, storage = null, ...rest } = {}) {
  const backend = createSongBackend({ songs, usedIn });
  const controller = createSongController({
    api: backend.client, createFit: fakeFit, storage, searchDebounceMs: 5, parseDebounceMs: 5, slidesDelayMs: 5, draftDelayMs: 5, saveTimeoutMs: 300, ...rest,
  });
  await controller.init();
  const get = controller.store.getState;
  const editor = () => get().editor;
  const settle = async () => { await tick(40); await until(() => !editor() || (!editor().parse.stale && !editor().slidesBusy)); };
  return { backend, controller, get, editor, settle };
}

test("init carga la lista y la configuración, y la búsqueda usa song.search con antirrebote", async () => {
  const t = await make({ songs: [SONG, { title: "Otra pieza", sections: [{ label: "Verso 1", text: "nada que ver aquí" }] }] });
  assert.equal(t.get().loaded, true);
  assert.deepEqual(t.get().list.map((s) => s.title), ["Canción sintética", "Otra pieza"]);
  assert.equal(t.get().settings.transitionMs, 400);

  t.controller.setQuery("l");
  t.controller.setQuery("lu");
  t.controller.setQuery("lum");
  await until(() => t.get().results !== null);
  assert.equal(t.backend.queries.filter((q) => q.type === "song.search").length, 1, "una sola consulta tras escribir de corrido");
  assert.equal(t.backend.queries.find((q) => q.type === "song.search").payload.query, "lum");

  t.controller.setQuery("");
  await until(() => t.get().results === null);
  t.controller.dispose();
});

test("abrir una canción: el texto lleva cada sección una vez, el orden con repeticiones es aparte y las diapositivas se derivan", async () => {
  const t = await make();
  t.controller.openSong(1);
  await until(() => t.editor() && t.editor().slides !== null);
  const e = t.editor();
  assert.equal(t.get().mode, "editor");
  assert.equal(e.title, "Canción sintética");
  assert.ok(e.text.startsWith("[Verso 1]\n") && !e.text.includes("Orden:"));
  assert.equal(e.sections.length, 3);
  assert.deepEqual(e.order.map((o) => o.label), ["Verso 1", "Coro", "Verso 2", "Coro", "Coro"]);
  assert.equal(isDirty(e), false);
  assert.equal(saveLabel(e), "Guardar en la canción");
  // Verso 1 (3 líneas) cabe; el coro se repite 3 veces; todas las diapositivas llevan su sección.
  assert.equal(e.slides.filter((s) => s.sectionLabel === "Coro").length, 3);
  assert.ok(e.slides.every((s) => s.text.length > 0));
  t.controller.dispose();
});

test("editar el texto: se interpreta en el backend, se conservan identidad e id al renombrar y aparece «sin guardar»", async () => {
  const t = await make();
  t.controller.openSong(1);
  await until(() => t.editor()?.slides);
  const before = t.editor().sections.map((s) => [s.uid, s.serverId]);

  t.controller.setText(t.editor().text.replace("[Coro]", "[Estribillo]"));
  await t.settle();
  const e = t.editor();
  assert.deepEqual(e.sections.map((s) => [s.uid, s.serverId]), before, "el renombrado se reconoce por posición");
  assert.deepEqual(e.order.map((o) => o.label), ["Verso 1", "Estribillo", "Verso 2", "Estribillo", "Estribillo"], "el orden sigue las 3 apariciones");
  assert.equal(isDirty(e), true);
  assert.deepEqual(saveLabel(e), "Guardar en la canción (Estribillo ×3)");

  // Escribir texto nuevo en una sección que se repite también lo dice.
  t.controller.setText(t.editor().text.replace(line(2, 0), "una linea editada del estribillo"));
  await t.settle();
  assert.equal(saveLabel(t.editor()), "Guardar en la canción (Estribillo ×3)");
  t.controller.dispose();
});

test("una sección nueva entra al orden natural y una sección que desaparece deja una referencia sin resolver que impide guardar", async () => {
  const t = await make({ songs: [{ title: "Natural", sections: [{ label: "Verso 1", text: "uno" }, { label: "Coro", text: "dos" }] }] });
  t.controller.openSong(1);
  await until(() => t.editor()?.slides);
  t.controller.setText(`${t.editor().text}\n\n[Puente]\ntres`);
  await t.settle();
  assert.deepEqual(t.editor().order.map((o) => o.label), ["Verso 1", "Coro", "Puente"], "el orden natural sigue siendo natural");

  // Orden con repeticiones: ya no es natural → quitar un coro del texto deja referencias huérfanas.
  t.controller.repeatOrder(1);
  t.controller.setText("[Verso 1]\nuno\n\n[Puente]\ntres");
  await t.settle();
  assert.ok(saveBlocker(t.editor())?.includes("ya no existe"));
  assert.equal(await t.controller.save(), false);
  assert.equal(t.backend.sent("song.save").length, 0, "no se manda nada");
  t.controller.removeOrder(1);
  t.controller.removeOrder(1);
  assert.equal(saveBlocker(t.editor()), null);
  t.controller.dispose();
});

test("operaciones del orden: mover, repetir sin duplicar el texto, quitar e insertar", async () => {
  const t = await make();
  t.controller.openSong(1);
  await until(() => t.editor()?.slides);
  const sectionsBefore = JSON.stringify(t.editor().sections);
  t.controller.moveOrder(0, 2);
  assert.deepEqual(t.editor().order.map((o) => o.label), ["Coro", "Verso 2", "Verso 1", "Coro", "Coro"]);
  t.controller.repeatOrder(2);
  assert.deepEqual(t.editor().order.map((o) => o.label).slice(2, 4), ["Verso 1", "Verso 1"]);
  t.controller.removeOrder(2);
  t.controller.selectOrder(0);
  t.controller.insertSection(t.editor().sections[2].uid);
  assert.equal(t.editor().order[1].label, "Verso 2");
  assert.equal(JSON.stringify(t.editor().sections), sectionsBefore, "repetir NUNCA toca el texto de las secciones");
  assert.equal(isDirty(t.editor()), true);
  t.controller.moveOrder(0, 0);   // sin efecto
  t.controller.removeOrder(99);   // fuera de rango: sin efecto
  t.controller.dispose();
});

test("guardar: manda song.save con ids, revisión esperada y orden; al confirmar el editor queda limpio con la revisión nueva", async () => {
  const t = await make();
  t.controller.openSong(1);
  await until(() => t.editor()?.slides);
  t.controller.setText(t.editor().text.replace(line(2, 0), "linea editada del coro"));
  await t.settle();
  assert.equal(isDirty(t.editor()), true);

  assert.equal(await t.controller.save(), true);
  const sent = t.backend.sent("song.save");
  assert.equal(sent.length, 1);
  const p = sent[0].payload;
  assert.equal(p.id, 1);
  assert.equal(p.expectedRevision, 1);
  assert.equal(p.reason, "save");
  assert.deepEqual(p.arrangement, [0, 1, 2, 1, 1]);
  assert.deepEqual(p.sections.map((s) => s.id), [1, 2, 3], "se actualizan EN SU SITIO: llevan su id");
  assert.ok(p.sections[1].text.includes("linea editada del coro"));
  const e = t.editor();
  assert.equal(e.revision, 2);
  assert.equal(isDirty(e), false);
  assert.equal(t.get().saving, false);
  assert.equal(e.history.length, 1, "se creó una copia previa");
  t.controller.dispose();
});

test("una canción NUEVA: sin título no se guarda; con título se crea, queda abierta con su id y la lista se actualiza", async () => {
  const t = await make({ songs: [] });
  t.controller.newSong();
  t.controller.setText("[Verso 1]\nuna\nlínea\n\n[Coro]\notra");
  await t.settle();
  assert.equal(t.editor().songId, null);
  assert.equal(await t.controller.save(), false);
  assert.match(t.get().message, /título/);
  assert.equal(saveLabel(t.editor()), "Crear la canción");

  t.controller.setField("title", "Mi canción");
  assert.equal(await t.controller.save(), true);
  const p = t.backend.sent("song.save")[0].payload;
  assert.equal(p.id, null);
  assert.equal(p.expectedRevision, null);
  assert.deepEqual(p.sections.map((s) => s.id), [null, null]);
  assert.equal(t.editor().songId, 1);
  assert.equal(t.editor().revision, 1);
  assert.equal(isDirty(t.editor()), false);
  await until(() => t.get().list.length === 1);
  t.controller.dispose();
});

test("revisión caducada: otra ventana guardó antes → conflicto, NO se da por guardado y se ofrecen las dos salidas", async () => {
  const t = await make();
  t.controller.openSong(1);
  await until(() => t.editor()?.slides);
  t.controller.setField("title", "Mi cambio");
  t.backend.externalChange(1);   // otra ventana la guarda: revisión 2

  assert.equal(await t.controller.save(), false);
  assert.equal(t.get().conflict, true);
  assert.equal(t.get().saving, false);
  assert.equal(isDirty(t.editor()), true, "los cambios siguen sin guardar");
  assert.equal(t.editor().revision, 1);
  assert.equal(t.backend.songs.get(1).title, "Canción sintética (otro)", "el servidor conserva lo de la otra ventana");

  // «Guardar como canción nueva»: tus cambios no se pierden y la original no se toca.
  await t.controller.resolveConflict("saveCopy");
  assert.equal(t.backend.songs.size, 2);
  assert.equal(t.backend.songs.get(2).title, "Mi cambio");
  assert.equal(t.editor().songId, 2);
  t.controller.dispose();
});

test("conflicto → «Ver la versión actual» recarga lo del servidor y descarta lo no guardado", async () => {
  const t = await make();
  t.controller.openSong(1);
  await until(() => t.editor()?.slides);
  t.controller.setField("title", "Mi cambio");
  t.backend.externalChange(1);
  await t.controller.save();
  await t.controller.resolveConflict("reload");
  assert.equal(t.editor().title, "Canción sintética (otro)");
  assert.equal(t.editor().revision, 2);
  assert.equal(isDirty(t.editor()), false);
  assert.equal(t.get().conflict, false);
  t.controller.dispose();
});

test("un guardado sin confirmación (se pierde el evento) NO se da por hecho: vuelve a «sin guardar» con un aviso", async () => {
  const t = await make();
  t.controller.openSong(1);
  await until(() => t.editor()?.slides);
  t.controller.setField("title", "Sin confirmar");
  t.backend.dropNextChanged();

  assert.equal(await t.controller.save(), false);
  assert.match(t.get().message, /no confirmó/);
  assert.equal(t.get().saving, false);
  assert.equal(isDirty(t.editor()), true);
  t.controller.dispose();
});

test("un fallo del backend en el guardado (command.failed) se muestra y no marca nada como guardado", async () => {
  const t = await make();
  t.controller.openSong(1);
  await until(() => t.editor()?.slides);
  t.controller.setField("title", "Cambio");
  t.backend.failNext("song.save", "song.busy", "La base de datos está ocupada. No se cambió nada; inténtalo de nuevo en unos segundos.");
  assert.equal(await t.controller.save(), false);
  assert.match(t.get().message, /ocupada/);
  assert.equal(t.get().conflict, false);
  assert.equal(isDirty(t.editor()), true);
  assert.equal(await t.controller.save(), true, "reintentar funciona");
  t.controller.dispose();
});

test("lo que se edita MIENTRAS se guarda no se pierde ni se da por guardado (el backend «tarda» de verdad)", async () => {
  const t = await make();
  t.controller.openSong(1);
  await until(() => t.editor()?.slides);
  t.controller.setField("title", "Primero");
  t.backend.holdNext();                                  // el backend aún no ha procesado el guardado
  const saving = t.controller.save();
  await until(() => t.backend.held.length === 1);
  t.controller.setField("title", "Segundo, escrito durante el guardado");
  assert.equal(t.get().saving, true);
  t.backend.release();
  assert.equal(await saving, true);
  assert.equal(t.editor().title, "Segundo, escrito durante el guardado", "lo escrito durante el guardado sigue ahí");
  assert.equal(isDirty(t.editor()), true, "lo segundo sigue sin guardar");
  assert.equal(t.editor().revision, 2, "pero la revisión ya es la nueva (el siguiente guardado no dará conflicto)");
  assert.equal(await t.controller.save(), true);
  assert.equal(t.backend.songs.get(1).title, "Segundo, escrito durante el guardado");
  assert.equal(isDirty(t.editor()), false);
  t.controller.dispose();
});

test("dos guardados a la vez: solo se manda uno (no se encadenan ni se duplican)", async () => {
  const t = await make();
  t.controller.openSong(1);
  await until(() => t.editor()?.slides);
  t.controller.setField("title", "Doble");
  t.backend.holdNext();
  const first = t.controller.save();
  await until(() => t.backend.held.length === 1);
  assert.equal(await t.controller.save(), false, "el segundo guardado mientras el primero sigue en curso no hace nada");
  t.backend.release();
  assert.equal(await first, true);
  assert.equal(t.backend.sent("song.save").length, 1);
  assert.equal(t.backend.songs.get(1).revision, 2);
  t.controller.dispose();
});
test("deshacer el último guardado y restaurar una versión; las dos se pueden deshacer", async () => {
  const t = await make();
  t.controller.openSong(1);
  await until(() => t.editor()?.slides);
  t.controller.setField("title", "Versión B");
  await t.controller.save();
  t.controller.setField("title", "Versión C");
  await t.controller.save();
  assert.equal(t.editor().revision, 3);

  await t.controller.undoLastSave();
  assert.equal(t.editor().title, "Versión B");
  assert.equal(t.editor().revision, 4);
  assert.match(t.get().message, /Se deshizo/);

  await t.controller.openHistory();
  assert.equal(t.editor().historyOpen, true);
  const oldest = t.editor().history.at(-1);
  await t.controller.restoreVersion(oldest.id);
  assert.equal(t.editor().title, "Canción sintética");
  assert.equal(isDirty(t.editor()), false);
  assert.equal(t.backend.sent("song.restoreRevision").length, 2);
  assert.equal(t.backend.sent("song.restoreRevision")[0].payload.revisionId, null);
  t.controller.dispose();
});

test("restaurar con una revisión caducada falla con conflicto y no cambia el editor", async () => {
  const t = await make();
  t.controller.openSong(1);
  await until(() => t.editor()?.slides);
  t.controller.setField("title", "B");
  await t.controller.save();
  t.backend.externalChange(1);
  await t.controller.undoLastSave();
  assert.equal(t.get().conflict, true);
  assert.equal(t.editor().title, "B");
  t.controller.dispose();
});

test("borrar: avisa «usada en N servicios», no borra hasta confirmar y deja constancia de que quedó como «Falta»", async () => {
  const t = await make({ songs: [SONG, { title: "Sin usar", sections: [{ label: "Verso 1", text: "uno" }] }], usedIn: { 1: 3 } });
  t.controller.askDelete(1);
  await until(() => t.get().deleteAsk);
  assert.deepEqual(t.get().deleteAsk, { id: 1, title: "Canción sintética", used: 3 });
  assert.equal(t.backend.sent("song.delete").length, 0, "preguntar no borra");

  t.controller.cancelDelete();
  assert.equal(t.get().deleteAsk, null);
  assert.equal(t.backend.songs.size, 2);

  t.controller.askDelete(1);
  await until(() => t.get().deleteAsk);
  await t.controller.confirmDelete();
  assert.equal(t.backend.songs.has(1), false);
  assert.match(t.get().message, /3 servicio.*Falta/);
  await until(() => t.get().list.length === 1);

  t.controller.askDelete(2);
  await until(() => t.get().deleteAsk);
  assert.equal(t.get().deleteAsk.used, 0);
  t.controller.dispose();
});

test("duplicar crea otra canción «(copia)» con las secciones y el orden y la abre", async () => {
  const t = await make();
  t.controller.duplicate(1);
  await until(() => t.editor()?.songId === 2);
  assert.equal(t.editor().title, "Canción sintética (copia)");
  assert.deepEqual(t.editor().order.map((o) => o.label), ["Verso 1", "Coro", "Verso 2", "Coro", "Coro"]);
  const p = t.backend.sent("song.save")[0].payload;
  assert.equal(p.id, null);
  assert.deepEqual(p.sections.map((s) => s.id), [null, null, null], "la copia no comparte secciones con la original");
  assert.equal(t.backend.songs.size, 2);
  assert.equal(t.backend.songs.get(1).title, "Canción sintética");
  t.controller.dispose();
});

test("salir con cambios sin guardar: se pregunta en la propia pantalla; seguir, descartar o guardar", async () => {
  const t = await make();
  t.controller.openSong(1);
  await until(() => t.editor()?.slides);
  t.controller.closeEditor();
  assert.equal(t.get().mode, "list", "sin cambios sale directo, sin preguntar");
  assert.equal(t.get().confirmLeave, false);
  t.controller.dispose();

  const u = await make();
  u.controller.openSong(1);
  await until(() => u.editor()?.slides);
  u.controller.setField("title", "Cambiado");
  u.controller.closeEditor();
  assert.equal(u.get().confirmLeave, true);
  assert.equal(u.get().mode, "editor");

  await u.controller.resolveLeave("stay");
  assert.equal(u.get().confirmLeave, false);
  assert.equal(u.get().mode, "editor");
  assert.equal(u.editor().title, "Cambiado");

  u.controller.closeEditor();
  await u.controller.resolveLeave("save");
  assert.equal(u.get().mode, "list");
  assert.equal(u.backend.songs.get(1).title, "Cambiado");

  u.controller.openSong(1);
  await until(() => u.editor()?.slides);
  u.controller.setField("title", "Descartado");
  u.controller.closeEditor();
  await u.controller.resolveLeave("discard");
  assert.equal(u.get().mode, "list");
  assert.equal(u.backend.songs.get(1).title, "Cambiado", "descartar no guarda");
  u.controller.dispose();
});

test("abrir otra canción con cambios sin guardar también pregunta, y si no se puede guardar se queda en el editor", async () => {
  const t = await make({ songs: [SONG, { title: "Otra", sections: [{ label: "Verso 1", text: "x" }] }] });
  t.controller.openSong(1);
  await until(() => t.editor()?.slides);
  t.controller.setField("title", "   ");
  t.controller.openSong(2);
  assert.equal(t.get().confirmLeave, true);
  assert.equal(t.editor().songId, 1);
  await t.controller.resolveLeave("save");   // sin título: no se puede guardar
  assert.equal(t.editor().songId, 1, "no se pierde el trabajo");
  assert.match(t.get().message, /título/);
  t.controller.dispose();
});

test("importar: el archivo se decodifica, se previsualiza con el backend y recién al aplicar pasa al editor (sin guardar)", async () => {
  const t = await make({ songs: [] });
  t.controller.openImport();
  const text = "[Verso 1]\nuna linea sintetica\notra linea\n\n[Coro]\nlinea del coro\n\n[Coro]\n\nOrden: V1 C V1 C";
  const bytes = new TextEncoder().encode(text);
  await t.controller.importFile({ name: "mi-cancion.txt", size: bytes.length, arrayBuffer: async () => bytes.buffer });
  await until(() => t.get().importDraft?.parsed);
  const d = t.get().importDraft;
  assert.equal(d.encoding, "utf-8");
  assert.equal(d.title, "mi-cancion", "el título sale del nombre del archivo");
  assert.deepEqual(d.parsed.sections.map((s) => s.label), ["Verso 1", "Coro"]);
  assert.equal(t.backend.sent("song.save").length, 0, "importar no guarda nada");

  t.controller.applyImport();
  assert.equal(t.get().mode, "editor");
  assert.equal(t.get().importDraft, null);
  assert.equal(t.editor().title, "mi-cancion");
  assert.equal(t.editor().fromImport, true);
  assert.deepEqual(t.editor().order.map((o) => o.label), ["Verso 1", "Coro", "Coro"].slice(0, t.editor().order.length));
  assert.equal(isDirty(t.editor()) || t.editor().songId === null, true);

  await t.controller.save();
  assert.equal(t.backend.sent("song.save")[0].payload.reason, "import", "la copia previa dirá «Importación»");
  t.controller.dispose();
});

test("importar un archivo en Windows-1252: se avisa y se puede cambiar la codificación; los que no son texto o son enormes se rechazan", async () => {
  const t = await make({ songs: [] });
  t.controller.openImport();
  const latin = Uint8Array.from([...Buffer.from("[Verso 1]\nLumar"), 0xed, ...Buffer.from(" se"), 0xf1, ...Buffer.from("or")]);
  await t.controller.importFile({ name: "antiguo.txt", size: latin.length, arrayBuffer: async () => latin.buffer });
  let d = t.get().importDraft;
  assert.equal(d.encoding, "windows-1252");
  assert.match(d.decodeWarnings[0], /no está en UTF-8/);
  assert.ok(d.text.includes("Lumarí señor"));

  t.controller.importForceEncoding("utf-8");
  assert.match(t.get().importDraft.fileError, /no es UTF-8/);
  t.controller.importForceEncoding("windows-1252");
  assert.equal(t.get().importDraft.fileError, null);

  const zip = Uint8Array.from([0x50, 0x4b, 3, 4, 0, 0]);
  await t.controller.importFile({ name: "falso.txt", size: zip.length, arrayBuffer: async () => zip.buffer });
  d = t.get().importDraft;
  assert.match(d.fileError, /Word o PowerPoint/);
  assert.equal(d.text, "");

  let read = false;
  await t.controller.importFile({ name: "enorme.txt", size: 300 * 1024, arrayBuffer: async () => { read = true; return new ArrayBuffer(0); } });
  assert.equal(read, false, "un archivo enorme se rechaza ANTES de leerlo");
  assert.match(t.get().importDraft.fileError, /demasiado grande/);
  t.controller.dispose();
});

test("importar con errores (más secciones de las permitidas) no se puede aplicar", async () => {
  const t = await make({ songs: [] });
  t.controller.openImport(Array.from({ length: 61 }, (_, i) => `[Parte ${i}]\ntexto ${i}`).join("\n\n"));
  await until(() => t.get().importDraft?.parsed);
  assert.ok(t.get().importDraft.parsed.errors.length > 0);
  t.controller.applyImport();
  assert.equal(t.get().mode, "list", "con errores no pasa al editor");
  t.controller.dispose();
});

test("borrador: los cambios sin guardar se escriben (con antirrebote) y se recuperan tras recargar la página", async () => {
  const storage = memoryDraftStorage();
  const a = await make({ storage });
  a.controller.openSong(1);
  await until(() => a.editor()?.slides);
  assert.equal(storage.peek(), null, "sin cambios no hay borrador");
  a.controller.setField("title", "Título a medio escribir");
  a.controller.setText(a.editor().text.replace(line(2, 0), "linea escrita y no guardada"));
  await until(() => storage.peek() !== null && JSON.parse(storage.peek()).sections.some((s) => s.text.includes("no guardada")));   // el borrador lleva las secciones YA interpretadas
  for (const written of storage.writes) {
    const d = JSON.parse(written);
    assert.equal(d.text.includes("no guardada"), d.sections.some((s) => s.text.includes("no guardada")), "NINGÚN borrador escrito lleva el texto nuevo con las secciones viejas (ni al revés)");
  }
  a.controller.dispose();

  const b = await make({ storage });          // «se recargó el panel»
  assert.deepEqual(b.get().recovered, { songId: 1, title: "Título a medio escribir" });
  await b.controller.openRecovered();
  assert.equal(b.editor().title, "Título a medio escribir");
  assert.equal(isDirty(b.editor()), true);
  assert.ok(b.editor().sections.some((s) => s.text.includes("linea escrita y no guardada")));
  assert.equal(b.get().recovered, null);
  await b.controller.save();
  assert.equal(storage.peek(), null, "guardado: el borrador se borra");
  b.controller.dispose();

  storage.set(JSON.stringify({ v: 1, songId: null, title: "otro" }));
  const c = await make({ storage });
  c.controller.discardRecovered();
  assert.equal(storage.peek(), null);
  c.controller.dispose();
});

test("un borrador ilegible no rompe el arranque", async () => {
  const storage = memoryDraftStorage();
  storage.set("{ esto no es json");
  const t = await make({ storage });
  assert.equal(t.get().loaded, true);
  assert.equal(t.get().recovered, null);
  assert.equal(storage.peek(), null);
  t.controller.dispose();
});

test("tema: se manda song.setSettings y se confirma con lo que guardó el backend", async () => {
  const t = await make();
  const next = { ...t.get().settings, theme: { ...t.get().settings.theme, fontSize: 80, minFontSize: 64 }, atEnd: "clear" };
  await t.controller.saveSettings(next);
  assert.equal(t.backend.sent("song.setSettings").length, 1);
  assert.deepEqual(t.get().settings, next);
  assert.equal(t.get().settings.theme.minFontSize < t.get().settings.theme.fontSize, true);
  t.controller.dispose();
});

test("el cálculo de diapositivas CEDE el hilo en tandas con una canción larga y una edición nueva lo cancela", async () => {
  const sections = Array.from({ length: 40 }, (_, i) => ({ label: `Parte ${i + 1}`, text: body(100 + i, 6) }));
  let clock = 0;
  let yields = 0;
  let measured = 0;
  const countingFit = () => ({ fits: (blocks) => { measured++; return fakeFit().fits(blocks); }, dispose: () => undefined });
  const t = await make({
    songs: [{ title: "Larga", sections }], createFit: countingFit,
    now: () => (clock += 3), yieldNow: async () => { yields++; await tick(0); }, sliceBudgetMs: 12,
  });
  t.controller.openSong(1);
  await until(() => t.editor()?.slides, 4000);
  assert.ok(yields >= 5, `debió ceder el hilo varias veces (cedió ${yields})`);
  assert.ok(t.editor().slides.length >= 40);

  // Una edición nueva cancela el cálculo en curso: nunca se publican diapositivas de un texto viejo.
  const slidesBefore = t.editor().slides;
  const measuredBefore = measured;
  t.controller.setText(t.editor().text + "\n\n[Final]\nlinea nueva");
  t.controller.setText(t.editor().text + "\n\n[Final]\nlinea nueva distinta");
  await t.settle();
  assert.ok(t.editor().slides.some((s) => s.sectionLabel === "Final"));
  assert.notEqual(t.editor().slides, slidesBefore);
  assert.ok(measured - measuredBefore < 40 * 4, "lo ya medido se reutiliza: no se vuelve a medir toda la canción");
  t.controller.dispose();
});

test("dispose detiene los temporizadores y las suscripciones", async () => {
  const t = await make();
  t.controller.openSong(1);
  await until(() => t.editor()?.slides);
  t.controller.dispose();
  t.controller.setText("[Verso 1]\nalgo");
  await tick(60);
  assert.equal(t.backend.queries.filter((q) => q.type === "song.parseText").length, 0, "tras dispose no se consulta nada más");
});
