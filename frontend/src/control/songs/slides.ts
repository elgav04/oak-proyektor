import { splitToFit } from "../bible/paginate";
import { MAX_BLOCK_CHARS } from "../bible/slide";

/**
 * Diapositivas DERIVADAS de una canción (no se guardan: dependen del tema y del tamaño de letra). Funciones puras: la medición real (¿cabe este texto?) se inyecta
 * (`fits`), igual que en la paginación de la Biblia (`measure.ts` en la app; una medición simulada en los tests).
 *
 * Reglas:
 *  - El texto de una sección se corta en TROZOS por cada línea en blanco (corte MANUAL de diapositiva): nunca se une un trozo con el siguiente.
 *  - Un trozo que cabe es una diapositiva. Si no cabe, se corta por LÍNEAS (se llena cada diapositiva con las líneas que quepan y, si se puede con el mismo número de
 *    diapositivas, se reparten por igual: 3+2 en vez de 4+1: corte AUTOMÁTICO). Una línea que sola no cabe se divide por palabra (y, si hace falta, por carácter).
 *  - Nunca se pierde ni se recorta texto: al unir las diapositivas de un trozo se recuperan sus líneas, en orden.
 *  - El orden de reproducción puede repetir secciones: sus diapositivas se repiten sin volver a medir (la medición se memoriza por trozo).
 */

/** Cómo empieza una diapositiva respecto de la anterior. */
export type Boundary = "section" | "manual" | "auto";

export interface SongSlide {
  /** Estable mientras no cambie el orden ni el texto: `posición en el orden:trozo:página`. */
  key: string;
  /** Posición en el orden de reproducción. */
  orderIndex: number;
  sectionUid: string;
  sectionLabel: string;
  chunk: number;
  page: number;
  text: string;
  /** `section` = primera de una sección; `manual` = primera de un trozo tras una línea en blanco; `auto` = continuación por falta de espacio. */
  boundary: Boundary;
}

export interface SlideSource {
  uid: string;
  label: string;
  text: string;
}

/** ¿Cabe este texto en el área del tema? */
export type FitsText = (text: string) => boolean;

/** Trozos de una sección: separados por línea(s) en blanco (el backend ya reduce cada grupo a UNA línea en blanco; aquí se tolera cualquier cantidad). */
export function splitChunks(sectionText: string): string[] {
  return sectionText
    .replace(/\r\n?/g, "\n")
    .split(/\n[ \t]*\n/)
    .map((c) => c.replace(/^\n+|\n+$/g, ""))
    .filter((c) => c.trim().length > 0);
}

/** El límite del backend por bloque se aplica ANTES de medir: una página que lo supera no se proyectaría (`output.invalidContent`). */
function limited(fits: FitsText): FitsText {
  return (text) => text.length <= MAX_BLOCK_CHARS && fits(text);
}

/** Reparto por líneas de `lines` en `pageCount` diapositivas lo más iguales posible, o null si alguna no cabe. */
function evenSplit(lines: readonly string[], pageCount: number, fits: FitsText): string[] | null {
  const base = Math.floor(lines.length / pageCount);
  const extra = lines.length % pageCount;
  const pages: string[] = [];
  let at = 0;
  for (let p = 0; p < pageCount; p++) {
    const size = base + (p < extra ? 1 : 0);
    if (size === 0) return null;
    const page = lines.slice(at, at + size).join("\n");
    if (!fits(page)) return null;
    pages.push(page);
    at += size;
  }
  return pages;
}

/** Diapositivas de UN trozo (sin líneas en blanco). Siempre devuelve al menos una. */
export function paginateChunk(chunk: string, fitsText: FitsText): string[] {
  const fits = limited(fitsText);
  const text = chunk.trim();
  if (text.length === 0) return [];
  if (fits(text)) return [text];

  const lines = text.split("\n").map((l) => l.trimEnd());
  const pages: string[] = [];
  let current: string[] = [];
  let splitInsideLine = false;
  for (const line of lines) {
    const candidate = [...current, line].join("\n");
    if (fits(candidate)) {
      current.push(line);
      continue;
    }
    if (current.length > 0) {
      pages.push(current.join("\n"));
      current = [];
    }
    if (fits(line)) {
      current = [line];
      continue;
    }
    // Una sola línea que no cabe: se parte por palabra (o carácter).
    splitInsideLine = true;
    const parts = splitToFit(line, fits);
    const last = parts.pop();
    pages.push(...parts);
    current = last === undefined ? [] : [last];
  }
  if (current.length > 0) pages.push(current.join("\n"));

  if (!splitInsideLine && pages.length >= 2) {
    const balanced = evenSplit(lines, pages.length, fits);
    if (balanced) return balanced;
  }
  return pages;
}

/** Los trozos distintos que habrá que medir para estas secciones (sin repetir): lo que se calienta en la memoria antes de derivar. */
export function uniqueChunks(sources: readonly SlideSource[], order: readonly number[]): string[] {
  const seen = new Set<string>();
  const out: string[] = [];
  for (const index of order) {
    const section = sources[index];
    if (!section) continue;
    for (const chunk of splitChunks(section.text)) {
      if (!seen.has(chunk)) {
        seen.add(chunk);
        out.push(chunk);
      }
    }
  }
  return out;
}

/** Derivar las diapositivas de toda la canción en el orden de reproducción. `pagesOf` es (normalmente) la versión memorizada de `paginateChunk`. */
export function deriveSlides(sources: readonly SlideSource[], order: readonly number[], pagesOf: (chunk: string) => readonly string[]): SongSlide[] {
  const slides: SongSlide[] = [];
  order.forEach((sourceIndex, orderIndex) => {
    const section = sources[sourceIndex];
    if (!section) return;
    splitChunks(section.text).forEach((chunk, chunkIndex) => {
      pagesOf(chunk).forEach((text, page) => {
        slides.push({
          key: `${String(orderIndex)}:${String(chunkIndex)}:${String(page)}`,
          orderIndex,
          sectionUid: section.uid,
          sectionLabel: section.label,
          chunk: chunkIndex,
          page,
          text,
          boundary: page > 0 ? "auto" : chunkIndex > 0 ? "manual" : "section",
        });
      });
    });
  });
  return slides;
}

export interface WarmOptions {
  /** Milisegundos que puede trabajar seguidos antes de ceder el hilo (presupuesto de una tarea del panel: ~50 ms; se usa una fracción). */
  budgetMs: number;
  now: () => number;
  /** Cede el hilo (por ejemplo `setTimeout 0`). */
  yieldNow: () => Promise<void>;
  cancelled: () => boolean;
}

/**
 * Mide los trozos en TANDAS: tras `budgetMs` de trabajo cede el hilo, para que el panel nunca se quede atascado con una canción larga. Devuelve false si se canceló
 * (porque llegó una edición nueva) antes de terminar.
 */
export async function warmChunks(chunks: readonly string[], measure: (chunk: string) => void, options: WarmOptions): Promise<boolean> {
  let sliceStart = options.now();
  for (const chunk of chunks) {
    if (options.cancelled()) return false;
    measure(chunk);
    if (options.now() - sliceStart >= options.budgetMs) {
      await options.yieldNow();
      sliceStart = options.now();
    }
  }
  return !options.cancelled();
}
