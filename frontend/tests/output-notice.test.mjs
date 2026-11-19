import assert from "node:assert/strict";
import test, { after } from "node:test";
import { closeServer, loadTs } from "./helper.mjs";

const { describeOutput } = await loadTs("/src/control/outputNotice.ts");
after(closeServer);

const display = (id, over = {}) => ({ id, x: 0, y: 0, width: 1920, height: 1080, scale: 1, isPrimary: false, hostsPanel: false, ...over });
const PANEL = display("0,0,1920x1080", { isPrimary: true, hostsPanel: true });
const PROJECTOR = display("1920,0,1920x1080");
const OTHER = display("3840,0,1280x720", { width: 1280, height: 720 });
const win = (over = {}) => ({ isOpen: true, displayId: PROJECTOR.id, isWindowed: false, isPaused: true, pauseReason: "displayLost", autoRestore: true, emergencyHotkey: null, ...over });

test("sin salida abierta o sin pausa no hay aviso", () => {
  assert.equal(describeOutput([PANEL, PROJECTOR], null).notice.kind, "none");
  assert.equal(describeOutput([PANEL, PROJECTOR], win({ isOpen: false, isPaused: false })).notice.kind, "none");
  assert.equal(describeOutput([PANEL, PROJECTOR], win({ isPaused: false, pauseReason: null })).notice.kind, "none");
});

test("proyector desconectado y ningún otro monitor utilizable: «Proyector desconectado»", () => {
  const d = describeOutput([PANEL], win());
  assert.equal(d.notice.kind, "displayLost");
  assert.equal(d.canResumeInPlace, false);          // el monitor no está: «Reanudar salida» no puede funcionar
});

test("proyector desconectado pero hay OTRO monitor libre: aviso de monitor distinto con ese monitor como opción, y no se reanuda en el sitio", () => {
  const d = describeOutput([PANEL, OTHER], win());
  assert.equal(d.notice.kind, "newDisplay");
  assert.deepEqual(d.notice.alternatives.map((a) => a.id), [OTHER.id]);
  assert.equal(d.canResumeInPlace, false);
});

test("el proyector vuelve con OTRO id (otra resolución o posición): cuenta como monitor distinto, nunca como el mismo", () => {
  const sameSlotOtherResolution = display("1920,0,1280x720", { width: 1280, height: 720 });
  const d = describeOutput([PANEL, sameSlotOtherResolution], win());
  assert.equal(d.notice.kind, "newDisplay");
  assert.deepEqual(d.notice.alternatives.map((a) => a.id), [sameSlotOtherResolution.id]);
});

test("con tres monitores, todos los libres se ofrecen y el del panel nunca", () => {
  const d = describeOutput([PANEL, OTHER, display("5120,0,1920x1080")], win());
  assert.equal(d.notice.kind, "newDisplay");
  assert.deepEqual(d.notice.alternatives.map((a) => a.id), [OTHER.id, "5120,0,1920x1080"]);
});

test("el monitor que aloja el panel nunca es una alternativa (la salida lo taparía)", () => {
  const d = describeOutput([PANEL, OTHER], win());
  assert.ok(!d.notice.alternatives.some((a) => a.hostsPanel));
  // Con el panel en OTHER, el monitor antiguo del panel queda libre y pasa a ser la única alternativa.
  const moved = describeOutput([display(PANEL.id), display(OTHER.id, { hostsPanel: true })], win());
  assert.deepEqual(moved.notice.alternatives.map((a) => a.id), [PANEL.id]);
});

test("si el proyector ya está conectado de nuevo NO se dice «desconectado»: la salida espera al operador y se puede reanudar en el sitio", () => {
  const d = describeOutput([PANEL, PROJECTOR, OTHER], win({ autoRestore: false }));
  assert.equal(d.notice.kind, "displayBack");
  assert.equal(d.canResumeInPlace, true);
});

test("pausa manual o de emergencia: aviso con su motivo, se reanuda en el sitio si el monitor sigue", () => {
  for (const reason of ["manual", "emergency", "panelCrossed"]) {
    const d = describeOutput([PANEL, PROJECTOR], win({ pauseReason: reason }));
    assert.deepEqual(d.notice, { kind: "paused", reason });
    assert.equal(d.canResumeInPlace, true);
  }
});

test("pausa manual con el proyector ausente: no se puede reanudar en el sitio, y no se llama «monitor nuevo»", () => {
  const d = describeOutput([PANEL, OTHER], win({ pauseReason: "emergency" }));
  assert.deepEqual(d.notice, { kind: "paused", reason: "emergency" });
  assert.equal(d.canResumeInPlace, false);
});

test("salida en ventana normal (sin monitor asignado) se reanuda en el sitio", () => {
  const d = describeOutput([PANEL], win({ displayId: null, isWindowed: true, pauseReason: "manual" }));
  assert.equal(d.notice.kind, "paused");
  assert.equal(d.canResumeInPlace, true);
});

test("pausa porque el panel se arrastró al monitor de la salida: «Reanudar» no se ofrece (taparía al operador); en ventana normal sí", () => {
  const panelOnProjector = [display(PANEL.id, { isPrimary: true }), { ...PROJECTOR, hostsPanel: true }];
  const d = describeOutput(panelOnProjector, win({ pauseReason: "panelCrossed" }));
  assert.deepEqual(d.notice, { kind: "paused", reason: "panelCrossed" });
  assert.equal(d.canResumeInPlace, false);
  assert.equal(describeOutput(panelOnProjector, win({ pauseReason: "manual", isWindowed: true })).canResumeInPlace, true);
});

test("sin lista de monitores todavía no se afirma nada sobre el monitor", () => {
  const d = describeOutput([], win());
  assert.deepEqual(d.notice, { kind: "paused", reason: "displayLost" });
  assert.equal(d.canResumeInPlace, true);
});
