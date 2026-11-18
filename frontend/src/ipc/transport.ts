export interface IpcTransport {
  send(message: string): void;
  onMessage(callback: (message: string) => void): void;
}

interface PhotinoExternal {
  sendMessage: (message: string) => void;
  receiveMessage: (callback: (message: string) => void) => void;
}

function isPhotinoExternal(value: unknown): value is PhotinoExternal {
  return (
    typeof value === "object" &&
    value !== null &&
    typeof Reflect.get(value, "sendMessage") === "function" &&
    typeof Reflect.get(value, "receiveMessage") === "function"
  );
}

/**
 * Transporte de Photino (window.external.sendMessage / receiveMessage).
 * Fuera de Photino (p. ej. `npm run dev` en un navegador) no envía nada y avisa por consola.
 * Se lee con Reflect.get porque `window.external` figura como obsoleto en las definiciones DOM.
 */
export function createWindowTransport(): IpcTransport {
  const external: unknown = Reflect.get(window, "external");
  if (!isPhotinoExternal(external)) {
    console.warn("[ipc] window.external de Photino no disponible: el cliente IPC está inactivo.");
    return { send: () => undefined, onMessage: () => undefined };
  }
  return {
    send: (message) => {
      external.sendMessage(message);
    },
    onMessage: (callback) => {
      external.receiveMessage(callback);
    },
  };
}
