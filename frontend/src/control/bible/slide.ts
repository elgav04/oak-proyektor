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
  const bodies = paginate(paragraphs, (text, last) => fitsBlocks([text, ...closing(last)]));
  const pages = bodies.map((body, index) => [body, ...closing(index === bodies.length - 1)]);
  return { reference, pages };
}

/**
 * Tema con que se proyecta: el del operador con el ajuste automático ACTIVADO como red de seguridad (la paginación hace que el texto quepa al tamaño pedido;
 * si la medición difiriera en algo, la salida reduce la letra en vez de recortar). `minFontSize` nunca supera el tamaño.
 */
export function projectionTheme(theme: TextTheme): TextTheme {
  return { ...theme, autoFit: true, minFontSize: Math.max(8, Math.min(theme.minFontSize, theme.fontSize)) };
}
