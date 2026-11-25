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
const { useControlStore, connectControlStore, reconnectControl, dismissFailure, dismissSettingsWarnings } = await loadTs("/src/control/store.ts");
after(closeServer);

const msg = (kind, type, payload, id = "x") => ({ v:13, id, kind, type, payload });
const send = (o) => deliver(typeof o === "string" ? o : JSON.stringify(o));
const ok = (result) => ({ ok: true, result, error: null });
const tick = (ms = 20) => new Promise((r) => setTimeout(r, ms));
const queries = (type) => sent.filter((m) => m.kind === "query" && m.type === type);
/** Espera a que aparezca una consulta nueva (un intento nuevo) y devuelve cuántas hay. */
const nextAttempt = async (type, from) => {
  for (let i = 0; i < 200 && queries(type).length <= from; i++) await tick(5);
  return queries(type).length;
};
const window_ = (over = {}) => ({ isOpen: false, displayId: null, isWindowed: false, isPaused: false, pauseReason: null, autoRestore: true, emergencyHotkey: null, preferredDisplayId: null, onDisplayLost: "warnAndWait", ...over });
const outputPayload = (seq, master = "black", win = {}) => ({ seq, state: { schemaVersion: 2, revision: seq, master, background: { kind: "none", color: "#000000", mediaUrl: null, fileName: null, muted: true, transitionMs: 0, warning: null, retry: 0 }, content: { slide: null, transitionMs: 400 }, overlays: { items: [] } }, window: window_(win) });

let connection = null;
beforeEach(() => {
  sent.length = 0;
  useControlStore.setState({ connected: false, everConnected: false, displays: [], displaysSeq: null, output: null, database: null, failure: null, settingsWarnings: [] });
});
afterEach(() => { connection?.dispose(); connection = null; });

const answerInitialQueries = (displaysSeq = 1, outputSeq = 1) => {
  send(msg("reply", "display.list", ok({ seq: displaysSeq, displays: [{ id: "0,0,1920x1080" }] }), queries("display.list").at(-1).id));
  send(msg("reply", "output.getState", ok(outputPayload(outputSeq)), queries("output.getState").at(-1).id));
  send(msg("reply", "database.getStatus", ok({ seq: 1, state: "ready", message: null, backupPath: null }), queries("database.getStatus").at(-1).id));
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
const dbStatus = (seq, state, message = null, backupPath = null) => ({ seq, state, message, backupPath });

test("la carga inicial también trae el estado de la base de datos", async () => {
  connection = connectControlStore({ retry: { baseMs: 5, maxMs: 10 }, requestTimeoutMs: 200 });
  await tick();
  assert.equal(queries("database.getStatus").length, 1);
  answerInitialQueries();
  await tick();
  assert.equal(useControlStore.getState().database.state, "ready");
});

test("un fallo de la base de datos llega por evento con el mensaje y la copia de seguridad", async () => {
  connection = connectControlStore({ retry: { baseMs: 5000 }, requestTimeoutMs: 1000 });
  send(msg("event", "database.statusChanged", dbStatus(2, "failed", "Falló la migración 3", "C:/data/backups/oak.v2.db")));
  const db = useControlStore.getState().database;
  assert.deepEqual([db.state, db.message, db.backupPath], ["failed", "Falló la migración 3", "C:/data/backups/oak.v2.db"]);
});

test("un panel que carga DESPUÉS del fallo lo ve igualmente (por la query)", async () => {
  connection = connectControlStore({ retry: { baseMs: 5, maxMs: 10 }, requestTimeoutMs: 200 });
  await tick();
  send(msg("reply", "display.list", ok({ seq: 1, displays: [] }), queries("display.list").at(-1).id));
  send(msg("reply", "output.getState", ok(outputPayload(1)), queries("output.getState").at(-1).id));
  send(msg("reply", "database.getStatus", ok(dbStatus(1, "failed", "disco lleno")), queries("database.getStatus").at(-1).id));
  await tick();
  assert.equal(useControlStore.getState().database.state, "failed");
});

test("el estado de la base de datos también descarta secuencias viejas o repetidas", async () => {
  connection = connectControlStore({ retry: { baseMs: 5000 }, requestTimeoutMs: 1000 });
  send(msg("event", "database.statusChanged", dbStatus(5, "failed", "grave")));
  send(msg("event", "database.statusChanged", dbStatus(4, "ready")));      // viejo: no debe «curar» el fallo
  send(msg("event", "database.statusChanged", dbStatus(5, "ready")));      // repetido
  assert.equal(useControlStore.getState().database.state, "failed");
  send(msg("event", "database.statusChanged", dbStatus(6, "ready")));
  assert.equal(useControlStore.getState().database.state, "ready");
});

// ───────── heartbeat: `connected` vuelve a false si el backend deja de responder ─────────

const HEARTBEAT = { intervalMs: 20, staleAfterMs: 90 };   // tiempos cortos para el test; en producción son 4 s y 12 s
const statusReply = (id) => msg("reply", "database.getStatus", ok({ seq: 1, state: "ready", message: null, backupPath: null }), id);

/** Contesta a cada consulta de database.getStatus (incluido el heartbeat) mientras esté activo: un backend sano. */
const healthyBackend = () => {
  const answered = new Set();
  const timer = setInterval(() => {
    for (const q of queries("database.getStatus")) {
      if (!answered.has(q.id)) { answered.add(q.id); send(statusReply(q.id)); }
    }
  }, 5);
  return () => clearInterval(timer);
};

test("el panel arranca sin haber estado conectado", () => {
  const s = useControlStore.getState();
  assert.equal(s.connected, false);
  assert.equal(s.everConnected, false);
});

test("si el backend deja de responder, connected vuelve a false (y everConnected lo distingue de «nunca conectó»)", async () => {
  connection = connectControlStore({ retry: { baseMs: 5, maxMs: 10 }, requestTimeoutMs: 200, heartbeat: HEARTBEAT });
  await tick(3);                                           // antes del primer latido (20 ms): así las respuestas iniciales van a la carga, no a un latido
  answerInitialQueries();
  await tick(10);
  assert.equal(useControlStore.getState().connected, true);
  assert.equal(useControlStore.getState().everConnected, true);

  await tick(350);                                         // el backend se queda mudo: ni respuestas al heartbeat ni eventos
  assert.equal(useControlStore.getState().connected, false);
  assert.equal(useControlStore.getState().everConnected, true);
});

test("el tráfico inválido no cuenta como señal de vida durante el silencio", async () => {
  connection = connectControlStore({ retry: { baseMs: 5, maxMs: 10 }, requestTimeoutMs: 200, heartbeat: HEARTBEAT });
  await tick(3);                                           // antes del primer latido (20 ms): así las respuestas iniciales van a la carga, no a un latido
  answerInitialQueries();
  await tick(10);
  for (let i = 0; i < 12; i++) {
    send("basura");
    send({ v: 1, id: "1", kind: "event", type: "output.stateChanged", payload: outputPayload(1) });   // protocolo viejo
    await tick(30);
  }
  assert.equal(useControlStore.getState().connected, false);
});

test("al volver cualquier mensaje válido, vuelve a conectado y se pide el estado completo otra vez", async () => {
  connection = connectControlStore({ retry: { baseMs: 5, maxMs: 10 }, requestTimeoutMs: 200, heartbeat: HEARTBEAT });
  await tick(3);                                           // antes del primer latido (20 ms): así las respuestas iniciales van a la carga, no a un latido
  answerInitialQueries();
  await tick(10);
  await tick(300);
  assert.equal(useControlStore.getState().connected, false);
  const loads = queries("display.list").length;

  send(msg("event", "output.stateChanged", outputPayload(9, "live", { isOpen: true })));
  assert.equal(useControlStore.getState().connected, true);
  await tick(40);
  assert.ok(queries("display.list").length > loads, "al reconectar debe resincronizar pidiendo el estado completo");
});

test("mientras el backend responde, el heartbeat lo mantiene conectado aunque no cambie nada", async () => {
  connection = connectControlStore({ retry: { baseMs: 5, maxMs: 10 }, requestTimeoutMs: 200, heartbeat: HEARTBEAT });
  const stop = healthyBackend();
  await tick(3);                                           // antes del primer latido (20 ms): así las respuestas iniciales van a la carga, no a un latido
  answerInitialQueries();
  await tick(10);
  await tick(500);                                        // muchas veces el tiempo de caducidad, sin ningún evento de negocio
  stop();
  assert.equal(useControlStore.getState().connected, true);
  assert.ok(queries("database.getStatus").length >= 5, "el heartbeat debe estar preguntando");
});

test("el heartbeat no acumula consultas si el backend no responde (una sola en vuelo)", async () => {
  connection = connectControlStore({ retry: { baseMs: 5000, maxMs: 5000 }, requestTimeoutMs: 1000, heartbeat: { intervalMs: 10, staleAfterMs: 400 } });
  await tick(150);
  // 15 ticks de 10 ms: sin la regla serían ~15 consultas; con ella, la de la carga inicial más UN heartbeat.
  assert.ok(queries("database.getStatus").length <= 2, `consultas: ${String(queries("database.getStatus").length)}`);
});

test("dispose() detiene el heartbeat", async () => {
  connection = connectControlStore({ retry: { baseMs: 5000 }, requestTimeoutMs: 1000, heartbeat: { intervalMs: 10, staleAfterMs: 30 } });
  await tick(30);
  connection.dispose();
  connection = null;
  const before = queries("database.getStatus").length;
  await tick(120);
  assert.equal(queries("database.getStatus").length, before);
});

const mediaPayload = (seq, retry, fileName = "a.mp4") => {
  const p = outputPayload(seq, "live");
  p.state.background = { kind: "video", color: "#102030", mediaUrl: "file:///C:/v/" + fileName, fileName, muted: true, transitionMs: 0, warning: null, retry };
  return p;
};

test("el aviso de fallo del fondo sigue mientras sea el mismo archivo y se retira al reintentarlo o cambiar de archivo", async () => {
  connection = connectControlStore({ retry: { baseMs: 5000 }, requestTimeoutMs: 1000 });
  send(msg("event", "output.stateChanged", mediaPayload(1, 0)));
  send(msg("event", "output.backgroundFailed", { reason: "readError", message: "no se pudo leer", fileName: "a.mp4" }));
  assert.equal(useControlStore.getState().backgroundFailure?.fileName, "a.mp4");

  send(msg("event", "output.stateChanged", mediaPayload(2, 0)));            // otro snapshot, mismo fondo: el aviso sigue
  assert.notEqual(useControlStore.getState().backgroundFailure, null);

  send(msg("event", "output.stateChanged", mediaPayload(3, 1)));            // «Reintentar» / reaplicar el mismo archivo: sube retry
  assert.equal(useControlStore.getState().backgroundFailure, null);

  send(msg("event", "output.backgroundFailed", { reason: "readError", message: "otra vez", fileName: "a.mp4" }));   // si vuelve a fallar, aviso nuevo
  assert.equal(useControlStore.getState().backgroundFailure?.message, "otra vez");
  send(msg("event", "output.stateChanged", mediaPayload(4, 0, "b.mp4")));   // otro archivo: ya no vale
  assert.equal(useControlStore.getState().backgroundFailure, null);
});

// ───── H10: avisos de la carga de settings.json ─────

test("H10: los avisos de settings.json llegan al panel con la carga inicial y el panel funciona aunque esa consulta no se responda", async () => {
  connection = connectControlStore({ retry: { baseMs: 5, maxMs: 10 }, requestTimeoutMs: 200 });
  await tick();
  answerInitialQueries(1, 1);                                 // NO se responde a settings.getStatus
  await tick();
  assert.equal(useControlStore.getState().connected, true);
  assert.deepEqual(useControlStore.getState().settingsWarnings, []);
  assert.equal(queries("settings.getStatus").length, 1, "se pidió");

  send(msg("reply", "settings.getStatus", ok({ warnings: ["El tema de la Biblia no es válido en settings.json: se usa el tema por defecto."] }), queries("settings.getStatus").at(-1).id));
  await tick();
  assert.deepEqual(useControlStore.getState().settingsWarnings, ["El tema de la Biblia no es válido en settings.json: se usa el tema por defecto."]);
});

test("H10: cerrar el aviso lo quita y no reaparece al volver a pedir el estado", async () => {
  connection = connectControlStore({ retry: { baseMs: 5, maxMs: 10 }, requestTimeoutMs: 200 });
  await tick();
  answerInitialQueries(1, 1);
  await tick();
  send(msg("reply", "settings.getStatus", ok({ warnings: ["aviso"] }), queries("settings.getStatus").at(-1).id));
  await tick();
  assert.deepEqual(useControlStore.getState().settingsWarnings, ["aviso"]);
  dismissSettingsWarnings();
  assert.deepEqual(useControlStore.getState().settingsWarnings, []);

  const before = queries("settings.getStatus").length;       // el panel vuelve a cargar el estado completo (reconexión)
  reconnectControl();
  await nextAttempt("display.list", queries("display.list").length - 1);
  answerInitialQueries(2, 2);
  await tick();
  const status = queries("settings.getStatus");
  if (status.length > before) send(msg("reply", "settings.getStatus", ok({ warnings: ["aviso"] }), status.at(-1).id));
  await tick();
  assert.deepEqual(useControlStore.getState().settingsWarnings, [], "el aviso cerrado no vuelve");
});