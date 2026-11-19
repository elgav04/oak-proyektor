import assert from "node:assert/strict";
import test, { after } from "node:test";
import { closeServer, loadTs } from "./helper.mjs";

const { createRetrier } = await loadTs("/src/ipc/retry.ts");
after(closeServer);

const tick = (ms = 5) => new Promise((r) => setTimeout(r, ms));
/** Espera que no transcurre sola: solo termina al abortarse (kick/stop). Permite controlar el momento exacto de cada reintento. */
const manualSleep = (log) => (ms, signal) => new Promise((resolve) => {
  log.push(ms);
  signal.addEventListener("abort", () => resolve(), { once: true });
});
/** Espera instantánea que solo registra cuánto se pidió esperar. */
const instantSleep = (log) => (ms) => { log.push(ms); return Promise.resolve(); };

test("la espera crece (500, 1 s, 2 s, 4 s, 8 s) y se queda en el tope de 8 s", async () => {
  const delays = [];
  let calls = 0;
  const done = new Promise((resolve) => {
    createRetrier(async () => { if (++calls <= 7) throw new Error("sin backend"); resolve(); }, { sleep: instantSleep(delays) }).start();
  });
  await done;
  assert.deepEqual(delays, [500, 1000, 2000, 4000, 8000, 8000, 8000]);
});

test("reintenta INDEFINIDAMENTE: no se rinde tras N intentos", async () => {
  let calls = 0;
  const done = new Promise((resolve) => {
    createRetrier(async () => { if (++calls <= 300) throw new Error("x"); resolve(); }, { sleep: instantSleep([]) }).start();
  });
  await done;
  assert.equal(calls, 301);
});

test("con tiempos reales también se recupera (espera corta configurable)", async () => {
  let calls = 0;
  const done = new Promise((resolve) => {
    createRetrier(async () => { if (++calls < 4) throw new Error("x"); resolve(); }, { baseMs: 5, maxMs: 20 }).start();
  });
  await done;
  assert.equal(calls, 4);
});

test("kick() despierta la espera al instante y reinicia el crecimiento", async () => {
  const delays = [];
  let calls = 0;
  const r = createRetrier(async () => { calls++; throw new Error("x"); }, { sleep: manualSleep(delays) });
  r.start();
  await tick();
  assert.equal(calls, 1);                    // primer intento falló; ahora espera 500 ms (nunca transcurren solos)
  r.kick();                                  // «Reconectar»
  await tick();
  assert.equal(calls, 2);                    // reintentó YA, sin esperar
  r.kick(); await tick(); r.kick(); await tick();
  assert.equal(calls, 4);
  assert.deepEqual(delays, [500, 500, 500, 500]);   // cada kick reinicia la espera creciente
  r.stop();
});

test("sin kick la espera sigue creciendo entre fallos", async () => {
  const delays = [];
  // La espera cede el hilo (macrotarea): una espera instantánea con una tarea que siempre falla giraría en microtareas sin fin.
  const yieldingSleep = (ms) => { delays.push(ms); return new Promise((r) => setTimeout(r, 1)); };
  const r = createRetrier(async () => { throw new Error("x"); }, { sleep: yieldingSleep });
  r.start();
  // Se espera a que se registren los intentos (no un tiempo fijo): en Windows un setTimeout(1) tarda ~15 ms.
  for (let i = 0; i < 400 && delays.length < 7; i++) await tick(5);
  r.stop();
  assert.deepEqual(delays.slice(0, 7), [500, 1000, 2000, 4000, 8000, 8000, 8000]);
});

test("stop() detiene los reintentos para siempre", async () => {
  let calls = 0;
  const r = createRetrier(async () => { calls++; throw new Error("x"); }, { sleep: manualSleep([]) });
  r.start();
  await tick();
  r.stop();
  await tick(20);
  const after = calls;
  r.kick(); r.start();
  await tick(20);
  assert.equal(calls, after);
});

test("tras un éxito queda en reposo y kick() vuelve a ejecutar la tarea (resincronizar)", async () => {
  let calls = 0;
  const r = createRetrier(async () => { calls++; }, { sleep: instantSleep([]) });
  r.start();
  await tick();
  assert.equal(calls, 1);
  await tick(20);
  assert.equal(calls, 1);                    // en reposo: no repite sola
  r.kick();
  await tick();
  assert.equal(calls, 2);
  r.stop();
});

test("start() mientras ya está esperando reintenta de inmediato en vez de duplicar el bucle", async () => {
  let calls = 0;
  const r = createRetrier(async () => { calls++; throw new Error("x"); }, { sleep: manualSleep([]) });
  r.start(); await tick();
  r.start(); await tick();
  r.start(); await tick();
  assert.equal(calls, 3);                    // un intento por start, sin bucles paralelos
  r.stop();
});