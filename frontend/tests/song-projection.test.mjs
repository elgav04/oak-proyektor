import assert from "node:assert/strict";
import test, { after } from "node:test";
import { body, createSongBackend, line, tick, until } from "./song-fixtures.mjs";
import { closeServer, loadTs } from "./helper.mjs";

// Proyección de canciones: controlador real + cliente IPC real + backend falso (v12). Datos SINTÉTICOS.
const { createSongController } = await loadTs("/src/control/songs/controller.ts");
const { songLiveStatus, confirmedSlideIndex } = await loadTs("/src/control/songs/liveStatus.ts");
after(closeServer);

const fakeFit = () => ({ fits: (blocks) => blocks[0].split("\n").reduce((n, l) => n + Math.max(1, Math.ceil(l.length / 40)), 0) <= 4, dispose: () => undefined });
const SONG = {
  title: "Canción sintética", author: "Autora Ficticia",
  sections: [{ label: "Verso 1", text: body(1, 3) }, { label: "Coro", text: body(2, 2) }, { label: "Verso 2", text: body(3, 2) }],
  arrangement: [0, 1, 2, 1],   // V1 C V2 C → 4 diapositivas (cada sección cabe en una)
};
const BG = { kind: "video", path: "C:\\fondos\\sintetico.mp4", color: "#102030", muted: true };

async function make({ songs = [SONG], settings, ...rest } = {}) {
  const backend = createSongBackend({ songs, settings });
  const controller = createSongController({ api: backend.client, createFit: fakeFit, searchDebounceMs: 5, parseDebounceMs: 5, slidesDelayMs: 5, draftDelayMs: 5, saveTimeoutMs: 300, sendIntervalMs: 30, selectDelayMs: 10, ...rest });
  await controller.init();
  const get = controller.store.getState;
  const select = async (index = 0) => { controller.selectIndex(index); await until(() => get().projection?.slides != null); };
  const shows = () => backend.sent("output.showContent");
  return { backend, controller, get, select, shows };
}

test("seleccionar una canción carga su tira en el orden de reproducción (con repeticiones) SIN proyectar nada", async () => {
  const m = await make();
  await m.select();
  const p = m.get().projection;
  assert.deepEqual(p.slides.map((s) => s.sectionLabel), ["Verso 1", "Coro", "Verso 2", "Coro"]);
  assert.equal(p.slides[1].text, p.slides[3].text, "el coro repetido es el mismo texto");
  await tick(80);
  assert.equal(m.backend.commands.length, 0, "seleccionar no manda ningún comando");
  m.controller.dispose();
});

test("Enter proyecta la diapositiva seleccionada: un único output.showContent con el texto, el tema de canciones con autoFit y SIN ninguna escritura de canciones", async () => {
  const m = await make();
  await m.select();
  m.controller.projectCursor();
  await until(() => m.shows().length === 1);
  const sent = m.shows()[0].payload;
  assert.deepEqual(sent.blocks, [m.get().projection.slides[0].text]);
  assert.equal(sent.theme.autoFit, true, "con la red de seguridad de la salida");
  assert.ok(sent.theme.minFontSize < sent.theme.fontSize, "el mínimo del ajuste siempre es menor que el tamaño");
  assert.equal(sent.transitionMs, 400);
  assert.deepEqual([...new Set(m.backend.commands.map((c) => c.type))], ["output.showContent"], "proyectar no escribe canciones ni nada más");
  m.controller.dispose();
});

test("siguiente/anterior: la primera pulsación empieza por la seleccionada; en el límite inicial no hace nada; al final se queda en la última con «FIN» y no vacía la pantalla", async () => {
  const m = await make();
  await m.select();
  m.controller.nextSlide();
  assert.equal(m.get().projection.requested, 0, "todavía no se había proyectado nada: empieza por la seleccionada");
  m.controller.prevSlide();
  assert.equal(m.get().projection.cursor, 0, "antes de la primera no pasa nada");
  for (let i = 0; i < 3; i++) { await tick(35); m.controller.nextSlide(); }
  assert.equal(m.get().projection.cursor, 3);
  assert.equal(m.get().projection.atEnd, false);
  await tick(35);
  const before = m.shows().length;
  m.controller.nextSlide();   // una pulsación de más
  assert.equal(m.get().projection.cursor, 3, "se queda en la última");
  assert.equal(m.get().projection.atEnd, true, "con el indicador de fin");
  await tick(80);
  assert.equal(m.shows().length, before, "no se vuelve a proyectar");
  assert.equal(m.backend.sent("output.clearContent").length, 0, "y no se vacía la pantalla");
  m.controller.prevSlide();
  assert.equal(m.get().projection.atEnd, false, "volver atrás quita el «FIN»");
  m.controller.dispose();
});

test("al final con «al terminar: quitar el texto» la salida se limpia", async () => {
  const m = await make({ settings: { theme: createSongBackend({}).state.settings.theme, transitionMs: 250, atEnd: "clear" } });
  await m.select();
  m.controller.projectSlide(3);
  await tick(40);
  m.controller.nextSlide();
  await until(() => m.backend.sent("output.clearContent").length === 1);
  assert.equal(m.backend.sent("output.clearContent")[0].payload.transitionMs, 250);
  m.controller.dispose();
});

test("una ráfaga de teclas manda pocos mensajes y el ÚLTIMO siempre llega", async () => {
  const many = { title: "Larga", sections: Array.from({ length: 30 }, (_, i) => ({ label: `Verso ${String(i + 1)}`, text: body(i + 10, 2) })) };
  const m = await make({ songs: [many] });
  await m.select();
  m.controller.projectCursor();
  for (let i = 0; i < 25; i++) m.controller.nextSlide();
  assert.equal(m.get().projection.cursor, 25);
  await until(() => m.shows().at(-1)?.payload.blocks[0] === m.get().projection.slides[25].text);
  assert.ok(m.shows().length <= 4, `la ráfaga de 26 diapositivas mandó ${String(m.shows().length)} mensajes`);
  m.controller.dispose();
});

test("Esc quita el texto y cancela lo pendiente: una ráfaga seguida de Esc no resucita una diapositiva después", async () => {
  const m = await make();
  await m.select();
  m.controller.projectCursor();
  m.controller.nextSlide();
  m.controller.nextSlide();   // pendiente (dentro del intervalo)
  m.controller.clearProjection();
  await tick(120);
  const types = m.backend.commands.map((c) => c.type);
  assert.equal(types.at(-1), "output.clearContent", "lo último que ve la salida es la orden de limpiar");
  m.controller.dispose();
});

test("fondo propio: se aplica UNA vez (carril lento) antes del texto; una canción sin fondo NO toca el actual; si el comando falla se reintenta en la próxima proyección", async () => {
  const withBg = { ...SONG, title: "Con fondo", background: BG };
  const m = await make({ songs: [withBg, { title: "Sin fondo", sections: [{ label: "Verso 1", text: body(9, 2) }] }] });
  await m.select(0);
  m.controller.projectCursor();
  await tick(40);
  m.controller.nextSlide();
  await tick(40);
  const bgs = m.backend.sent("output.setBackground");
  assert.equal(bgs.length, 1, "una sola vez por canción");
  assert.deepEqual({ ...bgs[0].payload, transitionMs: 0 }, { kind: "video", path: BG.path, color: "#102030", muted: true, transitionMs: 0 });
  assert.ok(m.backend.commands.findIndex((c) => c.type === "output.setBackground") < m.backend.commands.findIndex((c) => c.type === "output.showContent"), "el fondo se manda antes que el texto");

  await m.select(1);
  m.controller.projectCursor();
  await until(() => m.shows().length >= 3);
  assert.equal(m.backend.sent("output.setBackground").length, 1, "la canción sin fondo no manda ningún fondo (no toca el actual)");
  m.controller.dispose();

  const retry = await make({ songs: [withBg] });
  await retry.select();
  retry.backend.failNext("output.setBackground", "output.mediaRejected", "No se puede usar ese archivo.");
  retry.controller.projectCursor();
  await until(() => retry.backend.sent("output.setBackground").length === 1);
  await tick(40);   // llega el command.failed
  retry.controller.nextSlide();
  await until(() => retry.backend.sent("output.setBackground").length === 2);
  retry.controller.dispose();
});

test("si la canción seleccionada se guarda o se borra, la tira se actualiza o desaparece", async () => {
  const m = await make();
  await m.select();
  const first = m.get().projection.slides.length;
  m.backend.songs.get(1).arrangement = [0, 1];
  m.backend.songs.get(1).revision += 1;
  m.backend.client.send("song.delete", { id: 99, expectedRevision: 1 });   // otra canción inexistente: no afecta
  await tick(30);
  assert.equal(m.get().projection.slides.length, first);
  await m.controller.openSong(1);
  await until(() => m.get().editor?.slides);
  m.controller.setField("title", "Título cambiado");
  await m.controller.save();
  await until(() => m.get().projection?.title === "Título cambiado" && m.get().projection.slides.length === 2);
  m.controller.dispose();
});

// ───── el estado EN VIVO sale de lo que confirma el backend ─────
const slidesOf = (texts) => texts.map((text, i) => ({ key: String(i), orderIndex: i, sectionUid: "u", sectionLabel: "Verso 1", chunk: 0, page: 0, text, boundary: "section" }));
const confirmed = ({ text = null, master = "live", isOpen = true, isPaused = false } = {}) => ({
  seq: 1, state: { master, content: { slide: text === null ? null : { id: "x", blocks: [{ text }], theme: {} }, transitionMs: 0 } }, window: { isOpen, isPaused },
});

test("EN VIVO / OCULTO / LISTO se derivan del estado confirmado; una marca del panel no basta", () => {
  const slides = slidesOf(["uno", "dos", "tres"]);
  assert.deepEqual(songLiveStatus(null, slides, 1), { status: { kind: "ready" }, liveIndex: null });
  assert.equal(songLiveStatus(confirmed({ text: null }), slides, 1).status.kind, "ready", "sin slide: LISTO aunque el panel haya pedido proyectar");
  assert.equal(songLiveStatus(confirmed({ text: "otra cosa" }), slides, 1).liveIndex, null, "otro texto (el backend lo rechazó o lo reemplazó)");
  const live = songLiveStatus(confirmed({ text: "dos" }), slides, null);
  assert.deepEqual([live.status.kind, live.liveIndex], ["live", 1]);
  assert.equal(songLiveStatus(confirmed({ text: "dos", master: "black" }), slides, 1).status.why, "Negro");
  assert.equal(songLiveStatus(confirmed({ text: "dos", master: "clearText" }), slides, 1).status.why, "Texto limpio");
  assert.equal(songLiveStatus(confirmed({ text: "dos", master: "logo" }), slides, 1).status.why, "Logo");
  assert.equal(songLiveStatus(confirmed({ text: "dos", master: "freeze" }), slides, 1).status.why, "Congelado");
  assert.equal(songLiveStatus(confirmed({ text: "dos", isPaused: true }), slides, 1).status.why, "Salida en pausa");
  assert.equal(songLiveStatus(confirmed({ text: "dos", isOpen: false }), slides, 1).status.why, "Salida cerrada");
});

test("un coro repetido: la pista (la que se pidió) desempata; sin pista, la primera", () => {
  const slides = slidesOf(["coro", "verso", "coro"]);
  assert.equal(confirmedSlideIndex(confirmed({ text: "coro" }), slides, 2), 2);
  assert.equal(confirmedSlideIndex(confirmed({ text: "coro" }), slides, null), 0);
  assert.equal(confirmedSlideIndex(confirmed({ text: "coro" }), slides, 1), 0, "la pista no coincide con el texto confirmado: no se fía de ella");
  assert.equal(confirmedSlideIndex({ ...confirmed({ text: "coro" }), state: { content: { slide: { blocks: [{ text: "a" }, { text: "b" }] } } } }, slides, null), null, "un slide de varios bloques no es una diapositiva de canción");
});
