import {
  PROTOCOL_VERSION,
  type CommandPayloads,
  type CommandType,
  type EventPayloads,
  type EventType,
  type IncomingMessage,
  type IpcErrorPayload,
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

/** Error de una query: el del servidor (`code` y `message` reales) o `timeout` / `transport` si nunca hubo respuesta. */
export class IpcRequestError extends Error {
  constructor(
    readonly code: string,
    message: string,
  ) {
    super(message);
    this.name = "IpcRequestError";
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null;
}

function isErrorPayload(value: unknown): value is IpcErrorPayload {
  return isRecord(value) && typeof value.code === "string" && typeof value.message === "string";
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
  if (kind === "reply" && typeof payload.ok !== "boolean") {
    console.warn("[ipc] reply sin `ok`; ignorado");
    return null;
  }
  // El payload se confía por tipo de mensaje (lo produce nuestro backend); la forma del sobre sí se valida.
  return { v: PROTOCOL_VERSION, id, kind, type, payload } as unknown as IncomingMessage;
}

export class IpcClient {
  private readonly pending = new Map<string, Pending>();
  private readonly subscribers = new Map<EventType, Set<Handler<never>>>();
  private readonly activity = new Set<() => void>();

  constructor(private readonly transport: IpcTransport) {
    transport.onMessage((raw) => {
      this.handle(raw);
    });
  }

  /**
   * Envía un comando (sin respuesta). Si falla en el servidor llega el evento `command.failed`. Devuelve el id del mensaje: los comandos cuyo RESULTADO llega como
   * un evento correlacionado (`song.save` → `song.changed.commandId`) lo usan para saber cuál fue la respuesta a cuál.
   */
  send<T extends CommandType>(type: T, payload: CommandPayloads[T]): string {
    const id = crypto.randomUUID();
    this.post({ v: PROTOCOL_VERSION, id, kind: "command", type, payload });
    return id;
  }

  /**
   * Envía una query y espera su reply correlacionado por id. Si el servidor responde con error, rechaza YA con ese error
   * (`IpcRequestError` con su `code` y `message` reales); solo si no hay respuesta rechaza por tiempo (`code: "timeout"`).
   */
  request<T extends QueryType>(type: T, payload: QueryPayloads[T]["request"], timeoutMs = DEFAULT_TIMEOUT_MS): Promise<QueryPayloads[T]["reply"]> {
    const id = crypto.randomUUID();
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new IpcRequestError("timeout", `Tiempo agotado esperando la respuesta de ${type}`));
      }, timeoutMs);
      this.pending.set(id, { type, resolve: resolve as (payload: unknown) => void, reject, timer });
      try {
        this.post({ v: PROTOCOL_VERSION, id, kind: "query", type, payload });
      } catch (error) {
        clearTimeout(timer);
        this.pending.delete(id);
        reject(new IpcRequestError("transport", error instanceof Error ? error.message : String(error)));
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

  /** Avisa cada vez que llega CUALQUIER mensaje válido (evento o reply): prueba de que el backend responde. */
  onActivity(handler: () => void): () => void {
    this.activity.add(handler);
    return () => {
      this.activity.delete(handler);
    };
  }

  private post(message: { v: number; id: string; kind: string; type: string; payload: unknown }): void {
    this.transport.send(JSON.stringify(message));
  }

  private handle(raw: string): void {
    const message = parseIncoming(raw);
    if (!message) return;

    for (const handler of this.activity) {
      try {
        handler();
      } catch (error) {
        console.error("[ipc] un observador de actividad falló", error);
      }
    }

    if (message.kind === "reply") {
      const entry = this.pending.get(message.id);
      if (!entry || entry.type !== message.type) {
        console.warn(`[ipc] reply sin query pendiente (${message.type}); ignorado`);
        return;
      }
      clearTimeout(entry.timer);
      this.pending.delete(message.id);
      const reply = message.payload as { ok: boolean; result: unknown; error: unknown };
      if (reply.ok) entry.resolve(reply.result);
      else if (isErrorPayload(reply.error)) entry.reject(new IpcRequestError(reply.error.code, reply.error.message));
      else entry.reject(new IpcRequestError("unknown", "El servidor respondió con un error sin detalle"));
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