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
const { visibilityFor } = await loadTs("/src/output/visibility.ts");
const alertLib = await loadTs("/src/output/alert.ts");
const { isHideAlertKey, isEmergencyKey } = await loadTs("/src/output/emergency.ts");
const roots = [];
after(async () => { await act(async () => { for (const r of roots) r.unmount(); }); });
after(closeServer);

// ───── Medición SIMULADA (happy-dom no hace layout): una letra mide size×0,5 de ancho y el texto parte en renglones de 1800 px ─────
const CHAR_W = 0.5;
const textHeight = (el) => {
  const size = parseFloat(el.style.fontSize);
  const perLine = Math.max(1, Math.floor(1800 / (size * CHAR_W)));
  return Math.ceil(el.textContent.length / perLine) * size * 1.2;
};
Object.defineProperty(win.HTMLElement.prototype, "offsetHeight", { configurable: true, get() { return this.classList?.contains("alert-text") ? textHeight(this) : 0; } });
Object.defineProperty(win.HTMLElement.prototype, "scrollWidth", { configurable: true, get() { return this.classList?.contains("alert-ticker-track") ? this.textContent.length * parseFloat(this.style.fontSize) * CHAR_W : 0; } });

const THEME = { fontFamily: "Arial", fontSize: 72, color: "#FFFFFF", outlineWidth: 0, outlineColor: "#000000", shadowBlur: 0, shadowOffset: 0, shadowColor: "#000000", align: "center", verticalAlign: "middle", lineHeight: 1.2, marginTop: 50, marginRight: 50, marginBottom: 50, marginLeft: 50, autoFit: true, minFontSize: 24 };
const ATHEME = { fontFamily: "Arial, sans-serif", fontSize: 52, color: "#FFFFFF", outlineWidth: 0, outlineColor: "#000000", bandColor: "#000000D9", align: "center", position: "bottom", paddingY: 22, maxLines: 2, autoFit: true, minFontSize: 31, tickerSpeed: 180 };
const VIDEO = { kind: "video", color: "#102030", mediaUrl: "file:///C:/v/a.mp4", fileName: "a.mp4", muted: true, transitionMs: 0, warning: null };
const slide = (id, text = id) => ({ id, blocks: [{ text }], theme: THEME });
const alert = (id, text = "AVISO DE PRUEBA 1", over = {}) => ({ id, text, mode: "band", theme: { ...ATHEME }, sticky: false, expiresAt: "2026-11-29T10:00:30.000Z", ...over });
const state = (master, s, background = VIDEO, a = null) => ({ schemaVersion: 3, revision: 0, master, background, content: { slide: s, transitionMs: 20 }, overlays: { alert: a } });

function mount() {
  const container = win.document.createElement("div");
  win.document.body.appendChild(container);
  const root = createRoot(container, { onCaughtError: () => undefined });
  roots.push(root);
  const render = async (st, seq) => { await act(async () => { root.render(createElement(OutputScreen, { state: st, seq, onBackgroundFailure: () => undefined })); }); };
  return {
    container, render,
    video: () => container.querySelector("video"),
    band: () => container.querySelector(".alert-band"),
    text: () => container.querySelector(".alert-text"),
    track: () => container.querySelector(".alert-ticker-track"),
    texts: () => [...container.querySelectorAll(".content-text")].map((n) => n.textContent),
    overlays: () => container.querySelector('[data-layer="overlays"]'),
  };
}
const tick = (ms = 60) => act(async () => { await sleep(ms); });

// ───── cinta estática ─────

test("cinta estática: ancla abajo a todo el ancho, con el fondo de la cinta, y entra con un fundido (opacidad 0 → 1)", async () => {
  const m = mount();
  await m.render(state("live", slide("s1", "texto"), VIDEO, alert("a1")), 1);
  const band = m.band();
  assert.ok(band, "hay una cinta");
  assert.equal(band.getAttribute("data-alert-id"), "a1");
  assert.deepEqual([band.style.left, band.style.width, band.style.bottom], ["0px", "1920px", "0px"]);
  assert.equal(band.style.top, "");
  assert.equal(band.style.backgroundColor === "" ? band.style.background : band.style.backgroundColor !== "", true);
  assert.equal(m.text().textContent, "AVISO DE PRUEBA 1");
  assert.equal(band.style.opacity, "0", "aparece con opacidad 0…");
  await tick(50);
  assert.equal(m.band().style.opacity, "1", "…y pasa a 1 (transición CSS en el compositor)");
  assert.match(band.style.transition, /opacity 200ms/);
});

test("cinta arriba: ancla al borde superior", async () => {
  const m = mount();
  await m.render(state("live", null, VIDEO, alert("a1", "AVISO", { theme: { ...ATHEME, position: "top" } })), 1);
  assert.equal(m.band().style.top, "0px");
  assert.equal(m.band().style.bottom, "");
});

test("el tope de altura de la cinta es el 30 % del lienzo (324 px): nunca una altura fija ni tapar la pantalla", async () => {
  const m = mount();
  await m.render(state("live", null, VIDEO, alert("a1")), 1);
  assert.equal(alertLib.ALERT_MAX_HEIGHT, 324);
  assert.equal(m.band().style.maxHeight, "324px");
  assert.equal(m.band().style.height, "", "la altura sale del contenido");
});

test("autoFit: un aviso corto conserva el tamaño del tema; uno largo baja la letra hasta caber en 2 renglones, siempre entre el mínimo y el tamaño", async () => {
  const m = mount();
  await m.render(state("live", null, VIDEO, alert("a1", "x".repeat(100))), 1);
  assert.equal(m.text().style.fontSize, "52px");

  await m.render(state("live", null, VIDEO, alert("a2", "y".repeat(140))), 2);
  const size = parseFloat(m.text().style.fontSize);
  assert.ok(size < 52 && size >= 31, `tamaño ${size}`);
  assert.ok(alertLib.fitsInLines(textHeight(m.text()), size, 2), "cabe en 2 renglones a ese tamaño");
  assert.ok(!alertLib.fitsInLines(textHeight({ style: { fontSize: `${size + 1}px` }, textContent: "y".repeat(140) }), size + 1, 2), "y es el MAYOR que cabe");
});

test("300 caracteres: la letra baja hasta el mínimo y, si ni así cabe, la cinta crece (hasta su tope) en vez de recortar el texto", async () => {
  const m = mount();
  const long = "z".repeat(300);
  await m.render(state("live", null, VIDEO, alert("a1", long)), 1);
  assert.equal(m.text().style.fontSize, "31px");
  assert.equal(m.text().textContent.length, 300, "el texto llega entero a la pantalla");
  assert.equal(m.band().style.maxHeight, "324px");
  assert.ok(textHeight(m.text()) < 324 - 2 * 22, "con la medición simulada, 3 renglones al mínimo caben bajo el tope");
});

test("sin autoFit se usa el tamaño del tema tal cual", async () => {
  const m = mount();
  await m.render(state("live", null, VIDEO, alert("a1", "w".repeat(300), { theme: { ...ATHEME, autoFit: false } })), 1);
  assert.equal(m.text().style.fontSize, "52px");
});

test("el contorno del tema usa el trazo detrás del relleno", async () => {
  const m = mount();
  await m.render(state("live", null, VIDEO, alert("a1", "AVISO", { theme: { ...ATHEME, outlineWidth: 3, outlineColor: "#123456" } })), 1);
  assert.match(m.text().getAttribute("style"), /paint-order: stroke fill/);   // happy-dom descarta -webkit-text-stroke; el trazo detrás del relleno sí se conserva
});

// ───── desplazamiento (ticker) ─────

test("ticker: una línea que cruza el lienzo a velocidad constante; duración = (1920 + ancho del texto) / velocidad, con la medición simulada", async () => {
  const m = mount();
  const text = "t".repeat(200);   // 200 × 52 × 0,5 = 5200 px
  await m.render(state("live", null, VIDEO, alert("a1", text, { mode: "ticker" })), 1);
  const track = m.track();
  assert.ok(track, "hay un desplazamiento");
  const expected = alertLib.tickerDurationSec(5200, 180);
  assert.equal(expected, 39.56);
  assert.match(track.style.animation, new RegExp(`alert-marquee ${expected}s linear infinite`));
  assert.equal(track.getAttribute("data-duration"), "39.56");
  assert.match(track.style.willChange, /transform/);
  assert.equal(m.container.querySelector(".alert-ticker").style.whiteSpace, "nowrap");
});

test("ticker: la velocidad del tema manda y un snapshot que repite la MISMA alerta no remonta el elemento ni reinicia la animación", async () => {
  const m = mount();
  const text = "t".repeat(100);
  await m.render(state("live", null, VIDEO, alert("a1", text, { mode: "ticker", theme: { ...ATHEME, tickerSpeed: 360 } })), 1);
  const track = m.track();
  const animation = track.style.animation;
  assert.match(animation, new RegExp(`alert-marquee ${alertLib.tickerDurationSec(2600, 360)}s`));

  for (let i = 2; i < 30; i++) {
    // Objeto NUEVO en cada snapshot, con otra caducidad (renovada): como llega del IPC cuando se vuelve a mostrar el mismo aviso.
    await m.render(state("live", null, VIDEO, alert("a1", text, { mode: "ticker", expiresAt: `2026-11-29T10:00:${String(i).padStart(2, "0")}.000Z`, theme: { ...ATHEME, tickerSpeed: 360 } })), i);
    assert.equal(m.track(), track, `se remontó en el snapshot ${i}`);
    assert.equal(m.track().style.animation, animation);
  }
});

test("ticker: otra alerta (otro id) reinicia el recorrido con el texto nuevo", async () => {
  const m = mount();
  await m.render(state("live", null, VIDEO, alert("a1", "t".repeat(100), { mode: "ticker" })), 1);
  const first = m.track();
  await m.render(state("live", null, VIDEO, alert("a2", "u".repeat(400), { mode: "ticker" })), 2);
  assert.notEqual(m.track(), first);
  assert.equal(m.track().textContent.length, 400);
  assert.equal(m.track().getAttribute("data-duration"), String(alertLib.tickerDurationSec(400 * 52 * CHAR_W, 180)));
});

test("tickerDurationSec: recorrido = lienzo + texto; nunca menos de 1 s ni con medidas inválidas", () => {
  assert.equal(alertLib.tickerDurationSec(0, 180), 10.67);
  assert.equal(alertLib.tickerDurationSec(1800, 180), 20.67);
  assert.equal(alertLib.tickerDurationSec(Number.NaN, 180), 10.67);
  assert.equal(alertLib.tickerDurationSec(1000, 0), 16.22);     // velocidad inválida → 180
  assert.ok(alertLib.tickerDurationSec(0, 400, 100) >= 1);
});

// ───── reemplazo, ocultar ─────

test("reemplazar una alerta cambia el texto EN EL SITIO: la misma cinta, sin hueco, sin fundido de salida y de entrada", async () => {
  const m = mount();
  await m.render(state("live", null, VIDEO, alert("a1", "AVISO UNO")), 1);
  await tick(50);
  const band = m.band();
  assert.equal(band.style.opacity, "1");
  for (const [i, id] of ["a2", "a3", "a4"].entries()) {
    await m.render(state("live", null, VIDEO, alert(id, `AVISO ${id}`)), i + 2);
    assert.equal(m.band(), band, "la cinta no se remonta");
    assert.equal(m.band().style.opacity, "1", "nunca baja la opacidad al reemplazar");
    assert.equal(m.text().textContent, `AVISO ${id}`);
  }
});

test("quitar la alerta: se desvanece (opacidad 0) y luego sale del DOM; volver a mostrarla antes de que termine la conserva", async () => {
  const m = mount();
  await m.render(state("live", null, VIDEO, alert("a1")), 1);
  await tick(50);
  await m.render(state("live", null, VIDEO, null), 2);
  assert.equal(m.band().style.opacity, "0", "empieza el fundido de salida");
  await tick(400);
  assert.equal(m.band(), null, "ya no está en el DOM");
  assert.equal(m.overlays().getAttribute("data-alert"), "");

  await m.render(state("live", null, VIDEO, alert("a2", "OTRO")), 3);
  await tick(50);
  assert.equal(m.band().style.opacity, "1");
  await m.render(state("live", null, VIDEO, null), 4);
  const leaving = m.band();
  await m.render(state("live", null, VIDEO, alert("a3", "OTRO MÁS")), 5);   // vuelve antes de que termine el fundido de salida
  await tick(300);
  assert.equal(m.band(), leaving);
  assert.equal(m.band().style.opacity, "1");
});

// ───── controles maestros ─────

test("controles maestros: Texto limpio, Negro y Logo ocultan la alerta (el estado se conserva); Live y Freeze la muestran", async () => {
  const m = mount();
  let seq = 0;
  for (const [master, hidden] of [["live", false], ["clearText", true], ["black", true], ["logo", true], ["live", false], ["freeze", false]]) {
    await m.render(state(master, slide("s1"), VIDEO, alert("a1")), ++seq);
    assert.equal(m.overlays().style.visibility, hidden ? "hidden" : "visible", master);
    assert.ok(m.band(), `${master}: la alerta sigue en el DOM (solo oculta)`);
  }
  assert.equal(visibilityFor("freeze").overlays, true);
});

// ───── Freeze: la alerta NO se congela ─────

const NOWIN = { panelUnresponsive: false, panelAlertOnOutput: false };

test("Freeze: el texto y el fondo se retienen, pero MOSTRAR y QUITAR la alerta sigue funcionando", async () => {
  const m = mount();
  let view = nextView(null, { seq: 1, state: state("live", slide("a", "antes")), window: NOWIN });
  await m.render(view.shown, view.seq);
  assert.equal(m.band(), null);

  // Se congela, y mientras tanto llegan texto nuevo, fondo nuevo Y una alerta.
  view = nextView(view, { seq: 2, state: state("freeze", slide("b", "durante"), { ...VIDEO, mediaUrl: "file:///C:/v/otro.mp4" }, alert("a1")), window: NOWIN });
  await m.render(view.shown, view.seq);
  await tick(50);
  assert.deepEqual(m.texts(), ["antes"], "el texto quedó congelado");
  assert.equal(m.video().getAttribute("src"), "file:///C:/v/a.mp4", "el fondo también");
  assert.equal(m.band().getAttribute("data-alert-id"), "a1", "pero la alerta SE MUESTRA");
  assert.equal(m.overlays().style.visibility, "visible");

  // Congelado, la alerta se reemplaza y se QUITA (lo que hace el atajo global o la caducidad).
  view = nextView(view, { seq: 3, state: state("freeze", slide("c", "durante 2"), VIDEO, alert("a2", "OTRO")), window: NOWIN });
  await m.render(view.shown, view.seq);
  assert.equal(m.text().textContent, "OTRO");
  view = nextView(view, { seq: 4, state: state("freeze", slide("c", "durante 2"), VIDEO, null), window: NOWIN });
  await m.render(view.shown, view.seq);
  await tick(350);
  assert.equal(m.band(), null, "quitada aunque Freeze siga activo");
  assert.deepEqual(m.texts(), ["antes"], "y el texto sigue congelado");
  assert.equal(view.frozen, true);
});

test("nextView con Freeze conserva la misma instancia si la alerta no cambió (no re-renderiza de más)", () => {
  const first = nextView(null, { seq: 1, state: state("live", slide("a")), window: NOWIN });
  const same = alert("a1");
  const overlays = { alert: same };
  const frozen = nextView(first, { seq: 2, state: { ...state("freeze", slide("b")), overlays }, window: NOWIN });
  const again = nextView(frozen, { seq: 3, state: { ...state("freeze", slide("b")), overlays }, window: NOWIN });
  assert.equal(again.shown, frozen.shown);
  assert.equal(frozen.shown.content, first.shown.content);
});

// ───── la alerta no toca el video ni el crossfade ─────

test("alternar la alerta 60 veces (mostrar, reemplazar, quitar) NO remonta el video ni reinicia su currentTime ni dispara el crossfade del texto", async () => {
  const m = mount();
  await m.render(state("live", slide("s1", "texto estable"), VIDEO, null), 1);
  const video = m.video();
  video.dispatchEvent(new win.Event("loadeddata"));
  video.currentTime = 33.3;
  const src = video.getAttribute("src");
  const slotOpacities = () => [...m.container.querySelectorAll(".content-slot")].map((s) => s.style.opacity);
  const slotNodes = [...m.container.querySelectorAll(".content-slot")];
  const baseline = slotOpacities();

  for (let i = 0; i < 60; i++) {
    const a = i % 3 === 2 ? null : alert(`a${i}`, `AVISO ${i}`, { mode: i % 2 ? "ticker" : "band" });
    await m.render(state("live", slide("s1", "texto estable"), { ...VIDEO }, a), i + 2);   // fondo y slide NUEVOS como objetos (así llegan del IPC), mismos ids
    assert.equal(m.video(), video, `el nodo del video cambió en el paso ${i}`);
    assert.equal(video.currentTime, 33.3, `currentTime se reinició en el paso ${i}`);
    assert.equal(video.getAttribute("src"), src);
    assert.equal(m.container.querySelectorAll("video").length, 1);
    assert.deepEqual(m.texts(), ["texto estable"], `el texto cambió en el paso ${i}`);
    assert.deepEqual(slotOpacities(), baseline, `empezó un crossfade en el paso ${i}`);
    assert.deepEqual([...m.container.querySelectorAll(".content-slot")], slotNodes, "los búferes del contenido se remontaron");
  }
});

// ───── límite de errores propio ─────

test("un error al pintar la alerta la oculta a ELLA: el texto, el fondo y el video siguen intactos, y la siguiente alerta se recupera", async () => {
  const m = mount();
  await m.render(state("live", slide("s1", "bueno"), VIDEO, null), 1);
  const video = m.video();
  video.dispatchEvent(new win.Event("loadeddata"));
  video.currentTime = 12;

  await m.render(state("live", slide("s1", "bueno"), VIDEO, alert("a1", { boom: true })), 2);   // el texto llega como objeto: React lanza al pintarlo
  assert.equal(m.band(), null, "la alerta rota no se pinta");
  assert.deepEqual(m.texts(), ["bueno"], "el contenido NO cambió ni cayó en su límite general");
  assert.equal(m.video(), video);
  assert.equal(video.currentTime, 12);

  await m.render(state("live", slide("s1", "bueno"), VIDEO, alert("a1", { boom: true })), 3);   // el mismo id roto: no reintenta en bucle
  assert.equal(m.band(), null);

  await m.render(state("live", slide("s2", "nuevo"), VIDEO, alert("a2", "RECUPERADA")), 4);
  await tick(50);
  assert.equal(m.text().textContent, "RECUPERADA");
  assert.equal(m.video(), video);
});

test("una alerta sin tema (snapshot corrupto) tampoco rompe el contenido", async () => {
  const m = mount();
  await m.render(state("live", slide("s1", "bueno"), VIDEO, { id: "a1", text: "X", mode: "band", sticky: false, expiresAt: "" }), 1);
  assert.equal(m.band(), null);
  assert.deepEqual(m.texts(), ["bueno"]);
});

// ───── respaldo de teclado ─────

test("el respaldo de la salida: Ctrl+Alt+Shift+A solo QUITA la alerta y no se confunde con la emergencia (M)", () => {
  const key = (code, mods = { ctrlKey: true, altKey: true, shiftKey: true }) => ({ code, ctrlKey: false, altKey: false, shiftKey: false, ...mods });
  assert.equal(isHideAlertKey(key("KeyA")), true);
  assert.equal(isEmergencyKey(key("KeyA")), false);
  assert.equal(isHideAlertKey(key("KeyM")), false);
  assert.equal(isEmergencyKey(key("KeyM")), true);
  assert.equal(isHideAlertKey(key("KeyA", { ctrlKey: true, altKey: false, shiftKey: true })), false, "sin Alt no es el atajo");
  assert.equal(isHideAlertKey(key("KeyA", { ctrlKey: false, altKey: false, shiftKey: false })), false, "la tecla A sola no hace nada");
});
