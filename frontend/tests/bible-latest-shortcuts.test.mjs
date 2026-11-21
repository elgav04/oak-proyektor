import assert from "node:assert/strict";
import test, { after } from "node:test";
import { closeServer, loadTs } from "./helper.mjs";

const { createLatestSender } = await loadTs("/src/control/bible/latest.ts");
const { BIBLE_SHORTCUTS, RESERVED_GLOBAL_SHORTCUTS, matchBibleShortcut, matchSearchKey, isTypingTarget } = await loadTs("/src/control/shortcuts.ts");
after(closeServer);

// ───────── «el último gana» con reloj falso ─────────

function fakeClock() {
  let now = 1000;
  const timers = new Map();
  let nextId = 1;
  return {
    clock: { now: () => now, setTimeout: (fn, ms) => { const id = nextId++; timers.set(id, { at: now + ms, fn }); return id; }, clearTimeout: (id) => { timers.delete(id); } },
    advance(ms) {
      const end = now + ms;
      for (;;) {
        const due = [...timers.entries()].filter(([, t]) => t.at <= end).sort((a, b) => a[1].at - b[1].at)[0];
        if (!due) break;
        now = due[1].at; timers.delete(due[0]); due[1].fn();
      }
      now = end;
    },
    pending: () => timers.size,
  };
}

test("el primer envío sale al instante; una ráfaga se reduce al primero y al ÚLTIMO", () => {
  const { clock, advance } = fakeClock();
  const sent = [];
  const s = createLatestSender((v) => sent.push(v), 60, clock);
  for (let i = 1; i <= 50; i++) { s.push(i); advance(2); }   // 50 pulsaciones en 100 ms
  advance(200);
  assert.equal(sent[0], 1);
  assert.equal(sent.at(-1), 50, "lo último que llega a la salida es el último versículo");
  assert.ok(sent.length <= 4, `se enviaron ${sent.length} mensajes en vez de 50`);
  assert.deepEqual(sent, [...sent].sort((a, b) => a - b), "nunca llega un valor viejo después de uno nuevo");
});

test("tras un descanso, el siguiente va al instante; y el pendiente siempre se manda aunque no vuelva a pulsar nada", () => {
  const { clock, advance, pending } = fakeClock();
  const sent = [];
  const s = createLatestSender((v) => sent.push(v), 60, clock);
  s.push("a"); s.push("b");
  assert.deepEqual(sent, ["a"]);
  advance(100);
  assert.deepEqual(sent, ["a", "b"]);
  assert.equal(pending(), 0);
  advance(500);
  s.push("c");
  assert.deepEqual(sent, ["a", "b", "c"]);
});

test("now() (Enter) envía ya y descarta lo pendiente; cancel() lo descarta sin enviar", () => {
  const { clock, advance } = fakeClock();
  const sent = [];
  const s = createLatestSender((v) => sent.push(v), 60, clock);
  s.push(1); s.push(2); s.now(3);
  advance(500);
  assert.deepEqual(sent, [1, 3]);
  s.push(4); s.push(5); s.cancel();
  advance(500);
  assert.deepEqual(sent, [1, 3, 4]);
});

// ───────── mapa de atajos ─────────

const key = (k, over = {}) => ({ key: k, ctrlKey: false, altKey: false, metaKey: false, shiftKey: false, ...over });

test("ningún atajo del mapa lleva Ctrl/Alt y ninguna pulsación de los globales (Ctrl+Alt+Shift+M/B/C/L/R) activa una acción", () => {
  assert.deepEqual([...RESERVED_GLOBAL_SHORTCUTS].sort(), ["Ctrl+Alt+Shift+B", "Ctrl+Alt+Shift+C", "Ctrl+Alt+Shift+L", "Ctrl+Alt+Shift+M", "Ctrl+Alt+Shift+R"]);
  for (const g of RESERVED_GLOBAL_SHORTCUTS) {
    const letter = g.split("+").at(-1);
    for (const k of [letter, letter.toLowerCase()]) assert.equal(matchBibleShortcut(key(k, { ctrlKey: true, altKey: true, shiftKey: true }), null), null, g);
  }
  for (const s of BIBLE_SHORTCUTS) {
    assert.ok(!("ctrl" in s) && !("alt" in s), `${s.action} declara un modificador global`);
    assert.equal(matchBibleShortcut(key(s.key, { ctrlKey: true, shiftKey: s.shift === true }), null), null, `Ctrl+${s.key} no debe activar ${s.action}`);
    assert.equal(matchBibleShortcut(key(s.key, { altKey: true, shiftKey: s.shift === true }), null), null);
  }
});

test("el mapa no tiene dos acciones distintas con la misma tecla y modificadores", () => {
  const seen = new Map();
  for (const s of BIBLE_SHORTCUTS) {
    const id = `${s.key}|${s.shift === true}`;
    assert.ok(!seen.has(id) || seen.get(id) === s.action, `choque en ${id}`);
    seen.set(id, s.action);
  }
});

test("cada acción del mapa se reconoce", () => {
  const expected = { "/": "focusSearch", ArrowDown: "verseNext", ArrowUp: "versePrev", Enter: "project", Escape: "clear", ArrowRight: "pageNext", PageDown: "pageNext", ArrowLeft: "pagePrev", PageUp: "pagePrev" };
  for (const [k, action] of Object.entries(expected)) assert.equal(matchBibleShortcut(key(k), null), action, k);
  assert.equal(matchBibleShortcut(key("ArrowDown", { shiftKey: true }), null), "extendNext");
  assert.equal(matchBibleShortcut(key("ArrowUp", { shiftKey: true }), null), "extendPrev");
});

test("la «/» funciona en el teclado español, donde es Mayús+7", () => {
  assert.equal(matchBibleShortcut(key("/", { shiftKey: true }), null), "focusSearch");
});

test("con el foco en un cuadro de texto, lista desplegable o editable no se interpreta ninguna tecla; en un botón, Enter es del botón", () => {
  for (const tagName of ["INPUT", "TEXTAREA", "SELECT"]) assert.equal(matchBibleShortcut(key("ArrowDown"), { tagName }), null, tagName);
  assert.equal(matchBibleShortcut(key("/"), { tagName: "DIV", isContentEditable: true }), null);
  assert.equal(matchBibleShortcut(key("Enter"), { tagName: "BUTTON" }), null);
  assert.equal(matchBibleShortcut(key("ArrowDown"), { tagName: "BUTTON" }), "verseNext");
  assert.equal(isTypingTarget({ tagName: "input" }), true);
  assert.equal(isTypingTarget(null), false);
});

test("teclas del cuadro de búsqueda", () => {
  assert.equal(matchSearchKey(key("ArrowDown")), "moveDown");
  assert.equal(matchSearchKey(key("ArrowUp")), "moveUp");
  assert.equal(matchSearchKey(key("Enter")), "submit");
  assert.equal(matchSearchKey(key("Escape")), "clear");
  assert.equal(matchSearchKey(key("a")), null);
  assert.equal(matchSearchKey(key("Enter", { ctrlKey: true })), null);
});
