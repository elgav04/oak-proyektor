// Carga módulos TypeScript del frontend con la API de Vite (ya instalado): sin dependencias de test nuevas.
import { fileURLToPath } from "node:url";
import { createServer } from "vite";

const root = fileURLToPath(new URL("..", import.meta.url));
let server = null;

// Los módulos avisan por consola de mensajes ignorados a propósito; en los tests ese ruido no aporta.
console.warn = () => undefined;
console.error = () => undefined;

export async function loadTs(path) {
  server ??= await createServer({ root, server: { middlewareMode: true, hmr: false }, appType: "custom", logLevel: "error" });
  return server.ssrLoadModule(path);
}

export async function closeServer() {
  if (server) {
    await server.close();
    server = null;
  }
}