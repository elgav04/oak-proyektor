// ÚNICA fuente de la versión del protocolo para los fixtures y tests de JS: sale de `frontend/src/ipc/protocol.ts` (PROTOCOL_VERSION). Una subida futura del protocolo no deja
// ningún `v: NN` suelto en los tests: un número escrito a mano hizo que el cliente rechazara todos los mensajes y los controladores reintentaran sin parar (RAM por las nubes).
import { loadTs } from "./helper.mjs";

const { PROTOCOL_VERSION } = await loadTs("/src/ipc/protocol.ts");

/** La versión vigente del protocolo. */
export const V = PROTOCOL_VERSION;
