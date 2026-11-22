import { create } from "zustand";
import { ipc } from "@/ipc/instance";
import type { DatabaseStatusPayload, DisplayInfo, DisplayListPayload, OutputBackgroundFailedPayload, OutputStateChangedPayload } from "@/ipc/protocol";
import { createRetrier, type RetryOptions } from "@/ipc/retry";
import { isNewer } from "@/ipc/sequence";
import { createTicker } from "@/ipc/ticker";

/** Un comando que el backend no pudo ejecutar (evento `command.failed`). */
export interface CommandFailure {
  commandId: string;
  commandType: string;
  code: string;
  message: string;
}

/** Réplica del estado que manda C#. No contiene estado de negocio propio. */
interface ControlState {
  /**
   * true mientras llega tráfico válido del backend: cualquier mensaje válido lo marca, y vuelve a false si no llega ninguno durante
   * `staleAfterMs` (el heartbeat pide cada pocos segundos algo barato, así que el silencio significa que el backend no responde).
   */
  connected: boolean;
  /** true si en algún momento hubo tráfico válido: distingue «Conectando…» (nunca respondió) de «sin respuesta» (dejó de responder). */
  everConnected: boolean;
  displays: DisplayInfo[];
  displaysSeq: number | null;
  output: OutputStateChangedPayload | null;
  /** Estado de la base de datos (aviso si su inicialización falló). */
  database: DatabaseStatusPayload | null;
  failure: CommandFailure | null;
  /** El fondo actual no se pudo reproducir (la salida cayó al color de reserva). Se limpia al cambiar de archivo de fondo o al cerrarlo el operador. */
  backgroundFailure: OutputBackgroundFailedPayload | null;
}

export const useControlStore = create<ControlState>(() => ({
  connected: false,
  everConnected: false,
  displays: [],
  displaysSeq: null,
  output: null,
  database: null,
  failure: null,
  backgroundFailure: null,
}));

/** Cada cuánto el panel pide algo barato al backend para comprobar que sigue vivo. */
export const HEARTBEAT_INTERVAL_MS = 4000;
/** Sin ningún mensaje válido durante este tiempo el panel deja de considerarse conectado (≈ 3 heartbeats perdidos). */
export const STALE_AFTER_MS = 12000;

export interface HeartbeatOptions {
  intervalMs?: number;
  staleAfterMs?: number;
}

/** Aplica solo si la secuencia es estrictamente mayor que la última (un snapshot viejo o repetido nunca pisa a uno nuevo). */
function applyDisplays(payload: DisplayListPayload): void {
  if (!isNewer(useControlStore.getState().displaysSeq, payload.seq)) return;
  useControlStore.setState({ displays: payload.displays, displaysSeq: payload.seq });
}

function applyOutput(payload: OutputStateChangedPayload): void {
  const state = useControlStore.getState();
  if (!isNewer(state.output?.seq ?? null, payload.seq)) return;
  // Otro fondo (u ninguno) ya no es el que falló: el aviso deja de valer.
  // También deja de valer si se pidió un reintento del mismo medio (sube `retry`): la salida lo vuelve a intentar y, si falla otra vez, manda un aviso nuevo.
  const retried = payload.state.background.retry > (state.output?.state.background.retry ?? 0);
  const stale = state.backgroundFailure !== null && (payload.state.background.fileName !== state.backgroundFailure.fileName || retried);
  useControlStore.setState(stale ? { output: payload, backgroundFailure: null } : { output: payload });
}

function applyDatabase(payload: DatabaseStatusPayload): void {
  if (!isNewer(useControlStore.getState().database?.seq ?? null, payload.seq)) return;
  useControlStore.setState({ database: payload });
}

export function dismissFailure(): void {
  useControlStore.setState({ failure: null });
}

export function dismissBackgroundFailure(): void {
  useControlStore.setState({ backgroundFailure: null });
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
export function connectControlStore(options?: { retry?: RetryOptions; requestTimeoutMs?: number; heartbeat?: HeartbeatOptions }): ControlConnection {
  const intervalMs = options?.heartbeat?.intervalMs ?? HEARTBEAT_INTERVAL_MS;
  const staleAfterMs = options?.heartbeat?.staleAfterMs ?? STALE_AFTER_MS;
  const retrier = createRetrier(() => load(options?.requestTimeoutMs ?? 5000), options?.retry);
  let lastActivity = Date.now();
  let heartbeatInFlight = false;

  const offs = [
    ipc.subscribe("display.changed", applyDisplays),
    ipc.subscribe("output.stateChanged", applyOutput),
    ipc.subscribe("database.statusChanged", applyDatabase),
    ipc.subscribe("output.backgroundFailed", (failure) => {
      useControlStore.setState({ backgroundFailure: failure });
    }),
    ipc.subscribe("command.failed", ({ commandId, commandType, error }) => {
      useControlStore.setState({ failure: { commandId, commandType, code: error.code, message: error.message } });
    }),
    ipc.onActivity(() => {
      lastActivity = Date.now();
      const { connected, everConnected } = useControlStore.getState();
      if (connected) return;
      useControlStore.setState({ connected: true, everConnected: true });
      // Volvió tras un silencio: se pudo perder algún evento mientras tanto, así que se vuelve a pedir el estado completo.
      if (everConnected) retrier.kick();
    }),
  ];

  retrier.start();

  // Heartbeat: sin él, el silencio es normal (el backend solo escribe cuando algo cambia) y `connected` no podría volver a false.
  // La consulta es barata y su respuesta cuenta como tráfico válido. Una sola en vuelo: si el backend no responde no se acumulan.
  // El reloj lo marca un worker (createTicker): así el throttling de páginas ocultas no retrasa el latido, y un hilo principal atascado NO lo envía.
  const stopHeartbeat = createTicker(intervalMs, () => {
    if (useControlStore.getState().connected && Date.now() - lastActivity > staleAfterMs) useControlStore.setState({ connected: false });
    if (heartbeatInFlight) return;
    heartbeatInFlight = true;
    ipc
      .request("database.getStatus", {}, staleAfterMs)
      .then(applyDatabase)
      .catch(() => undefined)   // sin respuesta: lo decide el cronómetro de arriba, no este catch
      .finally(() => {
        heartbeatInFlight = false;
      });
  });

  const connection: ControlConnection = {
    reconnect: () => {
      retrier.kick();
    },
    dispose: () => {
      stopHeartbeat();
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