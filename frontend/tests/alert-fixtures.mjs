// «Backend» SINTÉTICO de las alertas para los tests del panel: habla el protocolo REAL (la versión sale de protocol-version.mjs) con el cliente IPC real. Todos los avisos son
// textos inventados («AVISO DE PRUEBA …»): nada real. Responde alert.getSettings/alert.list, REGISTRA los comandos y «confirma» el estado de la salida como lo haría el backend real
// (evento `output.stateChanged` con la alerta en `overlays.alert`); NADA se marca en pantalla hasta que el backend lo confirma.
import { V } from "./protocol-version.mjs";
import { loadTs } from "./helper.mjs";

const { IpcClient } = await loadTs("/src/ipc/client.ts");

export const ATHEME = { fontFamily: "Arial, sans-serif", fontSize: 52, color: "#FFFFFF", outlineWidth: 0, outlineColor: "#000000", bandColor: "#000000D9", align: "center", position: "bottom", paddingY: 22, maxLines: 2, autoFit: true, minFontSize: 31, tickerSpeed: 180 };

/** Medición SIMULADA (happy-dom no hace layout): una letra mide size×0,5 de ancho, el texto parte en renglones de 1800 px y cada renglón mide size×1,2. */
export const simulatedHeight = (text) => (size) => Math.ceil(text.length / Math.max(1, Math.floor(1800 / (size * 0.5)))) * size * 1.2;

export function createAlertBackend(options = {}) {
  const commands = [];
  const queries = [];
  const saved = new Map();
  let nextId = 1;
  let nextAlert = 1;
  let seq = 1;
  let deliver = () => {};
  const state = {
    settings: { mode: "band", durationSec: 30, theme: { ...ATHEME }, tickerEnabled: options.tickerEnabled ?? true },
    master: "live",
    window: { isOpen: true, isPaused: false },
    alert: null,
    output: null,
  };
  const envelope = (kind, type, payload, id = crypto.randomUUID()) => JSON.stringify({ v: V, id, kind, type, payload });
  const reply = (req, result) => deliver(envelope("reply", req.type, { ok: true, result, error: null }, req.id));
  const fail = (req, code, message) => deliver(envelope("reply", req.type, { ok: false, result: null, error: { code, message } }, req.id));
  const event = (type, payload) => deliver(envelope("event", type, payload));
  const failed = (cmd, code, message) => event("command.failed", { commandId: cmd.id, commandType: cmd.type, error: { code, message } });

  const outputPayload = () => ({
    seq: seq++,
    state: {
      schemaVersion: 3, revision: 0, master: state.master,
      background: { kind: "color", color: "#102030", mediaUrl: null, fileName: null, muted: true, transitionMs: 0, warning: null, retry: 0 },
      content: { slide: null, transitionMs: 0 },
      overlays: { alert: state.alert },
    },
    window: state.window,
  });
  const confirm = () => { state.output = outputPayload(); event("output.stateChanged", state.output); return state.output; };

  const sortedPinned = () => [...saved.values()].filter((a) => a.pinned).sort((a, b) => a.text.localeCompare(b.text));
  // Como el backend real: solo las no fijadas usadas en los últimos 14 días, las 10 más nuevas.
  const recent = () => [...saved.values()].filter((a) => !a.pinned && a.lastUsedAt && Date.parse(a.lastUsedAt) >= Date.now() - 14 * 86400000).sort((a, b) => b.lastUsedAt.localeCompare(a.lastUsedAt)).slice(0, 10);
  const touch = (text) => {
    const key = text.toLowerCase();
    const found = [...saved.values()].find((a) => a.text.toLowerCase() === key);
    if (found) { found.useCount++; found.lastUsedAt = new Date().toISOString(); return; }
    const a = { id: nextId++, text, pinned: false, useCount: 1, lastUsedAt: new Date().toISOString() };
    saved.set(a.id, a);
  };

  const handleQuery = (req) => {
    queries.push(req);
    if (req.type === "alert.getSettings") return reply(req, state.settings);
    if (req.type === "alert.list") return reply(req, { pinned: sortedPinned(), recent: recent() });
    return fail(req, "unknown_message", req.type);
  };

  const handleCommand = (cmd) => {
    commands.push(cmd);
    const p = cmd.payload;
    switch (cmd.type) {
      case "alert.show": {
        const text = String(p.text ?? "").replace(/\s+/g, " ").trim();
        if (text.length === 0 || text.length > 300) return failed(cmd, "alert.invalidContent", "El aviso no es válido.");
        if (p.mode === "ticker" && !state.settings.tickerEnabled) return failed(cmd, "alert.invalidContent", "El desplazamiento está desactivado en los ajustes de las alertas: usa la cinta estática.");
        if (state.window.isPaused) return failed(cmd, "alert.outputPaused", "La salida está en pausa: la alerta no se mostró.");
        const sticky = p.durationSec === 0;
        state.alert = { id: `a${nextAlert++}`, text, mode: p.mode, theme: { ...state.settings.theme }, sticky, expiresAt: new Date(Date.now() + (sticky ? 1800 : p.durationSec) * 1000).toISOString() };
        touch(text);
        return confirm();
      }
      case "alert.hide": if (state.alert) { state.alert = null; confirm(); } return;
      case "alert.setSettings": state.settings = { ...p }; return;
      case "alert.save": {
        const text = String(p.text).trim();
        if (p.pinned && [...saved.values()].filter((a) => a.pinned && a.id !== p.id).length >= 30) return failed(cmd, "alert.full", "Ya hay 30 alertas fijadas.");
        let a = p.id ? saved.get(p.id) : [...saved.values()].find((x) => x.text.toLowerCase() === text.toLowerCase());
        if (p.id && !a) return failed(cmd, "alert.notFound", "La alerta ya no existe.");
        if (!a) { a = { id: nextId++, text, pinned: false, useCount: 0, lastUsedAt: null }; saved.set(a.id, a); }
        a.text = text; a.pinned = p.pinned;
        return event("alert.changed", { commandId: cmd.id, alertId: a.id, kind: "saved" });
      }
      case "alert.delete": saved.delete(p.id); return event("alert.changed", { commandId: cmd.id, alertId: p.id, kind: "deleted" });
      default: return;
    }
  };

  const transport = {
    send: (raw) => {
      const msg = JSON.parse(raw);
      if (msg.v !== V) return;   // como el backend real: otra versión no se interpreta
      queueMicrotask(() => { if (msg.kind === "query") handleQuery(msg); else handleCommand(msg); });
    },
    onMessage: (cb) => { deliver = cb; },
  };
  const client = new IpcClient(transport);

  const seed = (text, { pinned = false, daysAgo = 0 } = {}) => {
    const a = { id: nextId++, text, pinned, useCount: 1, lastUsedAt: new Date(Date.now() - daysAgo * 86400000).toISOString() };
    saved.set(a.id, a);
    return a;
  };

  return {
    client, state, saved, commands, queries, seed, confirm,
    sent: (type) => commands.filter((c) => c.type === type),
    output: () => state.output,
    setMaster: (master) => { state.master = master; return confirm(); },
    setWindow: (w) => { state.window = { ...state.window, ...w }; return confirm(); },
    /** Otra ventana (o el atajo global) quita la alerta: el backend lo confirma. */
    externalHide: () => { state.alert = null; return confirm(); },
  };
}
