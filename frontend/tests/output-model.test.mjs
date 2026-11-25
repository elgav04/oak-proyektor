import assert from "node:assert/strict";
import test, { after } from "node:test";
import { closeServer, loadTs } from "./helper.mjs";

const { visibilityFor } = await loadTs("/src/output/visibility.ts");
const { nextView } = await loadTs("/src/output/freeze.ts");
const { computeCanvasTransform } = await loadTs("/src/output/canvas.ts");
const { fitFontSize } = await loadTs("/src/output/fit.ts");
const { fadeReducer, initFade } = await loadTs("/src/output/crossfade.ts");
const { themeTextStyle, themeBoxStyle } = await loadTs("/src/output/theme.ts");
const { cleanPath, kindOfPath, THEME_PRESETS } = await loadTs("/src/control/themes.ts");
after(closeServer);

const THEME = THEME_PRESETS.white.theme;
const slide = (id) => ({ id, blocks: [{ text: id }], theme: THEME });
const bg = { kind: "none", color: "#000000", mediaUrl: null, fileName: null, muted: true, transitionMs: 0, warning: null };
const state = (master, slideId = null, background = bg) => ({ schemaVersion: 2, revision: 0, master, background, content: { slide: slideId ? slide(slideId) : null, transitionMs: 400 }, overlays: { items: [] } });
const win = (over = {}) => ({ panelUnresponsive: false, panelAlertOnOutput: false, ...over });
const snap = (seq, st, w = {}) => ({ seq, state: st, window: win(w) });

// ───── controles maestros ─────

test("cada control maestro muestra lo que debe, al instante", () => {
  assert.deepEqual(visibilityFor("live"), { background: true, content: true, overlays: true, black: false, logo: false });
  assert.deepEqual(visibilityFor("clearText"), { background: true, content: false, overlays: false, black: false, logo: false });
  assert.deepEqual(visibilityFor("black"), { background: false, content: false, overlays: false, black: true, logo: false });
  assert.deepEqual(visibilityFor("logo"), { background: false, content: false, overlays: false, black: false, logo: true });
});

// ───── Freeze ─────

test("Freeze conserva lo pintado y retiene los snapshots nuevos", () => {
  let v = nextView(null, snap(1, state("live", "s1")));
  assert.equal(v.shown.content.slide.id, "s1");
  assert.equal(v.frozen, false);

  v = nextView(v, snap(2, state("freeze", "s1")));            // congelar
  assert.equal(v.frozen, true);
  assert.equal(v.shown.content.slide.id, "s1");

  v = nextView(v, snap(3, state("freeze", "s2")));            // llega contenido nuevo mientras está congelado: NO se pinta
  v = nextView(v, snap(4, state("freeze", "s3", { ...bg, kind: "video", mediaUrl: "file:///C:/a.mp4" })));
  assert.equal(v.frozen, true);
  assert.equal(v.shown.content.slide.id, "s1");
  assert.equal(v.shown.background.kind, "none");              // tampoco el cambio de fondo
  assert.equal(v.latest.content.slide.id, "s3");              // pero se guarda el último
  assert.equal(v.seq, 4);                                      // y la secuencia avanza (el boundary de errores se reinicia con ella)
});

test("al descongelar se aplica el ULTIMO snapshot", () => {
  let v = nextView(null, snap(1, state("live", "s1")));
  v = nextView(v, snap(2, state("freeze", "s1")));
  v = nextView(v, snap(3, state("freeze", "s2")));
  v = nextView(v, snap(4, state("freeze", "s3")));
  v = nextView(v, snap(5, state("live", "s3")));
  assert.equal(v.frozen, false);
  assert.equal(v.shown.content.slide.id, "s3");
});

test("el objeto de lo pintado NO cambia durante Freeze: el fondo (video) no recibe nada", () => {
  let v = nextView(null, snap(1, state("live", "s1", { ...bg, kind: "video", mediaUrl: "file:///C:/a.mp4" })));
  const shown = v.shown;
  v = nextView(v, snap(2, state("freeze", "s1")));
  v = nextView(v, snap(3, state("freeze", "s9")));
  assert.equal(v.shown, shown);   // misma referencia: React no re-renderiza, el motor de fondo no se entera
});

test("un snapshot viejo o repetido no se aplica (tampoco durante Freeze)", () => {
  const v1 = nextView(null, snap(5, state("live", "s1")));
  assert.equal(nextView(v1, snap(5, state("live", "s2"))), v1);
  assert.equal(nextView(v1, snap(4, state("live", "s2"))), v1);
});

test("si el primer snapshot ya es Freeze no hay nada pintado que conservar: se pinta ese estado", () => {
  const v = nextView(null, snap(1, state("freeze", "s1")));
  assert.equal(v.shown.content.slide.id, "s1");
  assert.equal(v.frozen, false);
});

test("el aviso de panel sin respuesta no se congela", () => {
  let v = nextView(null, snap(1, state("live", "s1")));
  v = nextView(v, snap(2, state("freeze", "s1"), { panelUnresponsive: true, panelAlertOnOutput: true }));
  assert.equal(v.panelAlert, true);
});

// ───── lienzo virtual ─────

test("el lienzo de 1920x1080 se escala y centra en cualquier ventana", () => {
  assert.deepEqual(computeCanvasTransform(1920, 1080), { scale: 1, x: 0, y: 0 });
  assert.deepEqual(computeCanvasTransform(3840, 2160), { scale: 2, x: 0, y: 0 });
  assert.deepEqual(computeCanvasTransform(960, 540), { scale: 0.5, x: 0, y: 0 });
  assert.deepEqual(computeCanvasTransform(1920, 1200), { scale: 1, x: 0, y: 60 });   // 16:10: barras arriba y abajo
  assert.deepEqual(computeCanvasTransform(2560, 1080), { scale: 1, x: 320, y: 0 });  // ultraancho: barras laterales
  assert.deepEqual(computeCanvasTransform(0, 0), { scale: 1, x: 0, y: 0 });
});

// ───── ajuste automático ─────

test("fitFontSize halla el mayor tamano que cabe con pocas mediciones", () => {
  let calls = 0;
  const fits = (limit) => (s) => { calls++; return s <= limit; };
  assert.equal(fitFontSize(28, 72, fits(100)), 72);
  assert.equal(fitFontSize(28, 72, fits(50)), 50);
  assert.equal(fitFontSize(28, 72, fits(10)), 28);   // ni el minimo cabe: el minimo
  calls = 0;
  fitFontSize(8, 400, fits(137));
  assert.ok(calls <= 12, `demasiadas mediciones: ${calls}`);
  assert.equal(fitFontSize(8, 400, fits(137)), 137);
});

// ───── tema ─────

test("el tema se traduce a CSS (contorno detras del relleno, sombra, alineacion)", () => {
  const s = themeTextStyle({ ...THEME, outlineWidth: 3, outlineColor: "#112233", shadowBlur: 6, shadowOffset: 2, shadowColor: "#000000" }, 60);
  assert.equal(s.fontSize, "60px");
  assert.equal(s.WebkitTextStroke, "6px #112233");
  assert.equal(s.paintOrder, "stroke fill");
  assert.equal(s.textShadow, "0 2px 6px #000000");
  assert.equal(s.textAlign, "center");
  const none = themeTextStyle({ ...THEME, outlineWidth: 0, shadowBlur: 0, shadowOffset: 0 }, 60);
  assert.equal(none.WebkitTextStroke, undefined);
  assert.equal(none.textShadow, undefined);
  const box = themeBoxStyle({ ...THEME, marginLeft: 100, marginRight: 200, marginTop: 50, marginBottom: 70, verticalAlign: "bottom" }, 1920, 1080);
  assert.deepEqual([box.left, box.top, box.width, box.height, box.justifyContent], [100, 50, 1620, 960, "flex-end"]);
});

// ───── crossfade (maquina de estados) ─────

test("crossfade: el slide nuevo entra en el otro bufer y nunca hay hueco", () => {
  let s = initFade(slide("a"));
  assert.deepEqual([s.slots[0].id, s.slots[1], s.active, s.fading], ["a", null, 0, false]);
  s = fadeReducer(s, { type: "request", slide: slide("b"), ms: 400 });
  assert.deepEqual([s.slots[0].id, s.slots[1].id, s.active, s.fading], ["a", "b", 1, true]);   // a sigue presente mientras b entra
  s = fadeReducer(s, { type: "done", fadeId: s.fadeId });
  assert.deepEqual([s.slots[0], s.slots[1].id, s.fading], [null, "b", false]);
});

test("crossfade: una rafaga de 50 cambios se reduce al primero y al ultimo, sin reiniciar el fundido", () => {
  let s = initFade(slide("s0"));
  s = fadeReducer(s, { type: "request", slide: slide("s1"), ms: 400 });
  const firstFade = s.fadeId;
  for (let i = 2; i <= 50; i++) s = fadeReducer(s, { type: "request", slide: slide(`s${i}`), ms: 400 });
  assert.equal(s.fadeId, firstFade);                 // el fundido en curso no se reinicio
  assert.equal(s.slots[s.active].id, "s1");          // lo que entra sigue siendo s1
  assert.equal(s.pending.slide.id, "s50");           // solo se guarda el ultimo pedido
  s = fadeReducer(s, { type: "done", fadeId: firstFade });
  assert.equal(s.slots[s.active].id, "s50");         // al terminar arranca el ultimo
  assert.equal(s.fading, true);
  assert.ok(s.slots[0] || s.slots[1]);
});

test("crossfade: pedir el slide que ya es destino no hace nada; un 'done' ajeno se ignora", () => {
  let s = initFade(slide("a"));
  assert.equal(fadeReducer(s, { type: "request", slide: slide("a"), ms: 400 }), s);
  s = fadeReducer(s, { type: "request", slide: slide("b"), ms: 400 });
  assert.equal(fadeReducer(s, { type: "done", fadeId: s.fadeId + 7 }), s);
  assert.equal(fadeReducer(s, { type: "request", slide: slide("b"), ms: 400 }), s);
});

test("crossfade: quitar el slide funde a vacio; con 0 ms el cambio es instantaneo", () => {
  let s = initFade(slide("a"));
  s = fadeReducer(s, { type: "request", slide: null, ms: 300 });
  assert.equal(s.slots[s.active], null);
  assert.equal(s.slots[1 - s.active].id, "a");       // el anterior se desvanece
  s = fadeReducer(s, { type: "done", fadeId: s.fadeId });
  assert.deepEqual(s.slots, [null, null]);

  let t = initFade(slide("a"));
  t = fadeReducer(t, { type: "request", slide: slide("b"), ms: 0 });
  assert.equal(t.fading, false);
  assert.equal(t.slots[t.active].id, "b");
  assert.equal(t.slots[1 - t.active], null);
});

// ───── panel provisional ─────

test("la ruta pegada se limpia y el tipo de fondo sale de la extension", () => {
  assert.equal(cleanPath('  "C:\\Videos\\fondo.mp4"  '), "C:\\Videos\\fondo.mp4");
  assert.equal(kindOfPath("C:\\a\\b.MP4"), "video");
  assert.equal(kindOfPath("C:\\a\\b.webm"), "video");
  assert.equal(kindOfPath("C:\\a\\b.JPG"), "image");
  assert.equal(kindOfPath("C:\\a\\b.webp"), "image");
  assert.equal(kindOfPath("C:\\a\\b.mkv"), "video");   // desconocida: el backend explica el rechazo
});
