import { IpcClient } from "./client";
import { createWindowTransport } from "./transport";

/** Cliente único de la página. */
export const ipc = new IpcClient(createWindowTransport());
