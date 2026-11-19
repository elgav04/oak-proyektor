import assert from "node:assert/strict";
import test, { after, afterEach } from "node:test";
import { closeServer, loadTs } from "./helper.mjs";

const { createTicker } = await loadTs("/src/ipc/ticker.ts");
after(closeServer);

const realWorker = globalThis.Worker;
afterEach(() => {
  globalThis.Worker = realWorker;
});
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

class FakeWorker {
  static instances = [];
  constructor(url) {
    this.url = url;
    this.terminated = false;
    FakeWorker.instances.push(this);
  }
  terminate() {
    this.terminated = true;
  }
}

test("sin Web Worker (Node) cae a setInterval y se puede detener", async () => {
  delete globalThis.Worker;
  let ticks = 0;
  const stop = createTicker(10, () => { ticks++; });
  await sleep(80);
  stop();
  const atStop = ticks;
  assert.ok(atStop >= 2, `esperaba varios ticks, hubo ${atStop}`);
  await sleep(50);
  assert.equal(ticks, atStop);
});

test("con Worker el reloj es el del worker: el hilo principal solo ejecuta los mensajes que recibe, no un temporizador propio", async () => {
  FakeWorker.instances = [];
  globalThis.Worker = FakeWorker;
  let ticks = 0;
  const stop = createTicker(10, () => { ticks++; });
  await sleep(60);
  assert.equal(ticks, 0);                                   // sin mensajes del worker no hay tick, aunque pase el tiempo (no hay setInterval propio)
  const w = FakeWorker.instances[0];
  w.onmessage({});
  w.onmessage({});
  assert.equal(ticks, 2);
  stop();
  assert.equal(w.terminated, true);
  w.onmessage({});                                          // un mensaje que llega tarde tras detenerlo se ignora
  assert.equal(ticks, 2);
});

test("el script del worker es un Blob que emite al intervalo pedido", async () => {
  FakeWorker.instances = [];
  globalThis.Worker = FakeWorker;
  const stop = createTicker(4000, () => undefined);
  const url = FakeWorker.instances[0].url;
  assert.match(url, /^blob:/);
  stop();
});

test("si crear el worker falla, cae a setInterval", async () => {
  globalThis.Worker = class { constructor() { throw new Error("file:// no permite workers"); } };
  let ticks = 0;
  const stop = createTicker(10, () => { ticks++; });
  await sleep(60);
  stop();
  assert.ok(ticks >= 2);
});

test("si el worker falla después, pasa a setInterval y sigue latiendo", async () => {
  FakeWorker.instances = [];
  globalThis.Worker = FakeWorker;
  let ticks = 0;
  const stop = createTicker(10, () => { ticks++; });
  FakeWorker.instances[0].onerror({});
  await sleep(60);
  stop();
  assert.equal(FakeWorker.instances[0].terminated, true);
  assert.ok(ticks >= 2);
});
