import type { BibleVerseDto, TextTheme } from "@/ipc/protocol";
import { paginate } from "./paginate";
import { toSuperscript } from "./superscript";

export interface PassageOptions {
  /** Nombre corto de la Biblia (el que el operador configuró; si no, la abreviatura). */
  bibleName: string;
  /** Aviso de derechos de la Biblia; vacío = no hay nada que mostrar. */
  attribution: string;
  showAttribution: boolean;
  superscript: boolean;
}

export interface BuiltPassage {
  /** «Juan 3:16-18». */
  reference: string;
  /** Una página = los bloques que se mandan a la salida (párrafo del texto, referencia y, en la última, el aviso de derechos). */
  pages: string[][];
}

/**
 * Límites del backend por bloque y por slide (`ContentValidator.MaxBlockChars` / `MaxTotalChars`; un test de C# comprueba que coinciden). El backend RECHAZA un slide
 * que los supera (`output.invalidContent`), así que la paginación garantiza páginas por debajo: con letra muy pequeña (8 px caben decenas de miles de caracteres en
 * un lienzo de 1920×1080) una página que «cabe» podría tener más de 4000 caracteres y no se proyectaría. Se divide por longitud igual que por altura (H14).
 */
export const MAX_BLOCK_CHARS = 4000;
export const MAX_TOTAL_CHARS = 20000;

const collapse = (text: string): string => text.replace(/\s+/g, " ").trim();

/** «Juan 3:16», «Juan 3:16-18», «Génesis 1:31-2:3», «Juan 3:16, 18» (versículos no contiguos). Usa solo ASCII para que el parser de C# lo lea tal cual. */
export function formatReference(verses: readonly BibleVerseDto[]): string {
  const first = verses[0];
  const last = verses[verses.length - 1];
  if (!first || !last) return "";
  const book = first.bookName;
  if (first.bookNumber !== last.bookNumber) return verses.map((v) => `${v.bookName} ${String(v.chapter)}:${String(v.verseNumber)}`).join("; ");
  if (first.chapter === last.chapter) {
    const numbers = verses.map((v) => v.verseNumber);
    const contiguous = numbers.every((n, i) => i === 0 || n === (numbers[i - 1] ?? 0) + 1);
    if (verses.length === 1) return `${book} ${String(first.chapter)}:${String(first.verseNumber)}`;
    return contiguous
      ? `${book} ${String(first.chapter)}:${String(first.verseNumber)}-${String(last.verseNumber)}`
      : `${book} ${String(first.chapter)}:${numbers.join(", ")}`;
  }
  return `${book} ${String(first.chapter)}:${String(first.verseNumber)}-${String(last.chapter)}:${String(last.verseNumber)}`;
}

/**
 * Páginas de un pasaje a partir de la medición real (`fitsBlocks`). Los versículos se unen en un solo párrafo (como en una Biblia impresa) y, si no caben,
 * se reparten en páginas (los largos por límite de frase o cláusula). El número de versículo (opcional) va en superíndice solo al principio de cada versículo.
 */
export function buildPassage(verses: readonly BibleVerseDto[], options: PassageOptions, fitsBlocks: (blocks: readonly string[]) => boolean): BuiltPassage {
  const reference = formatReference(verses);
  const footer = `${reference} · ${options.bibleName}`;
  const attribution = options.showAttribution ? collapse(options.attribution) : "";
  const paragraphs = verses.map((v) => {
    const text = collapse(v.text);
    return options.superscript ? `${toSuperscript(v.verseNumber)} ${text}` : text;
  });
  const closing = (last: boolean): string[] => (last && attribution ? [footer, attribution] : [footer]);
  // La longitud del bloque principal (y la del slide entero) también limita la página: se comprueba ANTES de medir (es barato y evita mediciones inútiles).
  const bodyLimit = (last: boolean): number => Math.min(MAX_BLOCK_CHARS, MAX_TOTAL_CHARS - closing(last).reduce((sum, b) => sum + b.length, 0));
  const bodies = paginate(paragraphs, (text, last) => text.length <= bodyLimit(last) && fitsBlocks([text, ...closing(last)]));
  const pages = bodies.map((body, index) => [body, ...closing(index === bodies.length - 1)]);
  return { reference, pages };
}

/** Tamaño mínimo de la letra como fracción del pedido: la red de seguridad puede reducir hasta ahí (H3). */
export const MIN_FONT_RATIO = 0.8;
const ABSOLUTE_MIN_FONT = 8;

/**
 * Mínimo del ajuste automático para un tamaño pedido: SIEMPRE por debajo del tamaño (`floor(tamaño × 0,8)`), para que la red de seguridad tenga margen de
 * reducción; si el operador pidió un mínimo menor, se respeta. Solo con tamaño 8 (el mínimo permitido) no hay margen. Nunca supera el tamaño ni baja de 8.
 */
export function safeMinFontSize(fontSize: number, wantedMin: number): number {
  const ceiling = Math.max(ABSOLUTE_MIN_FONT, Math.floor(fontSize * MIN_FONT_RATIO));
  return Math.min(fontSize, Math.max(ABSOLUTE_MIN_FONT, Math.min(wantedMin, ceiling)));
}

/**
 * Tema con que se proyecta: el del operador con el ajuste automático ACTIVADO como red de seguridad (la paginación hace que el texto quepa al tamaño pedido;
 * si la medición difiriera en algo —otro escalado de Windows en el proyector, otro salto de línea—, la salida reduce la letra en vez de recortar).
 * `minFontSize` es SIEMPRE menor que el tamaño (`safeMinFontSize`), también con ajustes antiguos donde el mínimo había quedado igual al tamaño.
 */
export function projectionTheme(theme: TextTheme): TextTheme {
  return { ...theme, autoFit: true, minFontSize: safeMinFontSize(theme.fontSize, theme.minFontSize) };
}

/**
 * Cambia el tamaño de la letra del tema de la Biblia SIN arrastrar el mínimo: el mínimo se recalcula desde el valor por defecto con el nuevo tamaño (antes se
 * bajaba a `min(mínimo, tamaño)`, y al reducir el tamaño el mínimo acababa igual al tamaño y anulaba el ajuste automático).
 */
export function themeWithFontSize(theme: TextTheme, fontSize: number, defaultMin: number): TextTheme {
  return { ...theme, fontSize, minFontSize: safeMinFontSize(fontSize, defaultMin) };
}
