// «Backend» SINTÉTICO de la reproducción y el audio para los tests del panel: habla el protocolo REAL (la versión sale de protocol-version.mjs) con el cliente IPC real. Todos los nombres
// son inventados («Video de prueba», «Pista de prueba»): nada real. REGISTRA los comandos y «confirma» el estado como lo haría el backend real (`output.stateChanged` con el video en
// `foreground`, `audio.stateChanged`, `media.progress`); NADA se marca como «en pantalla» hasta que el backend lo confirma.
import { V } from "./protocol-version.mjs";
import { loadTs } from "./helper.mjs";

const { IpcClient } = await loadTs("/src/ipc/client.ts");

export const LAPTOP = { id: "id-laptop", name: "Altavoces del portátil" };
export const USB = { id: "id-usb", name: "Interfaz USB (mezcladora)" };
export const AUX = { id: "id-aux", name: "Salida auxiliar de la PC" };

export const SETTINGS = { churchDeviceId: null, churchDeviceName: null, masterVolumePct: 50, trackFadeInMs: 500, trackFadeOutMs: 800, videoFadeInMs: 400, videoFadeOutMs: 400, resumeOnDeviceReturn: false };

export function createPlaybackBackend(options = {}) {
  const commands = [];
  const queries = [];
  let seq = 1;
  let audioSeq = 1;
  let fgCounter = 1;
  let deliver = () => {};
  const state = {
    master: "live",
    window: { isOpen: true, isPaused: false },
    foreground: null,
    devices: options.devices ?? [LAPTOP, USB, AUX],
    defaultId: options.defaultId ?? LAPTOP.id,
    settings: { ...SETTINGS, ...(options.settings ?? {}) },
    track: null,
    defaultChange: null,
    media: new Map(),
    output: null,
    audio: null,
  };
  const envelope = (kind, type, payload, id = crypto.randomUUID()) => JSON.stringify({ v: V, id, kind, type, payload });
  const reply = (req, result) => deliver(envelope("reply", req.type, { ok: true, result, error: null }, req.id));
  const fail = (req, code, message) => deliver(envelope("reply", req.type, { ok: false, result: null, error: { code, message } }, req.id));
  const event = (type, payload) => deliver(envelope("event", type, payload));
  const failed = (cmd, code, message) => event("command.failed", { commandId: cmd.id, commandType: cmd.type, error: { code, message } });

  const hidden = () => !state.window.isOpen || state.window.isPaused || ["clearText", "black", "logo"].includes(state.master);
  const outputPayload = () => ({
    seq: seq++,
    state: {
      schemaVersion: 4, revision: 0, master: state.master,
      background: { kind: "color", color: "#102030", mediaUrl: null, fileName: null, muted: true, transitionMs: 0, warning: null, retry: 0 },
      content: { slide: null, transitionMs: 0 },
      overlays: { alert: null },
      foreground: state.foreground,
    },
    window: state.window,
  });
  const confirm = () => { state.output = outputPayload(); event("output.stateChanged", state.output); return state.output; };

  const route = () => {
    const church = state.settings.churchDeviceId === null ? null : state.devices.find((d) => d.id === state.settings.churchDeviceId) ?? null;
    const dflt = state.devices.find((d) => d.id === state.defaultId) ?? null;
    const usesDefault = state.settings.churchDeviceId === null;
    return {
      usesWindowsDefault: usesDefault, churchDeviceId: state.settings.churchDeviceId, churchDeviceName: church?.name ?? state.settings.churchDeviceName, churchConnected: usesDefault || church !== null,
      defaultDeviceId: dflt?.id ?? null, defaultDeviceName: dflt?.name ?? null, mismatch: !usesDefault && church !== null && church.id !== state.defaultId,
    };
  };
  const audioPayload = () => ({
    seq: audioSeq++, route: route(),
    devices: state.devices.map((d) => ({ id: d.id, name: d.name, isDefault: d.id === state.defaultId, isChurch: d.id === state.settings.churchDeviceId })),
    track: state.track, settings: state.settings, defaultChange: state.defaultChange,
  });
  const confirmAudio = () => { state.audio = audioPayload(); event("audio.stateChanged", state.audio); return state.audio; };

  const handleQuery = (req) => {
    queries.push(req);
    if (req.type === "audio.getState") return reply(req, audioPayload());
    if (req.type === "media.get") { const m = state.media.get(req.payload.id); return m ? reply(req, m) : fail(req, "media.notFound", "No existe."); }
    return fail(req, "unknown_message", req.type);
  };

  const handleCommand = (cmd) => {
    commands.push(cmd);
    const p = cmd.payload;
    switch (cmd.type) {
      case "media.play": {
        const m = state.media.get(p.mediaId);
        if (!m) return failed(cmd, "media.notFound", "No existe.");
        if (m.kind === "image") return failed(cmd, "media.notPlayable", "Una imagen no se reproduce.");
        if (m.kind === "video") {
          const start = (p.startPlaying ?? true) && !hidden();
          state.foreground = {
            id: `f${fgCounter++}`, mediaId: m.id, url: `file:///C:/datos/media/${m.id}.mp4`, fileName: m.name, phase: start ? "playing" : "paused", clipVolumePct: p.volumePct ?? 100,
            volumePct: Math.round(((p.volumePct ?? 100) * state.settings.masterVolumePct) / 100), muted: p.muted, loop: p.loop, seekMs: 0, seekSeq: 0, fadeInMs: 400, fadeOutMs: 400, backdrop: p.backdrop, onEnd: p.onEnd,
          };
          return confirm();
        }
        state.track = { mediaId: m.id, phase: p.startPlaying === true ? "playing" : "paused", positionMs: 0, durationMs: 120_000, loop: p.loop, volumePct: p.volumePct ?? 100, errorCode: null, errorMessage: null };
        return confirmAudio();
      }
      case "media.pause":
        if (p.target === "foreground" && state.foreground?.phase === "playing") { state.foreground = { ...state.foreground, phase: "paused" }; return confirm(); }
        if (p.target === "track" && state.track?.phase === "playing") { state.track = { ...state.track, phase: "paused" }; return confirmAudio(); }
        return;
      case "media.resume":
        if (p.target === "foreground") {
          if (!state.foreground) return failed(cmd, "media.nothingPlaying", "No hay ningún video en primer plano.");
          if (hidden()) return failed(cmd, "media.outputHidden", "La salida no muestra el video: vuelve a «En vivo» para reproducirlo.");
          state.foreground = { ...state.foreground, phase: "playing" };
          return confirm();
        }
        if (!state.track) return failed(cmd, "media.nothingPlaying", "No hay ninguna pista cargada.");
        state.track = { ...state.track, phase: "playing" };
        return confirmAudio();
      case "media.stop":
        if (p.target === "foreground") { state.foreground = null; return confirm(); }
        state.track = null;
        return confirmAudio();
      case "media.stopAll":
        if (state.foreground?.phase === "playing") { state.foreground = { ...state.foreground, phase: "paused" }; confirm(); }
        if (state.track?.phase === "playing") { state.track = { ...state.track, phase: "paused" }; confirmAudio(); }
        return;
      case "audio.setDevice": {
        if (p.deviceId !== null && !state.devices.some((d) => d.id === p.deviceId)) return failed(cmd, "media.deviceNotFound", "El dispositivo no está conectado.");
        state.settings = { ...state.settings, churchDeviceId: p.deviceId, churchDeviceName: p.deviceId === null ? null : state.devices.find((d) => d.id === p.deviceId).name };
        return confirmAudio();
      }
      case "audio.setMasterVolume": state.settings = { ...state.settings, masterVolumePct: p.volumePct }; return confirmAudio();
      case "audio.setSettings": state.settings = { ...p }; return confirmAudio();
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

  return {
    client, state, commands, queries,
    sent: (type) => commands.filter((c) => c.type === type),
    seedMedia: (item) => { const m = { id: state.media.size + 1, name: "Medio de prueba", kind: "video", ...item }; state.media.set(m.id, m); return m; },
    confirm, confirmAudio, emitAudio: (payload) => event("audio.stateChanged", payload),
    output: () => state.output,
    setMaster: (master) => { state.master = master; if (hidden() && state.foreground?.phase === "playing") state.foreground = { ...state.foreground, phase: "paused" }; return confirm(); },
    setWindow: (w) => { state.window = { ...state.window, ...w }; if (hidden() && state.foreground?.phase === "playing") state.foreground = { ...state.foreground, phase: "paused" }; return confirm(); },
    /** Windows cambia el predeterminado por su cuenta (p. ej. al conectar o quitar el USB): el backend lo registra y avisa. */
    windowsChangesDefault: (id) => {
      const from = state.devices.find((d) => d.id === state.defaultId);
      state.defaultId = id;
      state.defaultChange = { fromName: from?.name ?? null, toName: state.devices.find((d) => d.id === id)?.name ?? null, at: "2026-11-29T10:00:00Z" };
      return confirmAudio();
    },
    disconnect: (id) => { state.devices = state.devices.filter((d) => d.id !== id); if (state.defaultId === id) state.defaultId = state.devices[0]?.id ?? null; return confirmAudio(); },
    progress: (payload) => event("media.progress", payload),
    playbackFailed: (payload) => event("media.playbackFailed", payload),
    foreground: () => state.foreground,
  };
}
