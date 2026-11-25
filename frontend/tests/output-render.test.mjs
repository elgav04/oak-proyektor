import assert from "node:assert/strict";
import test, { after } from "node:test";
import { setupDom, sleep } from "./dom.mjs";
import { closeServer, loadTs } from "./helper.mjs";

const win = setupDom();
const { act } = await import("react");
const { createRoot } = await import("react-dom/client");
const { createElement } = await import("react");
const { OutputScreen } = await loadTs("/src/output/OutputScreen.tsx");
const { nextView } = await loadTs("/src/output/freeze.ts");
const roots = [];
after(async () => { await act(async () => { for (const r of roots) r.unmount(); }); });   // desmontar detiene los motores de fondo (su vigilancia usa un intervalo)
after(closeServer);

const THEME = { fontFamily: "Arial", fontSize: 72, color: "#FFFFFF", outlineWidth: 0, outlineColor: "#000000", shadowBlur: 0, shadowOffset: 0, shadowColor: "#000000", align: "center", verticalAlign: "middle", lineHeight: 1.2, marginTop: 50, marginRight: 50, marginBottom: 50, marginLeft: 50, autoFit: true, minFontSize: 24 };
const VIDEO = { kind: "video", color: "#102030", mediaUrl: "file:///C:/v/a.mp4", fileName: "a.mp4", muted: true, transitionMs: 0, warning: null };
/** Espera a que terminen los fundidos encadenados (cada paso solo se programa al terminar el anterior). */
const settle = async (m, expectedTexts) => {
  for (let i = 0; i < 40; i++) {
    await act(async () => { await sleep(40); });
    if (m.texts().length === expectedTexts) return;
  }
};
const slide = (id, text = id) => ({ id, blocks: [{ text }], theme: THEME });
const state = (master, s, background = VIDEO, transitionMs = 20) => ({ schemaVersion: 2, revision: 0, master, background, content: { slide: s, transitionMs }, overlays: { items: [] } });

function mount() {
  const container = win.document.createElement("div");
  win.document.body.appendChild(container);
  const root = createRoot(container, { onCaughtError: () => undefined });   // los errores que el boundary captura (a proposito) no deben fallar el test
  roots.push(root);
  let failures = [];
  const render = async (st, seq) => { await act(async () => { root.render(createElement(OutputScreen, { state: st, seq, onBackgroundFailure: (f) => failures.push(f) })); }); };
  return { container, root, render, failures: () => failures, video: () => container.querySelector("video"), texts: () => [...container.querySelectorAll(".content-text")].map((n) => n.textContent) };
}

test("el video de fondo NO se remonta ni se reinicia al cambiar el texto 50 veces seguidas: mismo nodo DOM y currentTime que continua", async () => {
  const m = mount();
  await m.render(state("live", slide("s0", "texto 0"), VIDEO, 20), 1);
  const video = m.video();
  assert.ok(video, "hay un <video>");
  video.dispatchEvent(new win.Event("loadeddata"));
  video.currentTime = 12.5;
  const loads = video.getAttribute("src");

  for (let i = 1; i <= 50; i++) {
    await m.render(state("live", slide(`s${i}`, `texto ${i}`), { ...VIDEO }, 20), i + 1);   // objeto de fondo NUEVO en cada snapshot (como llega del IPC)
    assert.equal(m.video(), video, `el nodo cambio en el cambio ${i}`);
    assert.equal(video.currentTime, 12.5, `currentTime se reinicio en el cambio ${i}`);
    assert.equal(video.getAttribute("src"), loads);
    assert.equal(m.container.querySelectorAll("video").length, 1);
    assert.ok(m.texts().length >= 1 && m.texts().every((t) => t.length > 0), `hubo un instante sin contenido en el cambio ${i}`);
  }
  await settle(m, 1);
  assert.equal(m.video(), video);
  assert.equal(m.texts().length, 1);
  assert.equal(m.texts()[0], "texto 50", "el ultimo texto es el que queda");
  assert.equal(video.currentTime, 12.5);
});

test("cambiar el control maestro (Clear, Black, Logo, Live) no toca el video: se oculta/cubre, no se desmonta", async () => {
  const m = mount();
  await m.render(state("live", slide("s1")), 1);
  const video = m.video();
  video.currentTime = 7;
  let seq = 1;
  for (const master of ["clearText", "black", "logo", "live", "black", "live"]) {
    await m.render(state(master, slide("s1")), ++seq);
    assert.equal(m.video(), video, master);
    assert.equal(video.currentTime, 7);
    const layer = (name) => m.container.querySelector(`[data-layer="${name}"]`);
    if (master === "black") assert.equal(layer("black").style.display, "block");
    if (master === "live") { assert.equal(layer("black").style.display, "none"); assert.equal(layer("content").style.visibility, "visible"); }
    if (master === "clearText") assert.equal(layer("content").style.visibility, "hidden");
    if (master === "logo") assert.equal(layer("logo").style.display, "flex");
  }
});

test("Clear conserva el slide: al volver a Live reaparece sin fundido", async () => {
  const m = mount();
  await m.render(state("live", slide("s1", "uno")), 1);
  await m.render(state("clearText", slide("s1", "uno")), 2);
  assert.equal(m.container.querySelector('[data-layer="content"]').style.visibility, "hidden");
  assert.equal(m.texts()[0], "uno");   // sigue en el DOM, solo oculto
  await m.render(state("live", slide("s1", "uno")), 3);
  assert.equal(m.container.querySelector('[data-layer="content"]').style.visibility, "visible");
});

test("crossfade con dos buferes: durante el fundido conviven los dos slides y al final queda solo el nuevo", async () => {
  const m = mount();
  await m.render(state("live", slide("a", "primero"), VIDEO, 60), 1);
  await m.render(state("live", slide("b", "segundo"), VIDEO, 60), 2);
  assert.deepEqual(m.texts().sort(), ["primero", "segundo"]);
  const slots = [...m.container.querySelectorAll(".content-slot")];
  assert.deepEqual(slots.map((s) => s.style.opacity).sort(), ["0", "1"]);
  await act(async () => { await sleep(200); });
  assert.deepEqual(m.texts(), ["segundo"]);
});

test("quitar el contenido (clearContent) funde a vacio sin tocar el fondo", async () => {
  const m = mount();
  await m.render(state("live", slide("a", "primero")), 1);
  const video = m.video();
  await m.render(state("live", null), 2);
  await act(async () => { await sleep(150); });
  assert.deepEqual(m.texts(), []);
  assert.equal(m.video(), video);
});

test("el area de texto usa el lienzo de 1920x1080 y los margenes del tema", async () => {
  const m = mount();
  await m.render(state("live", { ...slide("a"), theme: { ...THEME, marginLeft: 100, marginRight: 220, marginTop: 40, marginBottom: 60 } }), 1);
  const slot = m.container.querySelector(".content-slot");
  assert.deepEqual([slot.style.left, slot.style.top, slot.style.width, slot.style.height], ["100px", "40px", "1600px", "980px"]);
  const canvas = m.container.querySelector(".canvas");
  assert.deepEqual([canvas.style.width, canvas.style.height], ["1920px", "1080px"]);
});

test("el fondo que falla informa por onBackgroundFailure y la pantalla no queda vacia", async () => {
  const m = mount();
  await m.render(state("live", slide("a", "texto"), { ...VIDEO, mediaUrl: "file:///C:/v/hevc.mp4", color: "#445566" }), 1);
  const video = m.video();
  Object.defineProperty(video, "error", { configurable: true, value: { code: 4 } });
  await act(async () => { video.dispatchEvent(new win.Event("error")); });
  assert.deepEqual(m.failures().map((f) => [f.reason, f.mediaUrl]), [["unsupportedFormat", "file:///C:/v/hevc.mp4"]]);
  assert.equal(m.texts()[0], "texto");   // el texto sigue
});

// ───── ErrorBoundary: conserva lo ultimo pintado y se reinicia con seq ─────

test("ErrorBoundary: un snapshot que rompe el render conserva el ultimo estado pintado (no negro) y un snapshot posterior (otra seq) lo recupera", async () => {
  const m = mount();
  await m.render(state("live", slide("a", "bueno"), { kind: "color", color: "#1e3a8a", mediaUrl: null, fileName: null, muted: true, transitionMs: 0, warning: null }), 1);
  assert.equal(m.texts()[0], "bueno");
  // el texto llega como objeto: React lanza al renderizarlo
  await m.render(state("live", slide("b", { boom: true }), { kind: "color", color: "#7f1d1d", mediaUrl: null, fileName: null, muted: true, transitionMs: 0, warning: null }, 0), 2);
  assert.equal(m.texts()[0], "bueno", "se conserva lo ultimo pintado");
  assert.ok(m.container.querySelector(".canvas"), "no queda en negro");
  // otra secuencia con un snapshot bueno: se recupera
  await m.render(state("live", slide("c", "recuperado"), { kind: "color", color: "#1e3a8a", mediaUrl: null, fileName: null, muted: true, transitionMs: 0, warning: null }, 0), 3);
  assert.deepEqual(m.texts(), ["recuperado"]);
});

test("H8: un error de render del CONTENIDO no remonta el fondo: mismo <video>, mismo nodo, currentTime intacto, sin parpadeo del color de base", async () => {
  const m = mount();
  await m.render(state("live", slide("a", "bueno"), VIDEO, 0), 1);
  const video = m.video();
  video.dispatchEvent(new win.Event("loadeddata"));
  video.currentTime = 42;
  const src = video.getAttribute("src");

  await m.render(state("live", slide("b", { boom: true }), { ...VIDEO }, 0), 2);   // el contenido rompe el render: entra el fallback del boundary
  assert.equal(m.texts()[0], "bueno", "el contenido conserva lo ultimo pintado");
  assert.equal(m.video(), video, "el fondo NO se remonto");
  assert.equal(video.isConnected, true);
  assert.equal(video.currentTime, 42);
  assert.equal(video.getAttribute("src"), src);
  assert.equal(m.container.querySelectorAll("video").length, 1);

  await m.render(state("live", slide("c", "recuperado"), { ...VIDEO }, 0), 3);      // y al recuperarse tampoco
  assert.equal(m.video(), video);
  assert.equal(video.currentTime, 42);
  assert.deepEqual(m.texts(), ["recuperado"]);
});

test("H8: durante el error de contenido el fondo sigue al snapshot mas reciente (un cambio de fondo se aplica)", async () => {
  const m = mount();
  await m.render(state("live", slide("a", "bueno"), { ...VIDEO, kind: "color", mediaUrl: null, color: "#112233" }, 0), 1);
  await m.render(state("live", slide("b", { boom: true }), { ...VIDEO, kind: "color", mediaUrl: null, color: "#445566" }, 0), 2);
  assert.equal(m.texts()[0], "bueno");
  const base = m.container.querySelector('[data-layer="background"]').firstElementChild;
  assert.equal(base.style.backgroundColor, "#445566");
});

// ───── Freeze + video ─────

test("Freeze: el video SIGUE reproduciendose (mismo nodo, currentTime intacto) mientras texto y capas se retienen", async () => {
  const m = mount();
  let view = nextView(null, { seq: 1, state: state("live", slide("a", "antes")), window: { panelUnresponsive: false, panelAlertOnOutput: false } });
  await m.render(view.shown, view.seq);
  const video = m.video();
  video.dispatchEvent(new win.Event("loadeddata"));
  video.currentTime = 20;
  const plays = video.paused;

  for (const [seq, st] of [[2, state("freeze", slide("a", "antes"))], [3, state("freeze", slide("b", "durante"))], [4, state("freeze", slide("c", "durante 2"), { ...VIDEO, mediaUrl: "file:///C:/v/otro.mp4" })]]) {
    view = nextView(view, { seq, state: st, window: { panelUnresponsive: false, panelAlertOnOutput: false } });
    await m.render(view.shown, view.seq);
  }
  await act(async () => { await sleep(120); });
  assert.equal(m.video(), video, "mismo nodo de video");
  assert.equal(video.getAttribute("src"), "file:///C:/v/a.mp4", "tampoco cambio el fondo");
  assert.equal(video.currentTime, 20);
  assert.equal(video.paused, plays, "no se pauso");
  assert.deepEqual(m.texts(), ["antes"], "el texto quedo congelado");

  view = nextView(view, { seq: 5, state: state("live", slide("c", "durante 2"), { ...VIDEO, mediaUrl: "file:///C:/v/otro.mp4" }), window: { panelUnresponsive: false, panelAlertOnOutput: false } });
  await m.render(view.shown, view.seq);
  await act(async () => { await sleep(120); });
  assert.deepEqual(m.texts(), ["durante 2"], "al descongelar se aplica el ultimo");
});
