import assert from "node:assert/strict";
import test, { after, afterEach, beforeEach } from "node:test";
import { closeServer, loadTs } from "./helper.mjs";

// El cliente IPC de la página se crea al importar `instance`; se le da un puente falso ANTES de cargar el store.
const sent = [];
let deliver = () => {};
globalThis.window = {
  external: {
    sendMessage: (m) => sent.push(JSON.parse(m)),
    receiveMessage: (cb) => { deliver = cb; },
  },
};
const { useControlStore, connectControlStore, reconnectControl, dismissFailure } = await loadTs("/src/control/store.ts");
after(closeServer);

const msg = (kind, type, payload, id = "x") => ({ v: 2, id, kind, type, payload });
const send = (o) => deliver(typeof o === "string" ? o : JSON.stringify(o));
const ok = (result) => ({ ok: true, result, error: null });
const tick = (ms = 20) => new Promise((r) => setTimeout(r, ms));
const queries = (type) => sent.filter((m) => m.kind === "query" && m.type === type);
/** Espera a que aparezca una consulta nueva (un intento nuevo) y devuelve cuántas hay. */
const nextAttempt = async (type, from) => {
  for (let i = 0; i < 200 && queries(type).length <= from; i++) await tick(5);
  return queries(type).length;
};
const window_ = (over = {}) => ({ isOpen: false, displayId: null, isWindowed: false, isPaused: false, pauseReason: null, autoRestore: true, emergencyHotkey: null, ...over });
const outputPayload = (seq, master = "black", win = {}) => ({ seq, state: { revision: seq, master, backgroundColor: "#000", text: null }, window: window_(win) });

let connection = null;
beforeEach(() => {
  sent.length = 0;
  useControlStore.setState({ connected: false, displays: [], displaysSeq: null, output: null, failure: null });
});
afterEach(() => { connection?.dispose(); connection = null; });

const answerInitialQueries = (displaysSeq = 1, outputSeq = 1) => {
  send(msg("reply", "display.list", ok({ seq: displaysSeq, displays: [{ id: "0,0,1920x1080" }] }), queries("display.list").at(-1).id));
  send(msg("reply", "output.getState", ok(outputPayload(outputSeq)), queries("output.getState").at(-1).id));
};

test("la carga inicial deja el estado y marca conectado", async () => {
  connection = connectControlStore({ retry: { baseMs: 5, maxMs: 10 }, requestTimeoutMs: 200 });
  await tick();
  assert.equal(queries("display.list").length, 1);
  answerInitialQueries(3, 7);
  await tick();
  const s = useControlStore.getState();
  assert.equal(s.connected, true);
  assert.equal(s.displaysSeq, 3);
  assert.equal(s.output.seq, 7);
  assert.deepEqual(s.displays, [{ id: "0,0,1920x1080" }]);
});

test("se marca conectado con CUALQUIER mensaje válido, aunque la carga inicial no haya terminado", async () => {
  connection = connectControlStore({ retry: { baseMs: 5000 }, requestTimeoutMs: 1000 });
  await tick();
  assert.equal(useControlStore.getState().connected, false);
  send(msg("event", "output.stateChanged", outputPayload(1)));
  assert.equal(useControlStore.getState().connected, true);   // las queries siguen sin responder
});

test("un mensaje inválido NO marca conectado", async () => {
  connection = connectControlStore({ retry: { baseMs: 5000 }, requestTimeoutMs: 1000 });
  send("basura");
  send({ v: 1, id: "1", kind: "event", type: "output.stateChanged", payload: outputPayload(1) });   // protocolo viejo
  assert.equal(useControlStore.getState().connected, false);
});

test("snapshots con secuencia menor o IGUAL se descartan; solo el más nuevo manda", async () => {
  connection = connectControlStore({ retry: { baseMs: 5000 }, requestTimeoutMs: 1000 });
  send(msg("event", "output.stateChanged", outputPayload(5, "live", { isOpen: true })));
  send(msg("event", "output.stateChanged", outputPayload(4, "black", { isOpen: false })));      // viejo, llega tarde
  send(msg("event", "output.stateChanged", outputPayload(5, "logo", { isOpen: false })));       // repetido (misma seq, otro contenido)
  let s = useControlStore.getState();
  assert.equal(s.output.seq, 5);
  assert.equal(s.output.state.master, "live");
  assert.equal(s.output.window.isOpen, true);

  send(msg("event", "output.stateChanged", outputPayload(6, "black", { isOpen: true, isPaused: true, pauseReason: "displayLost" })));
  s = useControlStore.getState();
  assert.equal(s.output.seq, 6);
  assert.equal(s.output.window.isPaused, true);
});

test("el estado de la ventana de salida también avanza solo con secuencia nueva (aunque la revisión del estado no cambie)", async () => {
  connection = connectControlStore({ retry: { baseMs: 5000 }, requestTimeoutMs: 1000 });
  const a = outputPayload(10, "live", { isOpen: true }); a.state.revision = 3;
  const b = outputPayload(11, "live", { isOpen: false }); b.state.revision = 3;   // misma revisión, ventana cerrada
  send(msg("event", "output.stateChanged", a));
  send(msg("event", "output.stateChanged", b));
  assert.equal(useControlStore.getState().output.window.isOpen, false);
});

test("la lista de monitores sigue la misma regla de secuencia", async () => {
  connection = connectControlStore({ retry: { baseMs: 5000 }, requestTimeoutMs: 1000 });
  send(msg("event", "display.changed", { seq: 2, displays: [{ id: "a" }, { id: "b" }] }));
  send(msg("event", "display.changed", { seq: 1, displays: [{ id: "viejo" }] }));
  assert.deepEqual(useControlStore.getState().displays.map((d) => d.id), ["a", "b"]);
});

test("command.failed deja un aviso visible en el store y se puede cerrar", async () => {
  connection = connectControlStore({ retry: { baseMs: 5000 }, requestTimeoutMs: 1000 });
  send(msg("event", "command.failed", { commandId: "c9", commandType: "output.open", error: { code: "output.pageMissing", message: "Falta output.html" } }));
  assert.deepEqual(useControlStore.getState().failure, { commandId: "c9", commandType: "output.open", code: "output.pageMissing", message: "Falta output.html" });
  dismissFailure();
  assert.equal(useControlStore.getState().failure, null);
});

test("si el backend no responde, REINTENTA indefinidamente con espera creciente", async () => {
  connection = connectControlStore({ retry: { baseMs: 5, maxMs: 15 }, requestTimeoutMs: 30 });
  await tick(400);
  assert.ok(queries("display.list").length >= 5, `intentos: ${String(queries("display.list").length)}`);   // sigue intentando
  assert.equal(useControlStore.getState().connected, false);

  await nextAttempt("display.list", queries("display.list").length);                                       // por fin responde, a la consulta VIGENTE
  answerInitialQueries(2, 2);
  await tick(30);
  assert.equal(useControlStore.getState().connected, true);
  assert.equal(useControlStore.getState().displaysSeq, 2);
  const attempts = queries("display.list").length;
  await tick(150);
  assert.equal(queries("display.list").length, attempts);                                                  // cargado: deja de reintentar
});

test("un error del servidor en la carga se reintenta AL MOMENTO, sin esperar el timeout", async () => {
  connection = connectControlStore({ retry: { baseMs: 5, maxMs: 10 }, requestTimeoutMs: 1500 });
  await tick();
  send(msg("reply", "display.list", { ok: false, result: null, error: { code: "internal", message: "x" } }, queries("display.list").at(-1).id));
  await tick(80);
  assert.ok(queries("display.list").length >= 2, "debió reintentar sin esperar el timeout de 60 s");
});

test("«Reconectar» reintenta YA aunque la espera sea larga", async () => {
  connection = connectControlStore({ retry: { baseMs: 60000, maxMs: 60000 }, requestTimeoutMs: 10 });
  await tick(80);                                           // primer intento falló por timeout; ahora esperaría 60 s
  const before = queries("display.list").length;
  assert.equal(before, 1);
  reconnectControl();
  await tick(40);
  assert.equal(queries("display.list").length, before + 1);   // reintento inmediato
});

test("dispose() detiene los reintentos y las suscripciones", async () => {
  connection = connectControlStore({ retry: { baseMs: 5, maxMs: 10 }, requestTimeoutMs: 10 });
  await tick(60);
  connection.dispose();
  const attempts = queries("display.list").length;
  await tick(80);
  assert.equal(queries("display.list").length, attempts);
  send(msg("event", "output.stateChanged", outputPayload(99)));
  assert.equal(useControlStore.getState().output, null);       // ya no escucha
  connection = null;
});