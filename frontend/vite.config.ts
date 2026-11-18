import { resolve } from "node:path";
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";

// Dos páginas de entrada: control (index.html) y proyección (output.html).
export default defineConfig({
  plugins: [react(), tailwindcss()],
  // Rutas relativas: Photino sirve los estáticos desde disco.
  base: "./",
  resolve: { alias: { "@": resolve(import.meta.dirname, "src") } },
  build: {
    outDir: "dist",
    emptyOutDir: true,
    rollupOptions: {
      input: {
        index: resolve(import.meta.dirname, "index.html"),
        output: resolve(import.meta.dirname, "output.html"),
      },
    },
  },
});
