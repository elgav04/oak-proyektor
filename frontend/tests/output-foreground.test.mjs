import assert from "node:assert/strict";
import test, { after } from "node:test";
import { setHidden, setupDom, sleep, trackVideos } from "./dom.mjs";
import { closeServer, loadTs } from "./helper.mjs";

const win = setupDom();
trackVideos(win);
const { act, createElement } = await import("react");
const { createRoot } = await import("react-dom/client");
const { createForegroundPlayer } = await loadTs("/src/output/foreground/player.ts");
const { OutputScreen } = await loadTs("/src/output/OutputScreen.tsx");
const { nextView } = await loadTs("/src/output/freeze.ts");
const { visibilityFor } = await loadTs("/src/output/visibility.ts");
const { isSilenceKey } = await loadTs("/src/output/emergency.ts");
const players = [];
const roots = [];
after(async () => {
  await act(async () => { for (const r of roots) r.unmount(); });
  for (const p of players) p.dispose();   // la vigilancia usa intervalos: sin esto el proceso de la prueba no termina
});
after(closeServer);

const URL_A = "file:///C:/datos/media/files/ab/aaa.mp4";
const URL_B = "file:///C:/datos/media/files/cd/bbb.mp4";
const spec = (id, over = {}) => ({ id, url: URL_A, phase: "playing", volumePct: 50, muted: false, loop: false, seekMs: 0, seekSeq: 0, fadeInMs: 0, fadeOutMs: 0, backdrop: "black", ...over });
const fire = (el, type) => el.dispatchEvent(new win.Event(type));
const setError = (el, code) => Object.defineProperty(el, "error", { configurable: true, value: { code } });
const fast = { progressMs: 20, quietMs: 20, stallMs: 150, loadTimeoutMs: 400 };

function setup(options = {}) {
  const container = win.document.createElement("div");
  win.document.body.appendChild(container);
  const reports = [];
  const player = createForegroundPlayer(container, { onReport: (r) => reports.push(r), ...fast, ...options });
  players.push(player);
  return { container, player, reports, videos: () => [...container.querySelectorAll("video")], boxes: () => [...container.querySelectorAll("[data-foreground]")] };
}

test("un video en primer plano se crea con la URL validada, detrás de un fondo NEGRO, oculto hasta decodificar su primer fotograma", () => {
  const { player, videos, boxes } = setup();
  player.apply(spec("f1", { fadeInMs: 300 }), true);
  const [v] = videos();
  assert.equal(v.getAttribute("src"), URL_A);
  assert.equal(boxes()[0].style.opacity, "0");           // aún no hay fotograma: no se muestra (queda el fondo de debajo)
  assert.match(boxes()[0].style.cssText, /background:\s*(#000|rgb\(0, 0, 0\))/);   // las franjas de un video que no es 16:9 no dejan ver el fondo animado
  fire(v, "loadeddata");
  assert.equal(boxes()[0].style.opacity, "1");
  assert.match(boxes()[0].style.transition, /opacity 300ms/);   // fundido de entrada
});

test("con «mostrar el fondo» detrás, el contenedor no pinta negro", () => {
  const { player, boxes } = setup();
  player.apply(spec("f1", { backdrop: "background" }), true);
  assert.doesNotMatch(boxes()[0].style.cssText, /background:\s*(#000|rgb\(0, 0, 0\))/);
});

test("reproduce con sonido al volumen efectivo (rampa corta), y el mismo id NO remonta ni reinicia el elemento", async () => {
  const { player, videos } = setup();
  player.apply(spec("f1", { volumePct: 40 }), true);
  const [v] = videos();
  fire(v, "loadeddata");
  assert.ok(v.__plays >= 1, "play() se pidió");
  assert.equal(v.muted, false);
  await sleep(80);
  assert.ok(Math.abs(v.volume - 0.4) < 0.01, `volumen ${v.volume}`);

  v.currentTime = 21.5;
  const plays = v.__plays;
  for (let i = 0; i < 100; i++) player.apply(spec("f1", { volumePct: 40, url: URL_A }), true);   // objeto nuevo en cada snapshot
  assert.deepEqual(videos(), [v]);
  assert.equal(v.currentTime, 21.5);
  assert.equal(v.__plays, plays, "no se vuelve a llamar a play() sobre un video que ya suena");
  assert.equal(v.__loads, 0);
});

test("un cambio de volumen o de silencio se aplica sin remontar", async () => {
  const { player, videos } = setup({ stallMs: 60_000 });   // en happy-dom currentTime no avanza solo: sin esto la vigilancia lo daría por atascado
  player.apply(spec("f1", { volumePct: 50 }), true);
  const [v] = videos();
  fire(v, "loadeddata");
  player.apply(spec("f1", { volumePct: 20 }), true);
  await sleep(250);
  assert.ok(Math.abs(v.volume - 0.2) < 0.01);
  player.apply(spec("f1", { volumePct: 20, muted: true }), true);
  assert.equal(v.muted, true);
  assert.deepEqual(videos(), [v]);
});

test("pausar baja el volumen y pausa SIN soltar el elemento: la posición se conserva; reanudar continúa donde iba", async () => {
  const { player, videos } = setup();
  player.apply(spec("f1"), true);
  const [v] = videos();
  fire(v, "loadeddata");
  v.currentTime = 33;
  player.apply(spec("f1", { phase: "paused" }), true);
  await sleep(300);
  assert.equal(v.paused, true);
  assert.equal(v.volume, 0);
  assert.equal(v.currentTime, 33);
  assert.equal(v.isConnected, true);
  player.apply(spec("f1", { phase: "playing" }), true);
  assert.equal(v.paused, false);
  assert.equal(v.currentTime, 33);
  assert.deepEqual(videos(), [v]);
});

test("un control maestro que lo oculta (visible=false) lo oculta AL INSTANTE y lo calla aunque el estado aún diga «playing»; al volver no se reanuda solo", async () => {
  const { player, videos, boxes } = setup();
  player.apply(spec("f1"), true);
  const [v] = videos();
  fire(v, "loadeddata");
  v.currentTime = 12;
  player.apply(spec("f1", { phase: "playing" }), false);
  assert.equal(boxes()[0].style.opacity, "0");
  await sleep(300);
  assert.equal(v.paused, true);
  assert.equal(v.volume, 0);
  assert.equal(v.currentTime, 12);

  // El backend ya lo pausó (phase paused); volver a Live lo muestra pero NO lo reproduce.
  player.apply(spec("f1", { phase: "paused" }), true);
  assert.equal(boxes()[0].style.opacity, "1");
  assert.equal(v.paused, true);
  await sleep(40);
  assert.equal(v.paused, true);
});

test("buscar: la posición se aplica cuando seekSeq cambia, y dos «buscar» al mismo punto se distinguen", () => {
  const { player, videos } = setup();
  player.apply(spec("f1"), true);
  const [v] = videos();
  fire(v, "loadeddata");
  player.apply(spec("f1", { seekMs: 5000, seekSeq: 1 }), true);
  assert.equal(v.currentTime, 5);
  v.currentTime = 9;
  player.apply(spec("f1", { seekMs: 5000, seekSeq: 1 }), true);   // mismo pedido: no se repite
  assert.equal(v.currentTime, 9);
  player.apply(spec("f1", { seekMs: 5000, seekSeq: 2 }), true);   // otro «buscar» a la misma posición
  assert.equal(v.currentTime, 5);
});

test("un video nuevo (otro id) libera el anterior DE VERDAD; quitarlo hace el fundido de salida y también lo libera", async () => {
  const { player, videos } = setup();
  player.apply(spec("f1"), true);
  const [a] = videos();
  fire(a, "loadeddata");
  player.apply(spec("f2", { url: URL_B, fadeOutMs: 0 }), true);
  const b = videos().find((x) => x !== a);
  assert.equal(b.getAttribute("src"), URL_B);
  await sleep(120);
  assert.equal(a.isConnected, false);
  assert.equal(a.hasAttribute("src"), false);
  assert.ok(a.__loads >= 1, "load() suelta el decodificador y el archivo");
  assert.equal(a.paused, true);

  fire(b, "loadeddata");
  player.apply(spec("f2", { url: URL_B, fadeOutMs: 40 }), true);
  player.apply(null, true);
  assert.equal(b.isConnected, true);   // sale con fundido
  await sleep(200);
  assert.equal(b.isConnected, false);
  assert.equal(b.hasAttribute("src"), false);
  assert.deepEqual(videos(), []);
});

test("informa el avance REAL mientras suena y al instante cuando cambia el estado", async () => {
  const { player, videos, reports } = setup();
  player.apply(spec("f1"), true);
  const [v] = videos();
  fire(v, "loadeddata");
  Object.defineProperty(v, "duration", { configurable: true, value: 90 });
  v.currentTime = 4.2;
  await sleep(90);
  const playing = reports.filter((r) => r.id === "f1" && r.state === "playing");
  assert.ok(playing.length >= 2, "varios informes mientras suena");
  const last = playing.at(-1);
  assert.equal(last.positionMs, 4200);
  assert.equal(last.durationMs, 90000);
  assert.equal(last.errorCode, null);

  const before = reports.length;
  player.apply(spec("f1", { phase: "paused" }), true);
  await sleep(60);
  assert.ok(reports.slice(before).some((r) => r.state === "paused"));
});

test("al terminar (sin bucle) informa «ended» UNA vez; con bucle no", () => {
  const a = setup();
  a.player.apply(spec("f1"), true);
  const [v] = a.videos();
  fire(v, "loadeddata");
  fire(v, "ended");
  fire(v, "ended");
  assert.equal(a.reports.filter((r) => r.state === "ended").length, 1);

  const b = setup();
  b.player.apply(spec("f2", { loop: true }), true);
  const [w] = b.videos();
  assert.equal(w.loop, true);
  fire(w, "loadeddata");
  fire(w, "ended");
  assert.equal(b.reports.filter((r) => r.state === "ended").length, 0);
});

test("los fallos duros informan su código y dejan de mostrar el video (nunca negro ni congelado)", () => {
  for (const [code, expected] of [[4, "unsupportedFormat"], [3, "decodeError"], [2, "readError"]]) {
    const { player, videos, reports, boxes } = setup();
    player.apply(spec("f1"), true);
    const [v] = videos();
    fire(v, "loadeddata");
    setError(v, code);
    fire(v, "error");
    const err = reports.filter((r) => r.state === "error");
    assert.equal(err.length, 1);
    assert.equal(err[0].errorCode, expected);
    assert.equal(boxes()[0].style.opacity, "0");
  }
});

test("un video que se atasca (currentTime no avanza) o que nunca decodifica se da por fallido con «stalled»", async () => {
  const a = setup();
  a.player.apply(spec("f1"), true);
  const [v] = a.videos();
  fire(v, "loadeddata");
  v.currentTime = 3;
  await sleep(350);   // stallMs 150: no avanzó
  assert.ok(a.reports.some((r) => r.state === "error" && r.errorCode === "stalled"));

  const b = setup();
  b.player.apply(spec("f2"), true);   // nunca llega «loadeddata»
  await sleep(550);   // loadTimeoutMs 400
  assert.ok(b.reports.some((r) => r.state === "error" && r.errorCode === "stalled"));
});

test("con la ventana oculta no se juzga un atasco ni la carga (Chromium difiere la carga y pausa el video)", async () => {
  const { player, videos, reports } = setup();
  setHidden(win, true);
  try {
    player.apply(spec("f1"), true);
    await sleep(550);
    assert.equal(reports.filter((r) => r.state === "error").length, 0);
    assert.equal(videos().length, 1);
  } finally {
    setHidden(win, false);
  }
});

test("si el navegador rechaza el sonido, se reintenta silenciado antes de rendirse", async () => {
  const { player, videos, reports } = setup();
  player.apply(spec("f1", { phase: "paused" }), true);   // se crea en pausa: aún no pidió play()
  const [v] = videos();
  let calls = 0;
  v.play = () => {
    calls++;
    if (calls === 1) return Promise.reject(Object.assign(new Error("sin gesto"), { name: "NotAllowedError" }));
    return Promise.resolve();
  };
  player.apply(spec("f1", { phase: "playing" }), true);
  await sleep(30);
  assert.equal(calls, 2);
  assert.equal(v.muted, true);
  assert.equal(reports.filter((r) => r.state === "error").length, 0);
});

test("dispose libera todo, incluidos los que están saliendo", () => {
  const { player, videos, container } = setup();
  player.apply(spec("f1"), true);
  player.apply(spec("f2", { url: URL_B, fadeOutMs: 5000 }), true);
  player.apply(null, true);
  player.dispose();
  assert.deepEqual(videos(), []);
  assert.equal(container.children.length, 0);
});

// ───────────── integración con la salida real ─────────────

const THEME = { fontFamily: "Arial", fontSize: 72, color: "#FFFFFF", outlineWidth: 0, outlineColor: "#000000", shadowBlur: 0, shadowOffset: 0, shadowColor: "#000000", align: "center", verticalAlign: "middle", lineHeight: 1.2, marginTop: 50, marginRight: 50, marginBottom: 50, marginLeft: 50, autoFit: true, minFontSize: 24 };
const BG = { kind: "video", color: "#102030", mediaUrl: "file:///C:/v/fondo.mp4", fileName: "fondo.mp4", muted: true, transitionMs: 0, warning: null, retry: 0 };
const fg = (over = {}) => ({ id: "f1", mediaId: 7, url: URL_A, fileName: "v.mp4", phase: "playing", clipVolumePct: 100, volumePct: 50, muted: false, loop: false, seekMs: 0, seekSeq: 0, fadeInMs: 0, fadeOutMs: 0, backdrop: "black", onEnd: "back", ...over });
const full = (master, text, foreground, alert = null) => ({ schemaVersion: 4, revision: 0, master, background: BG, content: { slide: { id: `s-${text}`, blocks: [{ text }], theme: THEME }, transitionMs: 0 }, overlays: { alert }, foreground });

function mount() {
  const container = win.document.createElement("div");
  win.document.body.appendChild(container);
  const root = createRoot(container, { onCaughtError: () => undefined });
  roots.push(root);
  const reports = [];
  const render = async (st, seq) => { await act(async () => { root.render(createElement(OutputScreen, { state: st, seq, onForegroundReport: (r) => reports.push(r) })); }); };
  return { container, render, reports, layer: () => container.querySelector('[data-layer="foreground"]'), fgVideo: () => container.querySelector('[data-layer="foreground"] video'), bgVideo: () => container.querySelector('[data-layer="background"] video') };
}

test("el video en primer plano y el de fondo conviven: cambiar el texto 30 veces no remonta ni uno ni otro (mismo nodo, currentTime continuo)", async () => {
  const m = mount();
  await m.render(full("live", "t0", fg()), 1);
  const front = m.fgVideo();
  const back = m.bgVideo();
  assert.ok(front && back && front !== back);
  fire(front, "loadeddata"); fire(back, "loadeddata");
  front.currentTime = 8.5; back.currentTime = 20.25;
  for (let i = 1; i <= 30; i++) {
    await m.render(full("live", `t${i}`, fg()), i + 1);   // objetos nuevos en cada snapshot
    assert.equal(m.fgVideo(), front, `el video en primer plano cambió de nodo en ${i}`);
    assert.equal(m.bgVideo(), back, `el fondo cambió de nodo en ${i}`);
    assert.equal(front.currentTime, 8.5);
    assert.equal(back.currentTime, 20.25);
  }
});

test("Negro, Texto limpio y Logo ocultan el video en primer plano sin desmontarlo; Freeze NO lo oculta", async () => {
  const m = mount();
  await m.render(full("live", "x", fg()), 1);
  const front = m.fgVideo();
  fire(front, "loadeddata");
  const box = () => m.layer().querySelector("[data-foreground]");
  assert.equal(box().style.opacity, "1");
  let seq = 1;
  for (const master of ["black", "clearText", "logo"]) {
    await m.render(full(master, "x", fg({ phase: "paused" })), ++seq);   // el backend lo pausa al ocultarlo
    assert.equal(m.fgVideo(), front, master);
    assert.equal(box().style.opacity, "0", `visible en ${master}`);
    await act(async () => { await sleep(250); });   // la bajada de volumen antes de pausar
    assert.equal(front.paused, true, `sigue sonando en ${master}`);
  }
  await m.render(full("live", "x", fg({ phase: "paused" })), ++seq);
  assert.equal(box().style.opacity, "1");
  assert.equal(front.paused, true, "volver a Live no lo reanuda");
  await m.render(full("freeze", "x", fg({ phase: "playing" })), ++seq);
  assert.equal(box().style.opacity, "1");   // Freeze no lo oculta
  assert.equal(m.fgVideo(), front);
});

test("sin video en primer plano la capa existe y está vacía; con un snapshot de la versión anterior (sin el campo) tampoco falla", async () => {
  const m = mount();
  await m.render(full("live", "x", null), 1);
  assert.ok(m.layer());
  assert.equal(m.fgVideo(), null);
  const { foreground: _omit, ...old } = full("live", "x", null);
  await m.render(old, 2);
  assert.equal(m.fgVideo(), null);
});

test("quitar el video funde y libera su elemento; el fondo de debajo no se toca", async () => {
  const m = mount();
  await m.render(full("live", "x", fg({ fadeOutMs: 30 })), 1);
  const front = m.fgVideo();
  const back = m.bgVideo();
  fire(front, "loadeddata"); fire(back, "loadeddata");
  back.currentTime = 11;
  await m.render(full("live", "x", null), 2);
  await act(async () => { await sleep(250); });
  assert.equal(front.isConnected, false);
  assert.equal(front.hasAttribute("src"), false);
  assert.equal(m.bgVideo(), back);
  assert.equal(back.currentTime, 11);
});

// ───────────── Freeze, visibilidad y atajo de respaldo ─────────────

const snap = (seq, st) => ({ seq, state: st, window: { panelUnresponsive: false, panelAlertOnOutput: false } });

test("Freeze congela el texto y el fondo pero el video en primer plano SIGUE el estado más reciente", () => {
  let v = nextView(null, snap(1, full("live", "uno", fg())));
  v = nextView(v, snap(2, full("freeze", "uno", fg())));
  assert.equal(v.frozen, true);
  v = nextView(v, snap(3, full("freeze", "dos", fg({ phase: "paused" }))));   // el texto cambia (retenido); el video se pausa (se aplica)
  assert.equal(v.shown.content.slide.id, "s-uno");
  assert.equal(v.shown.foreground.phase, "paused");
  v = nextView(v, snap(4, full("freeze", "tres", null)));                      // el video se quita con Freeze: también se aplica
  assert.equal(v.shown.foreground, null);
  assert.equal(v.shown.content.slide.id, "s-uno");

  const same = nextView(v, snap(5, full("freeze", "cuatro", null)));
  assert.equal(same.shown, v.shown, "sin cambios del video ni de la alerta no hay re-render");
});

test("solo Live y Freeze muestran el video en primer plano", () => {
  assert.deepEqual(["live", "freeze", "clearText", "black", "logo"].map((m) => visibilityFor(m).foreground), [true, true, false, false, false]);
});

test("el respaldo de «Silencio» es Ctrl+Alt+Shift+S exacto y no se confunde con los otros", () => {
  const key = (code, mods = {}) => ({ code, ctrlKey: true, altKey: true, shiftKey: true, ...mods });
  assert.equal(isSilenceKey(key("KeyS")), true);
  assert.equal(isSilenceKey(key("KeyS", { altKey: false })), false);
  assert.equal(isSilenceKey(key("KeyM")), false);
  assert.equal(isSilenceKey(key("KeyA")), false);
});
