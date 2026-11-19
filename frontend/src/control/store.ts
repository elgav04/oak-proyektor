import { create } from "zustand";
import { ipc } from "@/ipc/instance";
import type { DatabaseStatusPayload, DisplayInfo, DisplayListPayload, OutputStateChangedPayload } from "@/ipc/protocol";
import { createRetrier, type RetryOptions } from "@/ipc/retry";
import { isNewer } from "@/ipc/sequence";

/** Un comando que el backend no pudo ejecutar (evento `command.failed`). */
export interface CommandFailure {
  commandId: string;
  commandType: string;
  code: string;
  message: string;
}

/** Réplica del estado que manda C#. No contiene estado de negocio propio. */
interface ControlState {
  /** true en cuanto llega CUALQUIER mensaje válido del backend. */
  connected: boolean;
  displays: DisplayInfo[];
  displaysSeq: number | null;
  output: OutputStateChangedPayload | null;
  /** Estado de la base de datos (aviso si su inicialización falló). */
  database: DatabaseStatusPayload | null;
  failure: CommandFailure | null;
}

export const useControlStore = create<ControlState>(() => ({ connected: false, displays: [], displaysSeq: null, output: null, database: null, failure: null }));

/** Aplica solo si la secuencia es estrictamente mayor que la última (un snapshot viejo o repetido nunca pisa a uno nuevo). */
function applyDisplays(payload: DisplayListPayload): void {
  if (!isNewer(useControlStore.getState().displaysSeq, payload.seq)) return;
  useControlStore.setState({ displays: payload.displays, displaysSeq: payload.seq });
}

function applyOutput(payload: OutputStateChangedPayload): void {
  if (!isNewer(useControlStore.getState().output?.seq ?? null, payload.seq)) return;
  useControlStore.setState({ output: payload });
}

function applyDatabase(payload: DatabaseStatusPayload): void {
  if (!isNewer(useControlStore.getState().database?.seq ?? null, payload.seq)) return;
  useControlStore.setState({ database: payload });
}

export function dismissFailure(): void {
  useControlStore.setState({ failure: null });
}

export interface ControlConnection {
  /** «Reconectar»: reintenta ya y reinicia la espera creciente. */
  reconnect(): void;
  dispose(): void;
}

let current: ControlConnection | null = null;

/** Botón «Reconectar» del panel. */
export function reconnectControl(): void {
  current?.reconnect();
}

async function load(requestTimeoutMs: number): Promise<void> {
  const [displays, output, database] = await Promise.all([
    ipc.request("display.list", {}, requestTimeoutMs),
    ipc.request("output.getState", {}, requestTimeoutMs),
    ipc.request("database.getStatus", {}, requestTimeoutMs),
  ]);
  applyDisplays(displays);
  applyOutput(output);
  applyDatabase(database);
}

/**
 * Se suscribe a los eventos y pide el estado inicial. Si el backend no responde, REINTENTA INDEFINIDAMENTE con espera creciente
 * (500 ms… hasta 8 s). Se considera conectado con cualquier mensaje válido, aunque la carga inicial aún no haya terminado.
 */
export function connectControlStore(options?: { retry?: RetryOptions; requestTimeoutMs?: number }): ControlConnection {
  const offs = [
    ipc.subscribe("display.changed", applyDisplays),
    ipc.subscribe("output.stateChanged", applyOutput),
    ipc.subscribe("database.statusChanged", applyDatabase),
    ipc.subscribe("command.failed", ({ commandId, commandType, error }) => {
      useControlStore.setState({ failure: { commandId, commandType, code: error.code, message: error.message } });
    }),
    ipc.onActivity(() => {
      if (!useControlStore.getState().connected) useControlStore.setState({ connected: true });
    }),
  ];

  const retrier = createRetrier(() => load(options?.requestTimeoutMs ?? 5000), options?.retry);
  retrier.start();

  const connection: ControlConnection = {
    reconnect: () => {
      retrier.kick();
    },
    dispose: () => {
      retrier.stop();
      offs.forEach((off) => {
        off();
      });
      if (current === connection) current = null;
    },
  };
  current = connection;
  return connection;
}