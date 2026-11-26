import assert from "node:assert/strict";
import test, { after } from "node:test";
import { closeServer, loadTs } from "./helper.mjs";

const { IpcClient, IpcRequestError } = await loadTs("/src/ipc/client.ts");
after(closeServer);

function make(transportOverrides = {}) {
  const sent = [];
  let deliver = () => {};
  const transport = {
    send: (m) => sent.push(JSON.parse(m)),
    onMessage: (cb) => { deliver = cb; },
    ...transportOverrides,
  };
  const client = new IpcClient(transport);
  return { client, sent, deliver: (o) => deliver(typeof o === "string" ? o : JSON.stringify(o)) };
}
const msg = (kind, type, payload, id = "x") => ({ v:15, id, kind, type, payload });
const ok = (result) => ({ ok: true, result, error: null });
const err = (code, message) => ({ ok: false, result: null, error: { code, message } });
const settlesWithin = (promise, ms) => Promise.race([promise.then(() => "settled", () => "settled"), new Promise((r) => setTimeout(() => r("pending"), ms))]);

test("send envía un comando con el sobre v2", () => {
  const { client, sent } = make();
  client.send("output.setMaster", { master: "live" });
  assert.equal(sent.length, 1);
  assert.deepEqual({ ...sent[0], id: "?" }, { v:15, id: "?", kind: "command", type: "output.setMaster", payload: { master: "live" } });
  assert.match(sent[0].id, /^[0-9a-f-]{36}$/);
});

test("request se resuelve con el RESULT del reply ok", async () => {
  const { client, sent, deliver } = make();
  const p = client.request("display.list", {});
  assert.equal(sent[0].kind, "query");
  deliver(msg("reply", "display.list", ok({ seq: 3, displays: [] }), sent[0].id));
  assert.deepEqual(await p, { seq: 3, displays: [] });
});

test("un reply con error rechaza AL INSTANTE con el error real del servidor (no espera el timeout)", async () => {
  const { client, sent, deliver } = make();
  const p = client.request("output.getState", {}, 5000);   // timeout largo: si esperara, el test lo delataría
  deliver(msg("reply", "output.getState", err("output.broken", "Sin estado disponible"), sent[0].id));

  assert.equal(await settlesWithin(p, 200), "settled");
  await assert.rejects(p, (e) => {
    assert.ok(e instanceof IpcRequestError);
    assert.equal(e.code, "output.broken");
    assert.equal(e.message, "Sin estado disponible");
    return true;
  });
});

test("un reply de error sin detalle utilizable rechaza con code unknown", async () => {
  const { client, sent, deliver } = make();
  const p = client.request("display.list", {});
  deliver(msg("reply", "display.list", { ok: false, result: null, error: null }, sent[0].id));
  await assert.rejects(p, (e) => e instanceof IpcRequestError && e.code === "unknown");
});

test("dos requests simultáneos se correlacionan por id aunque lleguen en desorden", async () => {
  const { client, sent, deliver } = make();
  const a = client.request("display.list", {});
  const b = client.request("output.getState", {});
  deliver(msg("reply", "output.getState", ok({ seq: 9 }), sent[1].id));
  deliver(msg("reply", "display.list", ok({ seq: 4 }), sent[0].id));
  assert.deepEqual(await b, { seq: 9 });
  assert.deepEqual(await a, { seq: 4 });
});

test("un reply con id desconocido o de otro type se ignora y el request vence por timeout", async () => {
  const { client, sent, deliver } = make();
  const p = client.request("display.list", {}, 60);
  deliver(msg("reply", "display.list", ok({}), "otro-id"));
  deliver(msg("reply", "output.getState", ok({}), sent[0].id));
  await assert.rejects(p, (e) => e instanceof IpcRequestError && e.code === "timeout");
});

test("un reply tardío tras el timeout no rompe nada", async () => {
  const { client, sent, deliver } = make();
  const p = client.request("display.list", {}, 30);
  await assert.rejects(p, (e) => e.code === "timeout");
  deliver(msg("reply", "display.list", ok({ seq: 1, displays: [] }), sent[0].id));
});

test("si el transporte falla al enviar, el request rechaza (code transport) en vez de colgarse", async () => {
  const c = new IpcClient({ send: () => { throw new Error("sin puente"); }, onMessage: () => {} });
  await assert.rejects(c.request("display.list", {}, 5000), (e) => e instanceof IpcRequestError && e.code === "transport" && /sin puente/.test(e.message));
});

test("subscribe recibe eventos, se cancela, y un suscriptor que falla no afecta a otro", () => {
  const { client, deliver } = make();
  const got = [];
  const off = client.subscribe("output.stateChanged", (p) => got.push(p));
  client.subscribe("output.stateChanged", () => { throw new Error("roto"); });
  client.subscribe("output.stateChanged", (p) => got.push("segundo:" + p.n));
  deliver(msg("event", "output.stateChanged", { n: 1 }));
  assert.deepEqual(got, [{ n: 1 }, "segundo:1"]);
  off();
  deliver(msg("event", "output.stateChanged", { n: 2 }));
  assert.deepEqual(got, [{ n: 1 }, "segundo:1", "segundo:2"]);
});

test("command.failed llega a sus suscriptores", () => {
  const { client, deliver } = make();
  let got = null;
  client.subscribe("command.failed", (p) => { got = p; });
  deliver(msg("event", "command.failed", { commandId: "c1", commandType: "output.open", error: { code: "output.pageMissing", message: "falta" } }));
  assert.equal(got.error.code, "output.pageMissing");
});

test("mensajes inválidos, de otra versión o desconocidos se ignoran sin lanzar", () => {
  const { client, deliver } = make();
  let calls = 0;
  client.subscribe("display.changed", () => { calls++; });
  for (const bad of ["no es json", "", "[]", "42", "null",
    { v: 1, id: "1", kind: "event", type: "display.changed", payload: {} },          // versión anterior
    { v: 2, id: "1", kind: "event", type: "display.changed", payload: {} },          // versión anterior (v2)
    { v: 99, id: "1", kind: "event", type: "display.changed", payload: {} },         // versión futura
    { v:15, kind: "event", type: "display.changed", payload: {} },                   // sin id
    { v:15, id: "1", kind: "command", type: "display.changed", payload: {} },        // un comando no entra
    { v:15, id: "1", kind: "event", type: "display.changed" },                       // sin payload
    { v:15, id: "1", kind: "event", type: "display.changed", payload: "x" },
    msg("event", "evento.desconocido", {})]) deliver(bad);
  assert.equal(calls, 0);
  deliver(msg("event", "display.changed", { seq: 1, displays: [] }));
  assert.equal(calls, 1);
});

test("un reply sin `ok` (formato v1) se ignora: la query vence por timeout", async () => {
  const { client, sent, deliver } = make();
  const p = client.request("display.list", {}, 40);
  deliver(msg("reply", "display.list", { displays: [] }, sent[0].id));
  await assert.rejects(p, (e) => e.code === "timeout");
});

test("onActivity avisa con CUALQUIER mensaje válido (evento o reply) y no con los inválidos", () => {
  const { client, sent, deliver } = make();
  let n = 0;
  const off = client.onActivity(() => { n++; });
  deliver("basura");
  deliver({ v: 1, id: "1", kind: "event", type: "x", payload: {} });
  assert.equal(n, 0);
  deliver(msg("event", "evento.sin.suscriptores", {}));
  void client.request("display.list", {}, 50).catch(() => undefined);
  deliver(msg("reply", "display.list", ok({}), sent[0].id));
  assert.equal(n, 2);
  off();
  deliver(msg("event", "otro", {}));
  assert.equal(n, 2);
});