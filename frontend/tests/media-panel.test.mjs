import assert from "node:assert/strict";
import test, { after } from "node:test";
import { createFakeRunner, createMediaBackend, item } from "./media-fixtures.mjs";
import { setupDom, sleep } from "./dom.mjs";
import { closeServer, loadTs } from "./helper.mjs";

// Biblioteca de medios del panel con DOM simulado (happy-dom NO decodifica ni mide): modelo puro, verificador (miniaturas y «¿se reproduce?»), controlador con un backend falso que habla el protocolo
// real, la pestaña y el flujo de extremo a extremo (importar → el backend confirma → el reproductor oculto verifica → miniatura). Todo con nombres y rutas inventados.
const win = setupDom();
for (const name of ["KeyboardEvent", "MouseEvent", "InputEvent", "HTMLInputElement", "HTMLSelectElement", "HTMLButtonElement", "HTMLLIElement", "HTMLCanvasElement", "HTMLAudioElement", "HTMLFormElement"]) {
  if (win[name]) globalThis[name] = win[name];
}
const { act, createElement } = await import("react");
const { createRoot } = await import("react-dom/client");
const model = await loadTs("/src/control/media/model.ts");
const verifierLib = await loadTs("/src/control/media/verifier.ts");
const { createMediaController } = await loadTs("/src/control/media/controller.ts");
const { MediaTab } = await loadTs("/src/control/media/MediaTab.tsx");
after(closeServer);

const OK_OUTCOME = { ok: true, durationMs: 9000, width: 1280, height: 720, errorCode: null, thumbnailBase64: "AAAA" };

// ───────────────────────── modelo puro ─────────────────────────

test("formatos: tamaños, duraciones, insignias, línea técnica y qué se puede hacer con cada elemento", () => {
  assert.equal(model.formatBytes(0), "0 B");
  assert.equal(model.formatBytes(1536), "1,5 KB");
  assert.equal(model.formatBytes(5 * 1024 ** 3), "5,0 GB");
  assert.equal(model.formatBytes(250 * 1024 ** 2), "250 MB");
  assert.equal(model.formatBytes(-1), "—");
  assert.equal(model.formatDuration(7_000), "0:07");
  assert.equal(model.formatDuration(205_000), "3:25");
  assert.equal(model.formatDuration(3_729_000), "1:02:09");
  assert.equal(model.formatDuration(null), "");

  assert.deepEqual(model.statusBadge(item(1, { status: "pending" })), { text: "Verificando…", tone: "idle", detail: null });
  assert.deepEqual(model.statusBadge(item(1, { status: "ok" })), { text: "Listo", tone: "ok", detail: null });
  assert.equal(model.statusBadge(item(1, { status: "ok", statusMessage: "Usa HEVC: se recomienda H.264." })).tone, "warn");
  assert.equal(model.statusBadge(item(1, { status: "unsupported", statusMessage: "No se puede reproducir." })).tone, "bad");
  assert.equal(model.statusBadge(item(1, { status: "missing" })).text, "Falta el archivo");

  assert.equal(model.techLine(item(1)), "1920×1080 · H.264 + AAC");
  assert.equal(model.techLine(item(1, { hasAudio: false, audioCodec: null })), "1920×1080 · H.264 · sin audio");
  assert.equal(model.techLine(item(1, { kind: "audio", width: null, height: null, videoCodec: null, audioCodec: "MP3" })), "MP3");

  assert.equal(model.canBeBackground(item(1)), true);
  assert.equal(model.canBeBackground(item(1, { kind: "audio" })), false);
  assert.equal(model.canBeBackground(item(1, { status: "unsupported" })), false);
  assert.equal(model.needsVerification(item(1)), true);
  assert.equal(model.needsVerification(item(1, { status: "ok" })), false);
  assert.equal(model.needsThumbnail(item(1, { status: "ok" })), true);
  assert.equal(model.needsThumbnail(item(1, { status: "ok", kind: "audio" })), false);
  assert.equal(model.needsThumbnail(item(1, { status: "ok", thumbnailUrl: "file:///x.jpg" })), false);
  assert.equal(model.needsThumbnail(item(1, { status: "missing", fileUrl: null })), false);
});

// ───────────────────────── verificador: de uno en uno y sin bucles ─────────────────────────

test("el verificador comprueba de UNO EN UNO, reporta lo pendiente y manda la miniatura; un elemento ya verificado con miniatura no se vuelve a tocar", async () => {
  const sent = [];
  const runner = createFakeRunner();
  const v = verifierLib.createMediaVerifier({ api: { send: (t, p) => { sent.push([t, p]); return "id"; } }, run: runner.run, gapMs: 1 });
  v.enqueue([item(1), item(2), item(3, { status: "ok", thumbnailUrl: "file:///t.jpg" }), item(4, { status: "ok" }), item(5, { kind: "audio" })]);
  await sleep(120);

  assert.deepEqual(runner.calls.map((c) => [c.id, c.wantThumbnail]), [[1, true], [2, true], [4, true], [5, false]], "el 3 ya está listo y con miniatura: no se toca; el audio no pide miniatura");
  assert.equal(runner.maxActive(), 1, "nunca dos comprobaciones a la vez");
  assert.deepEqual(sent.filter(([t]) => t === "media.reportProbe").map(([, p]) => p.mediaId), [1, 2, 5], "solo los pendientes se reportan (el 4 ya estaba OK)");
  assert.deepEqual(sent.filter(([t]) => t === "media.setThumbnail").map(([, p]) => p.mediaId), [1, 2, 4]);
  assert.deepEqual(sent.find(([t, p]) => t === "media.reportProbe" && p.mediaId === 1)[1], { mediaId: 1, ok: true, durationMs: 9000, width: 1280, height: 720, errorCode: null });
  assert.equal(v.current(), null);
  assert.equal(v.pending(), 0);
});

test("el verificador intenta cada elemento UNA vez por sesión: volver a encolar (p. ej. tras cada relectura de la lista) no repite nada y un fallo no se reintenta en bucle", async () => {
  const sent = [];
  const runner = createFakeRunner((it) => (it.id === 2 ? { ok: false, durationMs: null, width: null, height: null, errorCode: "unsupported", thumbnailBase64: null } : OK_OUTCOME));
  const v = verifierLib.createMediaVerifier({ api: { send: (t, p) => { sent.push([t, p]); return "id"; } }, run: runner.run, gapMs: 1 });
  const list = [item(1), item(2)];
  v.enqueue(list); await sleep(80);
  v.enqueue(list); v.enqueue(list); await sleep(80);   // el backend no cambió el estado (sigue «pending»): no se vuelve a intentar
  assert.equal(runner.calls.length, 2);
  const failure = sent.find(([t, p]) => t === "media.reportProbe" && p.mediaId === 2)[1];
  assert.deepEqual([failure.ok, failure.errorCode], [false, "unsupported"]);
  assert.equal(sent.filter(([t, p]) => t === "media.setThumbnail" && p.mediaId === 2).length, 0, "un fallo no manda miniatura");
});

test("si el reproductor oculto lanza o no devuelve miniatura el verificador sigue con el siguiente y reporta el fallo como «decode»", async () => {
  const sent = [];
  const runner = createFakeRunner((it) => (it.id === 1 ? new Error("boom") : { ...OK_OUTCOME, thumbnailBase64: null }));
  const v = verifierLib.createMediaVerifier({ api: { send: (t, p) => { sent.push([t, p]); return "id"; } }, run: runner.run, gapMs: 1 });
  v.enqueue([item(1), item(2)]); await sleep(80);
  assert.equal(sent.find(([t, p]) => t === "media.reportProbe" && p.mediaId === 1)[1].errorCode, "decode");
  assert.equal(sent.find(([t, p]) => t === "media.reportProbe" && p.mediaId === 2)[1].ok, true);
  assert.equal(sent.filter(([t]) => t === "media.setThumbnail").length, 0);
});

test("stop() vacía la cola y no manda nada más", async () => {
  const sent = [];
  const runner = createFakeRunner();
  const v = verifierLib.createMediaVerifier({ api: { send: (t) => { sent.push(t); return "id"; } }, run: runner.run, gapMs: 20 });
  v.enqueue([item(1), item(2), item(3), item(4)]);
  await sleep(15);
  v.stop();
  await sleep(120);
  assert.ok(runner.calls.length <= 2, `${String(runner.calls.length)} comprobaciones tras parar`);
  assert.equal(v.pending(), 0);
});

// ───────────────────────── el reproductor oculto del navegador (elementos simulados) ─────────────────────────

/** Un `document` que captura los elementos que crea el runner, para dispararles los eventos como lo haría el navegador. */
function captureDoc(initial = {}) {
  const created = [];
  const doc = {
    createElement: (tag) => {
      const el = win.document.createElement(tag); created.push(el);
      for (const [k, v] of Object.entries(initial[tag] ?? {})) Object.defineProperty(el, k, { configurable: true, value: v });
      return el;
    },
  };
  return { doc, created };
}
const defineVideo = (el, props) => { for (const [k, v] of Object.entries(props)) Object.defineProperty(el, k, { configurable: true, value: v }); };

test("probe del navegador, video: loadedmetadata → busca → seeked con fotograma = OK con duración y dimensiones reales; libera el elemento", async () => {
  const { doc, created } = captureDoc();
  const run = verifierLib.createBrowserProbeRunner(doc, 2000);
  const pending = run(item(1), false);
  const video = created.find((e) => e.tagName === "VIDEO");
  assert.ok(video, "se creó un <video> oculto (fuera del DOM)");
  assert.equal(video.isConnected, false);
  assert.equal(video.muted, true);
  assert.equal(video.getAttribute("src"), item(1).fileUrl);
  defineVideo(video, { duration: 31.4, videoWidth: 1280, videoHeight: 720 });
  video.dispatchEvent(new win.Event("loadedmetadata"));
  assert.equal(video.currentTime, 3.14, "busca al 10 % de la duración para decodificar un fotograma");
  video.dispatchEvent(new win.Event("seeked"));
  const outcome = await pending;
  assert.deepEqual([outcome.ok, outcome.durationMs, outcome.width, outcome.height, outcome.errorCode], [true, 31_400, 1280, 720, null]);
  assert.equal(video.getAttribute("src"), null, "src quitado: el medio se libera");
});

test("probe del navegador: sin fotograma decodificable, error de formato (código 4), error de lectura y tiempo agotado se distinguen", async () => {
  const { doc, created } = captureDoc();
  const run = verifierLib.createBrowserProbeRunner(doc, 150);

  let p = run(item(1), false); let v = created.at(-1);
  defineVideo(v, { duration: 10, videoWidth: 0, videoHeight: 0 });
  v.dispatchEvent(new win.Event("loadedmetadata")); v.dispatchEvent(new win.Event("seeked"));
  assert.equal((await p).errorCode, "decode", "metadatos sin fotograma: el contenedor se lee pero no se decodifica");

  p = run(item(2), false); v = created.at(-1);
  defineVideo(v, { error: { code: 4 } });
  v.dispatchEvent(new win.Event("error"));
  assert.deepEqual([(await p).ok, (await created.length)], [false, created.length]);

  p = run(item(3), false); v = created.at(-1);
  defineVideo(v, { error: { code: 3 } });
  v.dispatchEvent(new win.Event("error"));
  assert.equal((await p).errorCode, "decode");

  p = run(item(4), false);
  assert.equal((await p).errorCode, "timeout", "un archivo que nunca responde no deja la cola colgada");

  assert.equal((await run(item(5, { fileUrl: null }), false)).errorCode, "decode");
});

test("probe del navegador, código 4 = «unsupported» (el mensaje real lo pone el backend)", async () => {
  const { doc, created } = captureDoc();
  const p = verifierLib.createBrowserProbeRunner(doc, 500)(item(1), false);
  defineVideo(created.at(-1), { error: { code: 4 } });
  created.at(-1).dispatchEvent(new win.Event("error"));
  assert.equal((await p).errorCode, "unsupported");
});

test("probe del navegador, audio e imagen", async () => {
  const { doc, created } = captureDoc({ audio: { duration: 200.5 } });
  const run = verifierLib.createBrowserProbeRunner(doc, 500);
  const a = run(item(1, { kind: "audio" }), true);
  const audio = created.find((e) => e.tagName === "AUDIO");
  audio.dispatchEvent(new win.Event("canplay"));
  const ao = await a;
  assert.deepEqual([ao.ok, ao.durationMs, ao.thumbnailBase64], [true, 200_500, null], "el audio no tiene miniatura");

  const i = run(item(2, { kind: "image" }), false);
  const img = created.find((e) => e.tagName === "IMG");
  defineVideo(img, { naturalWidth: 1920, naturalHeight: 1080 });
  img.onload();
  const io = await i;
  assert.deepEqual([io.ok, io.width, io.height, io.durationMs], [true, 1920, 1080, null]);

  const bad = run(item(3, { kind: "image" }), false);
  created.filter((e) => e.tagName === "IMG").at(-1).onerror();
  assert.equal((await bad).ok, false);
});

// ───────────────────────── controlador ─────────────────────────

const tick = (ms = 40) => sleep(ms);

test("controlador: lee la lista, aplica carpeta, tipo y búsqueda en la consulta y gana siempre la última lectura", async () => {
  const b = createMediaBackend();
  const folder = b.addFolder("Anuncios");
  b.addItem({ name: "Alfa" }); b.addItem({ name: "Beta", folderId: folder.id }); b.addItem({ name: "Pista", kind: "audio" });
  const c = createMediaController({ api: b.client, refreshDelayMs: 5 });
  c.init(); await tick();
  assert.deepEqual(c.store.getState().items.map((i) => i.name), ["Alfa", "Beta", "Pista"]);
  assert.equal(c.store.getState().totalItems, 3);
  assert.equal(c.store.getState().freeBytes, b.state.freeBytes);

  c.setFolder(folder.id); await tick();
  assert.deepEqual(b.queries.at(-1).payload, { folderId: folder.id, onlyUnfiled: false, kind: null, query: null, limit: null });
  c.setFolder("unfiled"); await tick();
  assert.deepEqual([b.queries.at(-1).payload.folderId, b.queries.at(-1).payload.onlyUnfiled], [null, true]);
  c.setFolder("all"); c.setKind("audio"); await tick();
  assert.deepEqual(c.store.getState().items.map((i) => i.name), ["Pista"]);
  c.setKind(null); c.setQuery("  bet "); c.setQuery("beta"); c.setQuery("alf"); await tick(60);
  assert.deepEqual(c.store.getState().items.map((i) => i.name), ["Alfa"], "tras teclear varias veces gana la última");
  assert.equal(b.queries.at(-1).payload.query, "alf");
  c.dispose();
});

test("controlador: importar manda el comando, sigue el avance, no deja importar dos a la vez y el resultado queda como aviso (duplicado con «Mostrarlo», HEVC con aviso, error)", async () => {
  const b = createMediaBackend();
  b.addItem({ name: "Ya existente" });
  const c = createMediaController({ api: b.client, refreshDelayMs: 5 });
  c.init(); await tick();

  assert.equal(c.importFile("   ", null, null), false);
  assert.equal(c.store.getState().notice.kind, "error");
  assert.equal(b.sent("media.import").length, 0, "una ruta vacía no se manda");

  assert.equal(c.importFile(" E:\\USB\\anuncio de prueba.mp4 ", "Mi anuncio", null), true);
  await tick();
  const cmd = b.sent("media.import")[0];
  assert.deepEqual(cmd.payload, { path: "E:\\USB\\anuncio de prueba.mp4", name: "Mi anuncio", folderId: null });
  assert.equal(c.store.getState().import.running, true);
  assert.equal(c.importFile("E:\\otro.mp4", null, null), false, "una sola importación a la vez");
  assert.equal(b.sent("media.import").length, 1);

  b.progress("otro-id", "copiando", 1, 2);   // el avance de otra importación no se mezcla
  b.progress(cmd.id, "copiando", 500, 1000); await tick();
  assert.deepEqual([c.store.getState().import.phase, c.store.getState().import.doneBytes, c.store.getState().import.totalBytes], ["copiando", 500, 1000]);

  b.finish(cmd.id, { ok: false, errorCode: "media.duplicate", errorMessage: "Ese archivo ya está en la biblioteca.", duplicateOfId: 1, duplicateOfName: "Ya existente" }); await tick();
  assert.equal(c.store.getState().import.running, false);
  const dup = c.store.getState().notice;
  assert.deepEqual(dup.duplicateOf, { id: 1, name: "Ya existente" });
  c.showExisting("Ya existente"); await tick(60);
  assert.equal(c.store.getState().query, "Ya existente");
  assert.equal(c.store.getState().notice, null);

  c.importFile("E:\\hevc.mp4", null, null); await tick(); const cmd2 = b.sent("media.import")[1];
  b.finish(cmd2.id, { mediaId: 2, warningCode: "media.videoCodecWarning", warningMessage: "Este video usa HEVC (H.265): se recomienda H.264." }); await tick();
  assert.equal(c.store.getState().notice.kind, "warn");
  assert.match(c.store.getState().notice.text, /H\.264/);

  c.importFile("E:\\malo.avi", null, null); await tick(); const cmd3 = b.sent("media.import")[2];
  b.finish(cmd3.id, { ok: false, errorCode: "media.unsupportedContainer", errorMessage: "Formato no soportado (.avi). Conviértelo a MP4." }); await tick();
  assert.deepEqual([c.store.getState().notice.kind, c.store.getState().notice.text], ["error", "Formato no soportado (.avi). Conviértelo a MP4."]);
  c.dispose();
});

test("controlador: renombrar, mover, borrar, carpetas, fondo y verificar mandan SOLO su comando; no se manda lo que no cambia", async () => {
  const b = createMediaBackend();
  const it = b.addItem({ name: "Nombre" }); const folder = b.addFolder("Videos");
  const c = createMediaController({ api: b.client, refreshDelayMs: 5 });
  c.init(); await tick();
  const dto = c.store.getState().items[0];

  c.rename(dto, "Nombre"); c.rename(dto, "   "); c.move(dto, null);
  assert.equal(b.commands.length, 0, "ni renombrar a lo mismo, ni a vacío, ni mover donde ya está");
  c.rename(dto, " Nuevo nombre "); c.move(dto, folder.id); c.useAsBackground(dto, true); c.verifyLibrary(); c.createFolder("Música"); c.createFolder("  "); c.renameFolder(c.store.getState().folders[0], "Videos 2"); await tick();
  assert.deepEqual(b.commands.map((x) => x.type), ["media.rename", "media.move", "media.setBackground", "media.verifyLibrary", "media.createFolder", "media.renameFolder"]);
  assert.deepEqual(b.sent("media.setBackground")[0].payload, { mediaId: it.id, muted: true, transitionMs: 600 });
  assert.equal(c.store.getState().items[0].name, "Nuevo nombre", "tras el evento media.changed se relee la lista");

  c.setFolder(folder.id); c.deleteFolder(c.store.getState().folders.find((f) => f.id === folder.id)); await tick(60);
  assert.equal(c.store.getState().folder, "all", "al borrar la carpeta que se estaba viendo se vuelve a «Todas»");
  c.remove(c.store.getState().items[0]); await tick(60);
  assert.equal(c.store.getState().items.length, 0);
  c.dispose();
});

test("controlador: tras leer la lista verifica lo pendiente con el reproductor oculto y el estado real vuelve del backend (Listo + miniatura), sin reintentos", async () => {
  const b = createMediaBackend();
  b.addItem({ name: "Uno" }); b.addItem({ name: "Dos" });
  const runner = createFakeRunner();
  const c = createMediaController({ api: b.client, run: runner.run, refreshDelayMs: 5, verifierGapMs: 1 });
  c.init(); await tick(250);
  assert.deepEqual(c.store.getState().items.map((i) => [i.status, i.thumbnailUrl !== null]), [["ok", true], ["ok", true]]);
  assert.equal(runner.calls.length, 2, "cada elemento se comprobó una sola vez aunque la lista se releyó varias veces");
  assert.equal(runner.maxActive(), 1);
  assert.equal(c.store.getState().verifying, null);
  c.dispose();
});

// ───────────────────────── la pestaña ─────────────────────────

async function mount({ backend = createMediaBackend(), connected = true, run = null } = {}) {
  const controller = createMediaController({ api: backend.client, run, refreshDelayMs: 5, verifierGapMs: 1 });
  const container = win.document.createElement("div");
  win.document.body.appendChild(container);
  const root = createRoot(container);
  const render = async () => { await act(async () => { root.render(createElement(MediaTab, { controller, connected })); }); };
  await render();
  await act(async () => { await sleep(60); });
  await render();
  const q = (sel) => container.querySelector(sel);
  const qa = (sel) => [...container.querySelectorAll(sel)];
  const click = async (el) => { await act(async () => { el.dispatchEvent(new win.MouseEvent("click", { bubbles: true, cancelable: true })); await sleep(0); }); };
  const type = async (el, text) => {
    await act(async () => {
      Object.getOwnPropertyDescriptor(el.tagName === "SELECT" ? win.HTMLSelectElement.prototype : win.HTMLInputElement.prototype, "value").set.call(el, text);
      el.dispatchEvent(new win.Event(el.tagName === "SELECT" ? "change" : "input", { bubbles: true }));
      await sleep(0);
    });
  };
  const press = async (el, key) => { await act(async () => { el.dispatchEvent(new win.KeyboardEvent("keydown", { key, bubbles: true, cancelable: true })); await sleep(0); }); };
  const settle = async (ms = 60) => { await act(async () => { await sleep(ms); }); };
  const button = (re) => qa("button").find((b) => re.test(b.textContent ?? "") || re.test(b.getAttribute("aria-label") ?? ""));
  return { backend, controller, container, q, qa, click, type, press, settle, button, unmount: async () => { await act(async () => { root.unmount(); }); controller.dispose(); container.remove(); } };
}

test("pestaña: filas con nombre, tipo, duración, códecs, tamaño, insignia y miniatura; uso del disco; vacía dice cómo empezar", async () => {
  const empty = await mount();
  assert.match(empty.q('[data-testid="media-empty"]').textContent, /vacía/);
  assert.match(empty.q('[data-testid="media-usage"]').textContent, /0 elementos/);
  await empty.unmount();

  const b = createMediaBackend();
  b.addItem({ name: "Fondo azul", status: "ok", thumbnailUrl: "file:///C:/t/1.jpg?r=2" });
  b.addItem({ name: "Pista de prueba", kind: "audio", width: null, height: null, videoCodec: null, audioCodec: "MP3", durationMs: 205_000, hasAudio: true, status: "ok" });
  b.addItem({ name: "Con aviso", status: "ok", statusCode: "media.videoCodecWarning", statusMessage: "Usa HEVC: se recomienda H.264." });
  b.addItem({ name: "Roto", status: "unsupported", statusMessage: "El video usa un códec que este equipo no reproduce." });
  const m = await mount({ backend: b });
  const rows = m.qa('[data-testid="media-row"]');
  assert.equal(rows.length, 4);
  assert.match(m.q('[data-testid="media-usage"]').textContent, /4 elementos/);
  assert.match(m.q('[data-testid="media-usage"]').textContent, /libres en el disco/);
  const byName = (n) => rows.find((r) => r.textContent.includes(n));
  assert.equal(byName("Fondo azul").querySelector("img").getAttribute("src"), "file:///C:/t/1.jpg?r=2");
  assert.match(byName("Pista de prueba").textContent, /Audio · 3:25 · MP3/);
  assert.equal(byName("Pista de prueba").querySelector("img"), null, "el audio lleva un icono, no una miniatura");
  assert.match(byName("Con aviso").textContent, /se recomienda H\.264/);
  assert.equal(byName("Con aviso").querySelector('[data-testid="media-badge"]').textContent, "Aviso");
  assert.equal(byName("Roto").querySelector('[data-testid="media-badge"]').textContent, "No se puede usar");
  assert.equal([...byName("Roto").querySelectorAll("button")].some((x) => x.textContent === "Fondo"), false, "lo no soportado no se ofrece como fondo");
  assert.equal([...byName("Pista de prueba").querySelectorAll("button")].some((x) => x.textContent === "Fondo"), false, "una pista de audio tampoco");
  await m.unmount();
});

test("pestaña: importar desde el panel — pegar la ruta, el botón se activa, se manda, aparece el avance y la barra se limpia al terminar", async () => {
  const m = await mount();
  const importBtn = m.button(/^Importar$/);
  assert.equal(importBtn.disabled, true);
  await m.type(m.q('input[aria-label="Ruta completa del archivo"]'), "E:\\USB\\anuncio de prueba.mp4");
  await m.type(m.q('input[aria-label="Nombre (opcional)"]'), "Anuncio");
  assert.equal(m.button(/^Importar$/).disabled, false);
  await m.click(m.button(/^Importar$/));
  await m.settle();
  const cmd = m.backend.sent("media.import")[0];
  assert.deepEqual(cmd.payload, { path: "E:\\USB\\anuncio de prueba.mp4", name: "Anuncio", folderId: null });
  assert.equal(m.q('input[aria-label="Ruta completa del archivo"]').value, "", "el formulario se limpia");
  assert.equal(m.q('input[aria-label="Ruta completa del archivo"]').disabled, true, "mientras copia no se puede lanzar otra");

  m.backend.progress(cmd.id, "copiando", 250, 1000); await m.settle();
  assert.equal(m.q('[role="progressbar"]').getAttribute("aria-valuenow"), "25");
  assert.match(m.q('[data-testid="media-progress"]').textContent, /copiando/);
  await m.click(m.button(/^Cancelar$/));
  assert.equal(m.backend.sent("media.cancelImport").length, 1);

  m.backend.addItem({ name: "Anuncio" });
  m.backend.finish(cmd.id, { mediaId: 1 }); await m.settle(80);
  assert.equal(m.q('[data-testid="media-progress"]'), null);
  assert.match(m.q('[data-testid="media-notice"]').textContent, /Importado/);
  assert.equal(m.qa('[data-testid="media-row"]').length, 1, "la lista se relee al terminar");
  await m.unmount();
});

test("pestaña: un duplicado dice que ya existe y «Mostrarlo» filtra por su nombre; un error se ve en rojo y se puede cerrar", async () => {
  const b = createMediaBackend();
  b.addItem({ name: "Existente" }); b.addItem({ name: "Otro" });
  const m = await mount({ backend: b });
  await m.type(m.q('input[aria-label="Ruta completa del archivo"]'), "E:\\copia.mp4");
  await m.click(m.button(/^Importar$/)); await m.settle();
  b.finish(b.sent("media.import")[0].id, { ok: false, errorCode: "media.duplicate", errorMessage: "Ese archivo ya está en la biblioteca.", duplicateOfId: 1, duplicateOfName: "Existente" }); await m.settle();
  assert.match(m.q('[data-testid="media-notice"]').textContent, /ya está en la biblioteca como «Existente»/);
  await m.click(m.button(/^Mostrarlo$/)); await m.settle(80);
  assert.deepEqual(m.qa('[data-testid="media-row"]').map((r) => r.textContent.includes("Existente")), [true]);

  await m.type(m.q('input[aria-label="Ruta completa del archivo"]'), "E:\\malo.avi");
  await m.click(m.button(/^Importar$/)); await m.settle();
  b.finish(b.sent("media.import")[1].id, { ok: false, errorCode: "media.unsupportedContainer", errorMessage: "Formato no soportado (.avi)." }); await m.settle();
  assert.match(m.q('[data-testid="media-notice"]').className, /red/);
  await m.click(m.button(/Cerrar aviso/));
  assert.equal(m.q('[data-testid="media-notice"]'), null);
  await m.unmount();
});

test("pestaña: carpetas (crear, filtrar, renombrar, borrar conserva los elementos), mover, búsqueda y tipo", async () => {
  const b = createMediaBackend();
  b.addItem({ name: "Alfa" }); b.addItem({ name: "Beta" });
  const m = await mount({ backend: b });
  await m.type(m.q('input[aria-label="Nueva carpeta"]'), "Anuncios");
  await act(async () => { m.q('input[aria-label="Nueva carpeta"]').form.dispatchEvent(new win.Event("submit", { bubbles: true, cancelable: true })); await sleep(0); });
  await m.settle(80);
  assert.equal(b.sent("media.createFolder").length, 1);
  const chip = m.button(/^Anuncios \(0\)$/);
  assert.ok(chip, "la carpeta aparece como botón con su conteo");

  await m.type(m.qa('select[aria-label^="Carpeta de Alfa"]')[0], String(b.folders.keys().next().value));
  await m.settle(80);
  assert.equal(b.items.get(1).folderId, 1);
  await m.click(m.button(/^Anuncios \(1\)$/)); await m.settle(80);
  assert.deepEqual(m.qa('[data-testid="media-row"]').map((r) => r.textContent.includes("Alfa")), [true]);
  assert.equal(m.button(/^Anuncios \(1\)$/).getAttribute("aria-pressed"), "true");

  await m.click(m.button(/^Renombrar$/));
  const edit = m.q('input[aria-label="Nuevo nombre de la carpeta"]');
  await m.type(edit, "Videos"); await m.press(edit, "Enter"); await m.settle(80);
  assert.equal(b.folders.get(1).name, "Videos");
  await m.click(m.button(/Borrar carpeta/)); await m.settle(100);
  assert.equal(b.folders.size, 0);
  assert.equal(b.items.size, 2, "los elementos se conservan");
  assert.equal(m.qa('[data-testid="media-row"]').length, 2, "y se vuelve a «Todas»");

  await m.type(m.q('input[aria-label="Buscar por nombre"]'), "bet"); await m.settle(100);
  assert.deepEqual(m.qa('[data-testid="media-row"]').map((r) => r.textContent.includes("Beta")), [true]);
  await m.type(m.q('input[aria-label="Buscar por nombre"]'), ""); await m.type(m.q('select[aria-label="Tipo"]'), "audio"); await m.settle(100);
  assert.match(m.q('[data-testid="media-empty"]').textContent, /Ningún elemento coincide/);
  await m.unmount();
});

test("pestaña: usar como fondo respeta «Fondo en silencio»; borrar pide confirmación EN LÍNEA (sin diálogos del sistema) y no manda nada hasta confirmar; renombrar con Enter y Esc", async () => {
  const b = createMediaBackend();
  b.addItem({ name: "Fondo", status: "ok" });
  const m = await mount({ backend: b });
  await m.click(m.button(/^Fondo$/)); await m.settle();
  assert.deepEqual(b.sent("media.setBackground")[0].payload, { mediaId: 1, muted: true, transitionMs: 600 });
  await m.click(m.qa('input[type="checkbox"]')[0]);
  await m.click(m.button(/^Fondo$/)); await m.settle();
  assert.equal(b.sent("media.setBackground")[1].payload.muted, false);

  await m.click(m.button(/^Borrar Fondo$/));
  assert.ok(m.q('[role="group"][aria-label="Confirmar el borrado"]'));
  assert.equal(b.sent("media.delete").length, 0, "todavía no se borró nada");
  await m.click(m.button(/^No$/));
  assert.equal(m.q('[role="group"][aria-label="Confirmar el borrado"]'), null);
  await m.click(m.button(/^Borrar Fondo$/)); await m.click(m.button(/Sí, borrar/)); await m.settle(80);
  assert.equal(b.sent("media.delete").length, 1);
  assert.equal(m.qa('[data-testid="media-row"]').length, 0);
  await m.unmount();

  const b2 = createMediaBackend(); b2.addItem({ name: "Viejo" });
  const m2 = await mount({ backend: b2 });
  await m2.click(m2.button(/^Renombrar Viejo$/));
  const input = m2.q('input[aria-label="Nombre"]');
  await m2.type(input, "Nuevo"); await m2.press(input, "Escape"); await m2.settle();
  assert.equal(b2.sent("media.rename").length, 0, "Esc cancela");
  await m2.click(m2.button(/^Renombrar Viejo$/));
  const input2 = m2.q('input[aria-label="Nombre"]');
  await m2.type(input2, "Nuevo"); await m2.press(input2, "Enter"); await m2.settle(80);
  assert.deepEqual(b2.sent("media.rename")[0].payload, { id: 1, name: "Nuevo" });
  await m2.unmount();
});

test("pestaña: sin conexión todo se desactiva y no se manda nada", async () => {
  const b = createMediaBackend(); b.addItem({ name: "Alfa", status: "ok" });
  const m = await mount({ backend: b, connected: false });
  assert.equal(m.q('input[aria-label="Ruta completa del archivo"]').disabled, true);
  assert.equal(m.button(/^Importar$/).disabled, true);
  assert.equal(m.button(/Verificar biblioteca/).disabled, true);
  assert.equal(b.queries.length, 0, "ni siquiera lee la lista hasta que haya conexión");
  await m.unmount();
});

test("pestaña: la lista está VIRTUALIZADA — con 2000 elementos solo se pintan las filas visibles y desplazar mueve la ventana", async () => {
  const b = createMediaBackend();
  for (let i = 0; i < 600; i++) b.addItem({ name: `Elemento ${String(i).padStart(4, "0")}`, status: "ok", thumbnailUrl: "file:///t.jpg" });
  const m = await mount({ backend: b });
  assert.equal(m.controller.store.getState().items.length, 500, "el backend entrega como mucho 500 por consulta");
  assert.ok(m.qa('[data-testid="media-row"]').length <= 40, `${String(m.qa('[data-testid="media-row"]').length)} filas pintadas`);
  assert.match(m.container.textContent, /Hay más elementos de los que caben/);
  const list = m.q('[data-testid="media-list"]');
  Object.defineProperty(list, "scrollTop", { configurable: true, get: () => 20_000 });   // happy-dom no hace layout: se fijan el desplazamiento y el alto visible
  Object.defineProperty(list, "clientHeight", { configurable: true, get: () => 440 });
  await act(async () => { list.dispatchEvent(new win.Event("scroll", { bubbles: true })); await sleep(0); });
  const names = m.qa('[data-testid="media-row"]').map((r) => r.textContent);
  assert.ok(names.length <= 40 && names.some((t) => t.includes("Elemento 03")), "la ventana avanzó con el desplazamiento");
  await m.unmount();
});

// ───────────────────────── extremo a extremo ─────────────────────────

test("extremo a extremo: importar desde el panel → el backend lo confirma «pendiente» → el reproductor oculto lo verifica → vuelve «Listo» con miniatura, y ningún comando se repite", async () => {
  const b = createMediaBackend();
  b.onImport((cmd) => {
    const it = b.addItem({ name: "Anuncio de prueba" });
    b.progress(cmd.id, "copiando", 1000, 1000);
    b.finish(cmd.id, { mediaId: it.id });
    b.changedEvent("added", it.id);
  });
  const runner = createFakeRunner();
  const m = await mount({ backend: b, run: runner.run });
  assert.equal(m.qa('[data-testid="media-row"]').length, 0);

  await m.type(m.q('input[aria-label="Ruta completa del archivo"]'), "E:\\USB\\Anuncio de prueba.mp4");
  await m.click(m.button(/^Importar$/));
  await m.settle(400);

  const row = m.qa('[data-testid="media-row"]')[0];
  assert.ok(row, "el elemento aparece en la lista");
  assert.equal(row.querySelector('[data-testid="media-badge"]').textContent, "Listo", "tras verificar en el reproductor oculto");
  assert.ok(row.querySelector("img"), "con su miniatura");
  assert.deepEqual(b.commands.map((c) => c.type).filter((t) => t !== "media.import"), ["media.reportProbe", "media.setThumbnail"]);
  assert.equal(runner.calls.length, 1, "se comprobó una sola vez");
  assert.match(m.q('[data-testid="media-notice"]').textContent, /Importado/);
  await m.unmount();
});

test("higiene del panel: la pestaña no usa alert/confirm/prompt ni bucles infinitos y las comprobaciones ceden el hilo entre elementos", async () => {
  const fs = await import("node:fs");
  for (const f of ["MediaTab.tsx", "controller.ts", "verifier.ts", "model.ts"]) {
    const src = fs.readFileSync(new URL(`../src/control/media/${f}`, import.meta.url), "utf8");
    assert.doesNotMatch(src, /\b(window\.)?(alert|confirm|prompt)\(/, `${f} usa un diálogo del sistema`);
    assert.doesNotMatch(src, /while\s*\(\s*true\s*\)/, `${f} tiene un bucle infinito`);
  }
  const runner = createFakeRunner();
  const v = verifierLib.createMediaVerifier({ api: { send: () => "id" }, run: runner.run, gapMs: 15 });
  const start = performance.now();
  v.enqueue(Array.from({ length: 6 }, (_, i) => item(i + 1)));
  await sleep(300);
  assert.equal(runner.calls.length, 6);
  assert.ok(performance.now() - start >= 6 * 15 - 5, "hubo pausas de cesión entre los elementos");
});
