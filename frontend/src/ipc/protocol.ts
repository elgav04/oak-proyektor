// Reflejo manual de OakProyektor.Ipc / OakProyektor.Core. Un test de contrato (backend/tests) compara:
// PROTOCOL_VERSION, MESSAGE_CATALOG, MASTER_MODES y los campos de cada interfaz "payload" con los tipos de C#.
// Mantén las interfaces planas (sin objetos anidados en línea) para que ese test pueda leerlas.

export const PROTOCOL_VERSION = 1;

export type MessageKind = "command" | "event" | "query" | "reply";

export const MESSAGE_CATALOG = [
  { type: "output.open", kind: "command" },
  { type: "output.close", kind: "command" },
  { type: "output.setMaster", kind: "command" },
  { type: "display.list", kind: "query" },
  { type: "display.list", kind: "reply" },
  { type: "output.getState", kind: "query" },
  { type: "output.getState", kind: "reply" },
  { type: "display.changed", kind: "event" },
  { type: "output.stateChanged", kind: "event" },
] as const;

export const MASTER_MODES = ["live", "clearText", "black", "logo", "freeze"] as const;
export type MasterMode = (typeof MASTER_MODES)[number];

export type EmptyPayload = Record<string, never>;

export interface DisplayInfo {
  index: number;
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
  displayIndex: number | null;
}

export interface OutputSetMasterPayload {
  master: MasterMode;
}

export interface DisplayListPayload {
  displays: DisplayInfo[];
}

export interface OutputWindowStatus {
  isOpen: boolean;
  displayIndex: number | null;
  isWindowed: boolean;
}

export interface OutputStateChangedPayload {
  state: OutputState;
  window: OutputWindowStatus;
}

/** Payload de cada comando (UI → C#). */
export interface CommandPayloads {
  "output.open": OutputOpenPayload;
  "output.close": EmptyPayload;
  "output.setMaster": OutputSetMasterPayload;
}

/** Payload de cada evento (C# → UI). */
export interface EventPayloads {
  "display.changed": DisplayListPayload;
  "output.stateChanged": OutputStateChangedPayload;
}

/** Payload de cada query y de su reply. */
export interface QueryPayloads {
  "display.list": { request: EmptyPayload; reply: DisplayListPayload };
  "output.getState": { request: EmptyPayload; reply: OutputStateChangedPayload };
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
export type ReplyMessage = { [T in QueryType]: Envelope<"reply", T, QueryPayloads[T]["reply"]> }[QueryType];
export type IncomingMessage = EventMessage | ReplyMessage;
