import {
  PROTOCOL_VERSION,
  type CommandPayloads,
  type CommandType,
  type EventPayloads,
  type EventType,
  type IncomingMessage,
  type QueryPayloads,
  type QueryType,
} from "./protocol";
import type { IpcTransport } from "./transport";

type Handler<T> = (payload: T) => void;
interface Pending {
  type: QueryType;
  resolve: (payload: unknown) => void;
  reject: (error: Error) => void;
  timer: ReturnType<typeof setTimeout>;
}

const DEFAULT_TIMEOUT_MS = 5000;

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null;
}

/** Valida la forma del sobre. Devuelve null (y nunca lanza) si el mensaje no es utilizable. */
function parseIncoming(raw: string): IncomingMessage | null {
  let data: unknown;
  try {
    data = JSON.parse(raw);
  } catch {
    console.warn("[ipc] mensaje no es JSON; ignorado");
    return null;
  }
  if (!isRecord(data) || data.v !== PROTOCOL_VERSION) {
    console.warn("[ipc] sobre inválido o versión no soportada; ignorado");
    return null;
  }
  const { id, kind, type, payload } = data;
  if (typeof id !== "string" || typeof type !== "string" || (kind !== "event" && kind !== "reply") || !isRecord(payload)) {
    console.warn("[ipc] sobre incompleto; ignorado");
    return null;
  }
  // El payload se confía por tipo de mensaje (lo produce nuestro backend); la forma del sobre sí se valida.
  return { v: PROTOCOL_VERSION, id, kind, type, payload } as unknown as IncomingMessage;
}

export class IpcClient {
  private readonly pending = new Map<string, Pending>();
  private readonly subscribers = new Map<EventType, Set<Handler<never>>>();

  constructor(private readonly transport: IpcTransport) {
    transport.onMessage((raw) => {
      this.handle(raw);
    });
  }

  /** Envía un comando (sin respuesta). */
  send<T extends CommandType>(type: T, payload: CommandPayloads[T]): void {
    this.post({ v: PROTOCOL_VERSION, id: crypto.randomUUID(), kind: "command", type, payload });
  }

  /** Envía una query y espera su reply correlacionado por id. Rechaza si vence el tiempo. */
  request<T extends QueryType>(type: T, payload: QueryPayloads[T]["request"], timeoutMs = DEFAULT_TIMEOUT_MS): Promise<QueryPayloads[T]["reply"]> {
    const id = crypto.randomUUID();
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error(`Tiempo agotado esperando la respuesta de ${type}`));
      }, timeoutMs);
      this.pending.set(id, { type, resolve: resolve as (payload: unknown) => void, reject, timer });
      try {
        this.post({ v: PROTOCOL_VERSION, id, kind: "query", type, payload });
      } catch (error) {
        clearTimeout(timer);
        this.pending.delete(id);
        reject(error instanceof Error ? error : new Error(String(error)));
      }
    });
  }

  /** Suscribe a un evento. Devuelve la función para cancelar. */
  subscribe<T extends EventType>(type: T, handler: Handler<EventPayloads[T]>): () => void {
    let set = this.subscribers.get(type);
    if (!set) {
      set = new Set();
      this.subscribers.set(type, set);
    }
    set.add(handler);
    return () => {
      set.delete(handler);
    };
  }

  private post(message: { v: number; id: string; kind: string; type: string; payload: unknown }): void {
    this.transport.send(JSON.stringify(message));
  }

  private handle(raw: string): void {
    const message = parseIncoming(raw);
    if (!message) return;

    if (message.kind === "reply") {
      const entry = this.pending.get(message.id);
      if (!entry || entry.type !== message.type) {
        console.warn(`[ipc] reply sin query pendiente (${message.type}); ignorado`);
        return;
      }
      clearTimeout(entry.timer);
      this.pending.delete(message.id);
      entry.resolve(message.payload);
      return;
    }

    const handlers = this.subscribers.get(message.type);
    if (!handlers) return;   // evento sin suscriptores o desconocido: se ignora
    for (const handler of handlers) {
      try {
        (handler as Handler<unknown>)(message.payload);
      } catch (error) {
        console.error(`[ipc] un suscriptor de ${message.type} falló`, error);   // un suscriptor roto no afecta a los demás
      }
    }
  }
}


