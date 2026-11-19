import assert from "node:assert/strict";
import test, { after } from "node:test";
import { closeServer, loadTs } from "./helper.mjs";

const { isNewer } = await loadTs("/src/ipc/sequence.ts");
after(closeServer);

test("el primer snapshot siempre se aplica", () => {
  assert.equal(isNewer(null, 0), true);
  assert.equal(isNewer(null, 5), true);
});

test("solo se aplica una secuencia ESTRICTAMENTE mayor", () => {
  assert.equal(isNewer(5, 6), true);
  assert.equal(isNewer(5, 5), false);   // repetido
  assert.equal(isNewer(5, 4), false);   // viejo
  assert.equal(isNewer(5, 0), false);
});