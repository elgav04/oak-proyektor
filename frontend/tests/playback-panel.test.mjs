import assert from "node:assert/strict";
import test, { after } from "node:test";
import { AUX, LAPTOP, USB, createPlaybackBackend } from "./playback-fixtures.mjs";
import { setupDom, sleep } from "./dom.mjs";
import { closeServer, loadTs } from "./helper.mjs";

// Reproductor del panel con DOM simulado, backend falso con el protocolo real, cliente IPC real y controlador real. Todo con nombres inventados.
const win = setupDom();
for (const name of ["KeyboardEvent", "MouseEvent", "InputEvent", "HTMLInputElement", "HTMLSelectElement", "HTMLButtonElement"]) {
  if (win[name]) globalThis[name] = win[name];
}
const { act, createElement } = await import("react");
const { createRoot } = await import("react-dom/client");
const model = await loadTs("/src/control/playback/model.ts");
const { createPlaybackController } = await loadTs("/src/control/playback/controller.ts");
const { PlayerBar } = await loadTs("/src/control/playback/PlayerBar.tsx");
const { canPlay } = await loadTs("/src/control/media/model.ts");
const roots = [];
after(async () => { await act(async () => { for (const r of roots) r.unmount(); }); });
after(closeServer);

const settle = (ms = 20) => act(async () => { await sleep(ms); });
const outputOf = (foreground, extra = {}) => ({ seq: 1, state: { schemaVersion: 4, revision: 0, master: "live", background: { kind: "none", color: "#000000" }, content: { slide: null, transitionMs: 0 }, overlays: { alert: null }, foreground, ...(extra.state ?? {}) }, window: { isOpen: true, isPaused: false, ...(extra.window ?? {}) } });
const fgOf = (over = {}) => ({ id: "f1", mediaId: 1, url: "file:///C:/x.mp4", fileName: "Video de prueba", phase: "playing", clipVolumePct: 100, volumePct: 50, muted: false, loop: false, seekMs: 0, seekSeq: 0, fadeInMs: 400, fadeOutMs: 400, backdrop: "black", onEnd: "back", ...over });

async function setup(options = {}) {
  const backend = createPlaybackBackend(options);
  const controller = createPlaybackController({ api: backend.client, sliderIntervalMs: 15 });
  await controller.init();
  return { backend, controller };
}

// ───────────────────────── modelo puro ─────────────────────────

test("el estado del video se DERIVA de lo confirmado: en pantalla, en pausa, terminó, oculto por el modo maestro o la salida; Freeze NO lo oculta", () => {
  assert.deepEqual(model.foregroundStatus(null), { kind: "none" });
  assert.deepEqual(model.foregroundStatus(outputOf(null)), { kind: "none" });
  assert.equal(model.foregroundStatus(outputOf(fgOf())).kind, "onScreen");
  assert.equal(model.foregroundStatus(outputOf(fgOf({ phase: "paused" }))).kind, "paused");
  assert.equal(model.foregroundStatus(outputOf(fgOf({ phase: "ended" }))).kind, "ended");
  for (const [master, why] of [["black", "Negro"], ["clearText", "Texto limpio"], ["logo", "Logo"]]) {
    const s = model.foregroundStatus(outputOf(fgOf({ phase: "paused" }), { state: { master } }));
    assert.deepEqual([s.kind, s.why], ["hidden", why]);
  }
  assert.equal(model.foregroundStatus(outputOf(fgOf(), { state: { master: "freeze" } })).kind, "onScreen", "Freeze no lo oculta");
  assert.equal(model.foregroundStatus(outputOf(fgOf(), { window: { isOpen: false } })).why, "Salida cerrada");
  assert.equal(model.foregroundStatus(outputOf(fgOf(), { window: { isPaused: true } })).why, "Salida en pausa");
  assert.equal(model.foregroundBadge(model.foregroundStatus(outputOf(fgOf({ phase: "paused" }), { state: { master: "black" } }))), "OCULTO · Negro");
  assert.equal(model.foregroundBadge(model.foregroundStatus(outputOf(fgOf()))), "EN PANTALLA");
});

test("formato de tiempo y acotado de porcentajes", () => {
  assert.equal(model.formatClock(0), "0:00");
  assert.equal(model.formatClock(65_000), "1:05");
  assert.equal(model.formatClock(3_725_000), "1:02:05");
  assert.equal(model.formatClock(-5), "0:00");
  assert.equal(model.formatClock(Number.NaN), "0:00");
  assert.deepEqual([model.clampPct(-3), model.clampPct(55.4), model.clampPct(900), model.clampPct(Number.NaN)], [0, 55, 100, 0]);
});

test("la ruta de audio dice SIEMPRE por dónde sale cada cosa y avisa si no coinciden o si falta el dispositivo de la iglesia", () => {
  const base = { usesWindowsDefault: true, churchDeviceId: null, churchDeviceName: null, churchConnected: true, defaultDeviceId: LAPTOP.id, defaultDeviceName: LAPTOP.name, mismatch: false };
  const a = model.routeSummary(base);
  assert.equal(a.tracks, `Predeterminado de Windows: ${LAPTOP.name}`);
  assert.equal(a.video, LAPTOP.name);
  assert.deepEqual(a.warnings, []);

  const ok = model.routeSummary({ ...base, usesWindowsDefault: false, churchDeviceId: USB.id, churchDeviceName: USB.name, defaultDeviceId: USB.id, defaultDeviceName: USB.name });
  assert.deepEqual([ok.tracks, ok.video, ok.warnings.length], [USB.name, USB.name, 0]);

  const mismatch = model.routeSummary({ ...base, usesWindowsDefault: false, churchDeviceId: USB.id, churchDeviceName: USB.name, mismatch: true });
  assert.equal(mismatch.warnings.length, 1);
  assert.match(mismatch.warnings[0], new RegExp(USB.name.replace(/[()]/g, "\\$&")));
  assert.match(mismatch.warnings[0], new RegExp(LAPTOP.name));

  const missing = model.routeSummary({ ...base, usesWindowsDefault: false, churchDeviceId: USB.id, churchDeviceName: USB.name, churchConnected: false });
  assert.match(missing.tracks, /NO conectado/);
  assert.match(missing.warnings[0], /no está conectado/);
  assert.match(missing.warnings[0], /No se usa otro dispositivo/);

  assert.match(model.routeSummary({ ...base, defaultDeviceId: null, defaultDeviceName: null }).warnings[0], /ningún dispositivo de audio predeterminado/);
  assert.match(model.defaultChangeMessage("A", "B"), /de «A» a «B»/);
});

test("solo se pueden reproducir videos y audios utilizables; una imagen se usa de fondo", () => {
  assert.equal(canPlay({ kind: "video", status: "ok" }), true);
  assert.equal(canPlay({ kind: "audio", status: "pending" }), true);
  assert.equal(canPlay({ kind: "image", status: "ok" }), false);
  assert.equal(canPlay({ kind: "video", status: "missing" }), false);
  assert.equal(canPlay({ kind: "audio", status: "unsupported" }), false);
});

// ───────────────────────── controlador ─────────────────────────

test("init lee el estado del audio; un estado más viejo no pisa a uno más nuevo", async () => {
  const { backend, controller } = await setup({ settings: { masterVolumePct: 35 } });
  assert.equal(controller.store.getState().audio.settings.masterVolumePct, 35);
  const newer = backend.confirmAudio();
  await settle();
  assert.equal(controller.store.getState().audio.seq, newer.seq);
  backend.emitAudio({ ...newer, seq: newer.seq - 1, settings: { ...newer.settings, masterVolumePct: 99 } });   // un evento atrasado
  await settle();
  assert.equal(controller.store.getState().audio.settings.masterVolumePct, 35);
  assert.equal(controller.store.getState().audio.seq, newer.seq);
  controller.dispose();
});

test("reproducir un video manda media.play SIN decidir nada por su cuenta: el estado solo cambia cuando el backend lo confirma", async () => {
  const { backend, controller } = await setup();
  const video = backend.seedMedia({ name: "Video de prueba", kind: "video" });
  controller.playItem(video);
  assert.equal(backend.sent("media.play").length, 0, "los comandos viajan por el transporte");
  await settle();
  const [cmd] = backend.sent("media.play");
  assert.deepEqual(cmd.payload, { mediaId: video.id, startPlaying: null, volumePct: null, muted: false, loop: false, fadeInMs: null, fadeOutMs: null, backdrop: "black", onEnd: "back" });
  assert.equal(backend.foreground().phase, "playing");
  controller.dispose();
});

test("un audio se carga LISTO en pausa por defecto y solo suena si se pide; las imágenes no se mandan", async () => {
  const { backend, controller } = await setup();
  const audio = backend.seedMedia({ name: "Pista de prueba", kind: "audio" });
  const image = backend.seedMedia({ name: "Imagen de prueba", kind: "image" });
  controller.playItem(audio);
  controller.playItem(audio, { startNow: true, loop: true });
  controller.playItem(image);
  await settle();
  assert.deepEqual(backend.sent("media.play").map((c) => [c.payload.mediaId, c.payload.startPlaying, c.payload.loop]), [[audio.id, false, false], [audio.id, true, true]]);
  assert.equal(controller.store.getState().audio.track.phase, "playing");
  await settle(30);
  assert.equal(controller.store.getState().trackName, "Pista de prueba", "el nombre de la pista se pide a la biblioteca");
  controller.dispose();
});

test("el transporte va al destino que se nombra y «Silencio» manda media.stopAll", async () => {
  const { backend, controller } = await setup();
  controller.pause("foreground"); controller.resume("track"); controller.stop("track"); controller.setMuted(true); controller.silence();
  controller.setDevice(USB.id); controller.testSound("church"); controller.testSound("default");
  await settle();
  assert.deepEqual(backend.sent("media.pause").map((c) => c.payload), [{ target: "foreground" }]);
  assert.deepEqual(backend.sent("media.resume").map((c) => c.payload), [{ target: "track" }]);
  assert.deepEqual(backend.sent("media.stop").map((c) => c.payload), [{ target: "track" }]);
  assert.deepEqual(backend.sent("media.setVolume").map((c) => c.payload), [{ target: "foreground", volumePct: null, muted: true }]);
  assert.equal(backend.sent("media.stopAll").length, 1);
  assert.deepEqual(backend.sent("audio.setDevice").map((c) => c.payload), [{ deviceId: USB.id }]);
  assert.deepEqual(backend.sent("audio.test").map((c) => c.payload.target), ["church", "default"]);
  controller.dispose();
});

test("arrastrar la barra de avance o el volumen no inunda el canal: a lo sumo una orden cada intervalo y la ÚLTIMA siempre llega", async () => {
  const { backend, controller } = await setup();
  for (let i = 1; i <= 40; i++) { controller.seek("track", i * 1000); controller.setClipVolume("foreground", i); controller.setMasterVolume(i); }
  await settle(120);
  const seeks = backend.sent("media.seek");
  const vols = backend.sent("media.setVolume");
  const masters = backend.sent("audio.setMasterVolume");
  assert.ok(seeks.length < 10 && vols.length < 10 && masters.length < 10, `se enviaron ${seeks.length}/${vols.length}/${masters.length}`);
  assert.equal(seeks.at(-1).payload.positionMs, 40_000);
  assert.equal(vols.at(-1).payload.volumePct, 40);
  assert.equal(masters.at(-1).payload.volumePct, 40);
  controller.seekNow("track", 7000);
  await settle();
  assert.equal(backend.sent("media.seek").at(-1).payload.positionMs, 7000);
  controller.dispose();
});

test("el avance y los fallos de la salida llegan al estado de vista", async () => {
  const { backend, controller } = await setup();
  backend.progress({ target: "foreground", id: "f1", positionMs: 4200, durationMs: 90_000, playing: true });
  backend.progress({ target: "track", id: null, positionMs: 1500, durationMs: 120_000, playing: true });
  backend.playbackFailed({ id: "f1", code: "unsupportedFormat", message: "Este equipo no puede reproducir ese video." });
  await settle();
  const s = controller.store.getState();
  assert.deepEqual(s.fgProgress, { id: "f1", positionMs: 4200, durationMs: 90_000, playing: true });
  assert.deepEqual(s.trackProgress, { positionMs: 1500, durationMs: 120_000, playing: true });
  assert.equal(s.failure, "Este equipo no puede reproducir ese video.");
  controller.dismissFailure();
  assert.equal(controller.store.getState().failure, null);
  controller.dispose();
});

// ───────────────────────── la barra (DOM) ─────────────────────────

function mount(controller, output, connected = true) {
  const container = win.document.createElement("div");
  win.document.body.appendChild(container);
  const root = createRoot(container, { onCaughtError: () => undefined });
  roots.push(root);
  const render = async (out = output) => { await act(async () => { root.render(createElement(PlayerBar, { controller, connected, output: out })); }); };
  const q = (sel) => container.querySelector(sel);
  const byLabel = (label) => container.querySelector(`[aria-label="${label}"]`);
  const click = async (el) => { await act(async () => { el.dispatchEvent(new win.MouseEvent("click", { bubbles: true })); }); await settle(); };
  return { container, render, q, byLabel, click, text: () => container.textContent, unmount: () => act(async () => { root.unmount(); }) };
}

test("la barra muestra SIEMPRE por dónde sale cada audio y «Silencio» manda el mensaje que calla todo", async () => {
  const { backend, controller } = await setup();
  const m = mount(controller, backend.output());
  await m.render(); await settle();
  assert.match(m.q('[data-testid="player-route"]').textContent, new RegExp(`Pistas:.*Predeterminado de Windows: ${LAPTOP.name}`));
  assert.match(m.q('[data-testid="player-route"]').textContent, new RegExp(`Video:.*${LAPTOP.name}`));
  assert.equal(m.container.querySelectorAll('[data-testid="player-warning"]').length, 0);
  await m.click(m.byLabel("Silencio: calla las pistas y pausa el video"));
  assert.equal(backend.sent("media.stopAll").length, 1);
  controller.dispose();
});

test("AVISO VISIBLE si el dispositivo de la iglesia no es el predeterminado de Windows, y otro si Windows lo cambia durante el servicio (con «Entendido»)", async () => {
  const { backend, controller } = await setup({ settings: { churchDeviceId: USB.id, churchDeviceName: USB.name }, defaultId: USB.id });
  const m = mount(controller, backend.output());
  await m.render(); await settle();
  assert.equal(m.container.querySelectorAll('[data-testid="player-warning"]').length, 0, "coinciden: sin aviso");

  backend.windowsChangesDefault(LAPTOP.id);   // Windows pasa el predeterminado a los altavoces del portátil
  await settle(); await m.render();
  const warn = m.container.querySelectorAll('[data-testid="player-warning"]');
  assert.equal(warn.length, 1);
  assert.match(warn[0].textContent, /audio del video sale por el predeterminado de Windows/);
  assert.match(warn[0].textContent, new RegExp(LAPTOP.name));
  assert.equal(warn[0].getAttribute("role"), "alert");
  const change = m.q('[data-testid="player-default-change"]');
  assert.match(change.textContent, new RegExp(`de «${USB.name.replace(/[()]/g, "\\$&")}» a «${LAPTOP.name}»`));

  await m.click([...change.querySelectorAll("button")].find((b) => b.textContent === "Entendido"));
  assert.equal(m.q('[data-testid="player-default-change"]'), null);
  assert.equal(m.container.querySelectorAll('[data-testid="player-warning"]').length, 1, "la discrepancia sigue avisada mientras exista");
  controller.dispose();
});

test("si el dispositivo de la iglesia se desconecta, la barra lo avisa y NO ofrece otro", async () => {
  const { backend, controller } = await setup({ settings: { churchDeviceId: USB.id, churchDeviceName: USB.name }, defaultId: USB.id });
  const m = mount(controller, backend.output());
  await m.render(); await settle();
  backend.disconnect(USB.id);
  await settle(); await m.render();
  const warn = [...m.container.querySelectorAll('[data-testid="player-warning"]')].map((n) => n.textContent);
  assert.ok(warn.some((t) => /no está conectado/.test(t) && /No se usa otro dispositivo/.test(t)), warn.join("|"));
  assert.match(m.q('[data-testid="player-route"]').textContent, /NO conectado/);
  controller.dispose();
});

test("la insignia del video sale del estado confirmado: no hay «en pantalla» hasta que el backend lo confirma, y tras recargar el panel se recupera sola", async () => {
  const { backend, controller } = await setup();
  const video = backend.seedMedia({ name: "Video de prueba", kind: "video" });
  const m = mount(controller, null);
  await m.render(null);
  assert.equal(m.q('[data-testid="player-foreground"]'), null);

  controller.playItem(video);                       // se pidió, pero nada confirmado todavía
  assert.equal(m.q('[data-testid="player-foreground"]'), null);
  await settle();
  await m.render(backend.output());
  assert.equal(m.q('[data-testid="player-badge"]').textContent, "EN PANTALLA");

  // Recargar el panel = montar todo de nuevo con un controlador nuevo y el estado que devuelve output.getState.
  controller.dispose();
  await m.unmount();
  const reloaded = createPlaybackController({ api: backend.client });
  await reloaded.init();
  const m2 = mount(reloaded, backend.output());
  await m2.render();
  assert.equal(m2.q('[data-testid="player-badge"]').textContent, "EN PANTALLA");
  assert.match(m2.q('[data-testid="player-foreground"]').textContent, /Video de prueba/);
  reloaded.dispose();
});

test("con Negro, Texto limpio o Logo la barra dice «OCULTO · …», lo muestra pausado y NO deja reanudarlo; con Freeze sigue en pantalla", async () => {
  const { backend, controller } = await setup();
  const video = backend.seedMedia({ name: "Video de prueba", kind: "video" });
  controller.playItem(video); await settle();
  const m = mount(controller, backend.output());
  await m.render();
  const play = () => m.byLabel("Reproducir el video");

  backend.setMaster("black"); await settle(); await m.render(backend.output());
  assert.equal(m.q('[data-testid="player-badge"]').textContent, "OCULTO · Negro");
  assert.equal(backend.foreground().phase, "paused", "el backend lo pausó al ocultarlo");
  assert.equal(play().disabled, true, "reanudar oculto está desactivado");

  backend.setMaster("live"); await settle(); await m.render(backend.output());
  assert.equal(m.q('[data-testid="player-badge"]').textContent, "EN PAUSA", "volver a Live no lo reanuda");
  assert.equal(play().disabled, false);
  await m.click(play());
  assert.equal(backend.foreground().phase, "playing");

  backend.setMaster("freeze"); await settle(); await m.render(backend.output());
  assert.equal(m.q('[data-testid="player-badge"]').textContent, "EN PANTALLA");
  controller.dispose();
});

test("la pista no se corta con Negro ni con la pausa de la salida, y avisa si pierde el dispositivo", async () => {
  const { backend, controller } = await setup();
  const audio = backend.seedMedia({ name: "Pista de prueba", kind: "audio" });
  controller.playItem(audio, { startNow: true }); await settle(40);
  const m = mount(controller, backend.output());
  await m.render();
  assert.equal(m.q('[data-testid="player-track"] [data-testid="player-badge"]').textContent, "SONANDO");
  backend.setMaster("black"); backend.setWindow({ isPaused: true }); await settle(); await m.render(backend.output());
  assert.equal(m.q('[data-testid="player-track"] [data-testid="player-badge"]').textContent, "SONANDO");
  assert.equal(backend.sent("media.pause").length, 0);

  backend.state.track = { ...backend.state.track, phase: "deviceLost", errorCode: "media.deviceLost", errorMessage: "Dispositivo de audio desconectado: la pista está en pausa." };
  backend.confirmAudio(); await settle(); await m.render(backend.output());
  assert.equal(m.q('[data-testid="player-track"] [data-testid="player-badge"]').textContent, "DISPOSITIVO DESCONECTADO");
  assert.match(m.q('[data-testid="player-track"]').textContent, /Dispositivo de audio desconectado/);
  controller.dispose();
});

test("ajustes de audio: lista los dispositivos con el predeterminado marcado, elegir manda el id estable, y «Probar sonido» tiene una orden para cada ruta", async () => {
  const { backend, controller } = await setup({ settings: { churchDeviceId: "id-ausente", churchDeviceName: "Interfaz vieja" } });
  const m = mount(controller, backend.output());
  await m.render(); await settle();
  await m.click([...m.container.querySelectorAll("button")].find((b) => b.textContent === "Ajustes de audio"));
  const select = m.byLabel("Dispositivo de audio de la iglesia");
  const options = [...select.querySelectorAll("option")].map((o) => o.textContent);
  assert.ok(options[0].startsWith("Usar el predeterminado de Windows"));
  assert.ok(options.some((t) => t === "Interfaz vieja (NO conectado)"), "el dispositivo guardado que no está conectado se muestra, no se pierde");
  assert.ok(options.some((t) => t === `${LAPTOP.name} · predeterminado de Windows`));
  assert.equal(select.value, "id-ausente");

  await act(async () => { select.value = USB.id; select.dispatchEvent(new win.Event("change", { bubbles: true })); });
  await settle();
  assert.deepEqual(backend.sent("audio.setDevice").map((c) => c.payload), [{ deviceId: USB.id }]);
  assert.equal(backend.state.settings.churchDeviceId, USB.id);

  const test1 = [...m.container.querySelectorAll("button")].find((b) => b.textContent === "Probar sonido: dispositivo de la iglesia");
  const test2 = [...m.container.querySelectorAll("button")].find((b) => b.textContent === "Probar sonido: predeterminado de Windows");
  assert.ok(test1 && test2);
  await m.click(test1);
  await settle(1600);
  await m.click(test2);
  assert.deepEqual(backend.sent("audio.test").map((c) => c.payload.target), ["church", "default"]);

  const resume = m.container.querySelector('[data-testid="audio-settings"] input[type="checkbox"]');
  assert.equal(resume.checked, false, "reanudar sola está apagado por defecto");
  controller.dispose();
});

test("sin conexión todos los controles quedan desactivados y la barra no manda nada", async () => {
  const { backend, controller } = await setup();
  const m = mount(controller, backend.output(), false);
  await m.render(); await settle();
  const silence = m.byLabel("Silencio: calla las pistas y pausa el video");
  assert.equal(silence.disabled, true);
  await m.click(silence);
  assert.equal(backend.sent("media.stopAll").length, 0);
  assert.equal(AUX.id, "id-aux");
  controller.dispose();
});
