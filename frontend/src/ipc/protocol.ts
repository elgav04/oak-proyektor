// Reflejo manual de OakProyektor.Ipc / OakProyektor.Core. Un test de contrato (backend/tests) compara:
// PROTOCOL_VERSION, MESSAGE_CATALOG, MASTER_MODES y los campos de cada interfaz "payload" con los tipos de C#.
// Mantén las interfaces planas (sin objetos anidados en línea) para que ese test pueda leerlas.

/**
 * v2: snapshots con `seq` global creciente (las réplicas descartan seq <= la última); monitores por `id` (no por índice);
 * replies `{ ok, result, error }`; evento `command.failed`; `output.setMaster` y `output.recover` van por un carril de
 * prioridad en el backend (el orden entre carriles no está garantizado; dentro de cada uno sí).
 * v3: `output.recover` lleva `{ reason: "manual" | "emergency" }` (el atajo de respaldo de la salida se distingue del botón del panel).
 */
export const PROTOCOL_VERSION = 4;

export type MessageKind = "command" | "event" | "query" | "reply";

export const MESSAGE_CATALOG = [
  { type: "output.open", kind: "command" },
  { type: "output.close", kind: "command" },
  { type: "output.setMaster", kind: "command" },
  { type: "output.recover", kind: "command" },
  { type: "settings.setAutoRestore", kind: "command" },
  { type: "settings.setDisplayLostPolicy", kind: "command" },
  { type: "display.list", kind: "query" },
  { type: "display.list", kind: "reply" },
  { type: "output.getState", kind: "query" },
  { type: "output.getState", kind: "reply" },
  { type: "display.changed", kind: "event" },
  { type: "output.stateChanged", kind: "event" },
  { type: "command.failed", kind: "event" },
  { type: "database.getStatus", kind: "query" },
  { type: "database.getStatus", kind: "reply" },
  { type: "database.statusChanged", kind: "event" },
] as const;

export const MASTER_MODES = ["live", "clearText", "black", "logo", "freeze"] as const;
export type MasterMode = (typeof MASTER_MODES)[number];

export const DATABASE_STATES = ["initializing", "ready", "failed"] as const;
export type DatabaseState = (typeof DATABASE_STATES)[number];

export const PAUSE_REASONS = ["displayLost", "panelCrossed", "manual", "emergency", "duplicateMode", "unstable"] as const;
export type OutputPauseReason = (typeof PAUSE_REASONS)[number];

/** Qué hacer si el monitor de la salida desaparece: avisar y esperar (por defecto) o mover sola al mejor monitor que no sea el del panel. */
export const DISPLAY_LOST_POLICIES = ["warnAndWait", "moveToBest"] as const;
export type DisplayLostPolicy = (typeof DISPLAY_LOST_POLICIES)[number];

export type EmptyPayload = Record<string, never>;

/** `id` es la ÚNICA forma de referirse a un monitor; no uses la posición en la lista. */
export interface DisplayInfo {
  id: string;
  x: number;
  y: number;
  width: number;
  height: number;
  scale: number;
  isPrimary: boolean;
  hostsPanel: boolean;
}

export interface OutputState {
  revision: number;
  master: MasterMode;
  backgroundColor: string;
  text: string | null;
}

export interface OutputOpenPayload {
  displayId: string | null;
}

export interface OutputSetMasterPayload {
  master: MasterMode;
}

export interface DisplayListPayload {
  seq: number;
  displays: DisplayInfo[];
}

/** Quién pide la pausa: el botón «Recuperar control» del panel (`"manual"`) o el atajo de emergencia de la página de salida (`"emergency"`). */
export type RecoverReason = "manual" | "emergency";

export interface OutputRecoverPayload {
  reason: RecoverReason;
}

export interface SetAutoRestorePayload {
  enabled: boolean;
}

export interface SetDisplayLostPolicyPayload {
  policy: DisplayLostPolicy;
}

export interface OutputWindowStatus {
  isOpen: boolean;
  displayId: string | null;
  isWindowed: boolean;
  isPaused: boolean;
  pauseReason: OutputPauseReason | null;
  autoRestore: boolean;
  emergencyHotkey: string | null;
  /** Monitor preferido del operador, al que la salida siempre intenta volver. */
  preferredDisplayId: string | null;
  onDisplayLost: DisplayLostPolicy;
}

export interface OutputStateChangedPayload {
  seq: number;
  state: OutputState;
  window: OutputWindowStatus;
}

export interface IpcErrorPayload {
  code: string;
  message: string;
}

/** Forma común de todo reply. */
export interface IpcReplyFields {
  ok: boolean;
  result: unknown;
  error: IpcErrorPayload | null;
}

/** Payload de un reply, discriminado por `ok`. */
export type ReplyPayload<T> = { ok: true; result: T; error: null } | { ok: false; result: null; error: IpcErrorPayload };

/** Si `state` es `failed`, `message` explica qué pasó y `backupPath` (si lo hay) es la copia previa a la migración, con los datos originales. */
export interface DatabaseStatusPayload {
  seq: number;
  state: DatabaseState;
  message: string | null;
  backupPath: string | null;
}

export interface CommandFailedPayload {
  commandId: string;
  commandType: string;
  error: IpcErrorPayload;
}

/** Payload de cada comando (UI → C#). */
export interface CommandPayloads {
  "output.open": OutputOpenPayload;
  "output.close": EmptyPayload;
  "output.setMaster": OutputSetMasterPayload;
  "output.recover": OutputRecoverPayload;
  "settings.setAutoRestore": SetAutoRestorePayload;
  "settings.setDisplayLostPolicy": SetDisplayLostPolicyPayload;
}

/** Payload de cada evento (C# → UI). */
export interface EventPayloads {
  "display.changed": DisplayListPayload;
  "output.stateChanged": OutputStateChangedPayload;
  "command.failed": CommandFailedPayload;
  "database.statusChanged": DatabaseStatusPayload;
}

/** Payload de cada query y de su reply. */
export interface QueryPayloads {
  "display.list": { request: EmptyPayload; reply: DisplayListPayload };
  "output.getState": { request: EmptyPayload; reply: OutputStateChangedPayload };
  "database.getStatus": { request: EmptyPayload; reply: DatabaseStatusPayload };
}

export type CommandType = keyof CommandPayloads;
export type EventType = keyof EventPayloads;
export type QueryType = keyof QueryPayloads;

interface Envelope<K extends MessageKind, T extends string, P> {
  v: typeof PROTOCOL_VERSION;
  id: string;
  kind: K;
  type: T;
  payload: P;
}

/** Uniones discriminadas por `kind` y `type`. */
export type CommandMessage = { [T in CommandType]: Envelope<"command", T, CommandPayloads[T]> }[CommandType];
export type EventMessage = { [T in EventType]: Envelope<"event", T, EventPayloads[T]> }[EventType];
export type QueryMessage = { [T in QueryType]: Envelope<"query", T, QueryPayloads[T]["request"]> }[QueryType];
export type ReplyMessage = { [T in QueryType]: Envelope<"reply", T, ReplyPayload<QueryPayloads[T]["reply"]>> }[QueryType];
export type IncomingMessage = EventMessage | ReplyMessage;




