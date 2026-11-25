import assert from "node:assert/strict";
import test, { after } from "node:test";
import { closeServer, loadTs } from "./helper.mjs";

const { decodeSongFile, looksMojibake, MAX_FILE_BYTES } = await loadTs("/src/control/songs/fileDecoder.ts");
after(closeServer);

const utf8 = (text, bom = false) => Uint8Array.from([...(bom ? [0xef, 0xbb, 0xbf] : []), ...new TextEncoder().encode(text)]);
const ACCENTS = "Lumarí señor ñandú tenóko ¿qué? ¡sí! — «cita» €";

test("UTF-8 válido se lee como UTF-8 sin avisos", () => {
  const r = decodeSongFile(utf8(ACCENTS));
  assert.deepEqual([r.ok, r.text, r.encoding, r.fellBack, r.warnings], [true, ACCENTS, "utf-8", false, []]);
});

test("la marca BOM de UTF-8 se detecta y se quita", () => {
  const r = decodeSongFile(utf8(ACCENTS, true));
  assert.deepEqual([r.text, r.encoding], [ACCENTS, "utf-8-bom"]);
});

test("lo que NO es UTF-8 cae a Windows-1252 y lo DICE (la lección del mojibake)", () => {
  // «Lumarí señor ñandú €» en Windows-1252.
  const bytes = Uint8Array.from([...Buffer.from("Lumar"), 0xed, ...Buffer.from(" se"), 0xf1, ...Buffer.from("or "), 0xf1, ...Buffer.from("and"), 0xfa, 0x20, 0x80, 0x20, 0x93, 0x78, 0x94]);
  const r = decodeSongFile(bytes);
  assert.equal(r.text, "Lumarí señor ñandú € “x”");
  assert.deepEqual([r.encoding, r.fellBack], ["windows-1252", true]);
  assert.match(r.warnings[0], /no está en UTF-8.*Windows-1252/);
});

test("se puede forzar la codificación cuando la automática se equivoca", () => {
  const bytes = Uint8Array.from([...Buffer.from("caf"), 0xe9]);
  assert.equal(decodeSongFile(bytes, "windows-1252").text, "café");
  assert.equal(decodeSongFile(bytes, "windows-1252").warnings.some((w) => /elegiste/.test(w)), true);
  const bad = decodeSongFile(bytes, "utf-8");
  assert.equal(bad.ok, false);
  assert.match(bad.message, /no es UTF-8/);
});

test("UTF-16 con BOM, en los dos órdenes de bytes", () => {
  const le = new Uint8Array([0xff, 0xfe, ...new Uint8Array(Buffer.from(ACCENTS, "utf16le"))]);
  const be = Uint8Array.from([0xfe, 0xff, ...Buffer.from(ACCENTS, "utf16le").swap16()]);
  assert.deepEqual([decodeSongFile(le).text, decodeSongFile(le).encoding], [ACCENTS, "utf-16le"]);
  assert.deepEqual([decodeSongFile(be).text, decodeSongFile(be).encoding], [ACCENTS, "utf-16be"]);
});

test("el doble codificado (UTF-8 válido pero dañado) se lee, se avisa y se detecta el mojibake", () => {
  const damaged = "seÃ±or cafÃ©";
  const r = decodeSongFile(utf8(damaged));
  assert.equal(r.text, damaged);
  assert.ok(r.warnings.some((w) => /acentos parecen dañados/.test(w)));
  assert.equal(looksMojibake(damaged), true);
  assert.equal(looksMojibake(ACCENTS), false);
  assert.ok(decodeSongFile(utf8("una línea con � dentro")).warnings.some((w) => w.includes("�")));
});

test("lo que no es texto se rechaza con un mensaje claro", () => {
  const cases = [
    [[0x50, 0x4b, 0x03, 0x04, 0, 0], /Word o PowerPoint/],
    [[...Buffer.from("%PDF-1.4")], /PDF/],
    [[...Buffer.from("{\\rtf1\\ansi")], /RTF/],
    [[0xd0, 0xcf, 0x11, 0xe0, 1, 2], /antiguo de Office/],
    [[0x89, 0x50, 0x4e, 0x47, 13, 10], /imagen/],
    [[0xff, 0xd8, 0xff, 0xe0], /imagen/],
    [[...Buffer.from("GIF89a")], /imagen/],
    [[0x61, 0x62, 0x00, 0x63], /binario/],
  ];
  for (const [bytes, pattern] of cases) {
    const r = decodeSongFile(Uint8Array.from(bytes));
    assert.equal(r.ok, false);
    assert.match(r.message, pattern);
  }
  const noisy = decodeSongFile(Uint8Array.from(Array.from({ length: 400 }, (_, i) => (i % 3 === 0 ? 1 : 0x61))));
  assert.equal(noisy.ok, false);
  assert.match(noisy.message, /caracteres de control/);
});

test("vacío y demasiado grande", () => {
  assert.deepEqual(decodeSongFile(new Uint8Array(0)), { ok: true, text: "", encoding: "utf-8", fellBack: false, warnings: [] });
  const big = decodeSongFile(new Uint8Array(MAX_FILE_BYTES + 1));
  assert.equal(big.ok, false);
  assert.match(big.message, /demasiado grande.*256 KB/);
});
