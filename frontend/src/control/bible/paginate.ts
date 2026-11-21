import { isSuperscriptNumber } from "./superscript";

/**
 * Paginación de texto por MEDICIÓN: función pura. No sabe de DOM ni de fuentes: recibe `fits(texto, esUltima)` («¿cabe este párrafo en una página?»;
 * `esUltima` = la página lleva además el aviso de derechos). La medición real la pone `measure.ts` (layout del navegador con la fuente y el tema reales);
 * los tests ponen una medición simulada.
 *
 * Garantías (las comprueban los tests):
 *  - NUNCA se pierde ni se recorta texto: al unir las páginas se recupera el original (salvo espacios en los cortes).
 *  - Cada página cabe según `fits` (salvo que ni un solo carácter quepa: entonces se avanza de uno en uno en vez de colgarse).
 *  - Los versículos enteros se agrupan en una página si caben; un versículo que no cabe se divide en el límite más natural:
 *    final de frase (. ? ! …) > cláusula (, ; : —) > palabra > (palabra gigante) carácter.
 *  - Coste acotado: búsqueda binaria por página (O(páginas · log palabras) mediciones), sin bucles largos en el hilo principal.
 */
export type Fits = (text: string, isLast: boolean) => boolean;

interface Cut {
  /** Posición en el texto donde empieza el trozo siguiente (tras los espacios). */
  index: number;
  /** 3 = final de frase, 2 = cláusula, 1 = solo palabra. */
  level: 1 | 2 | 3;
}

const SENTENCE_END = /[.!?…]["'”’»)\]]*$/;
const CLAUSE_END = /[,;:—–]["'”’»)\]]*$/;

/** Puntos de corte candidatos (después de cada espacio). No se corta justo tras el número de versículo en superíndice ni deja el corte sin texto. */
function cutPoints(text: string): Cut[] {
  const cuts: Cut[] = [];
  const spaces = /\s+/g;
  for (let m = spaces.exec(text); m !== null; m = spaces.exec(text)) {
    const before = text.slice(0, m.index);
    const end = m.index + m[0].length;
    if (before.trim().length === 0 || end >= text.length) continue;
    if (isSuperscriptNumber(before.trim())) continue;
    cuts.push({ index: end, level: SENTENCE_END.test(before) ? 3 : CLAUSE_END.test(before) ? 2 : 1 });
  }
  return cuts;
}

/** Mayor `i` en [lo, hi] con `ok(i)` verdadero (se asume monótono: si cabe a N, cabe a menos). -1 si ni `lo` cumple. */
function lastTrue(lo: number, hi: number, ok: (i: number) => boolean): number {
  if (hi < lo || !ok(lo)) return -1;
  let a = lo;
  let b = hi;
  while (a < b) {
    const mid = Math.ceil((a + b) / 2);
    if (ok(mid)) a = mid;
    else b = mid - 1;
  }
  return a;
}

/** Evita cortar un par sustituto (emoji, etc.) por la mitad. */
function safeIndex(text: string, k: number): number {
  const code = text.charCodeAt(k - 1);
  return k > 1 && code >= 0xd800 && code <= 0xdbff ? k - 1 : k;
}

/** Primer trozo de `text` que cabe y dónde empieza el resto. */
function firstChunk(text: string, fits: (t: string) => boolean): { head: string; rest: string } {
  const cuts = cutPoints(text);
  const j = lastTrue(0, cuts.length - 1, (i) => fits(text.slice(0, cuts[i]?.index ?? 0).trimEnd()));
  if (j >= 0) {
    const limit = cuts[j]?.index ?? 0;
    // Retrocede a un límite mejor (frase o cláusula) si está en la parte final del trozo; si no, corta por palabra.
    let best = j;
    for (let i = j; i >= 0; i--) {
      const c = cuts[i];
      if (!c || c.index < limit * 0.55) break;
      const current = cuts[best];
      if (current && (c.level > current.level)) best = i;
    }
    const at = cuts[best]?.index ?? limit;
    return { head: text.slice(0, at).trimEnd(), rest: text.slice(at).trimStart() };
  }
  // Ni la primera palabra cabe (palabra enorme o área minúscula): se corta por caracteres, y siempre se avanza al menos uno.
  const k = lastTrue(1, text.length, (n) => fits(text.slice(0, safeIndex(text, n))));
  const at = safeIndex(text, Math.max(1, k));
  return { head: text.slice(0, at), rest: text.slice(at) };
}

/** Si la última página queda casi vacía, reparte las dos últimas por el mejor límite cercano a la mitad (si ambas mitades caben). */
function rebalanceTail(parts: string[], fits: (t: string) => boolean): void {
  if (parts.length < 2) return;
  const last = parts[parts.length - 1] ?? "";
  const prev = parts[parts.length - 2] ?? "";
  if (last.length >= prev.length * 0.3) return;
  const joined = `${prev} ${last}`;
  const cuts = cutPoints(joined);
  const middle = joined.length / 2;
  const near = cuts.filter((c) => c.index >= joined.length * 0.3 && c.index <= joined.length * 0.7);
  // Pocos candidatos: los de frase/cláusula y el límite de palabra más cercano a la mitad.
  const wordNearest = [...near].sort((a, b) => Math.abs(a.index - middle) - Math.abs(b.index - middle))[0];
  const candidates = near.filter((c) => c.level > 1).slice(0, 12);
  if (wordNearest && !candidates.includes(wordNearest)) candidates.push(wordNearest);
  let best: { head: string; rest: string; score: number } | null = null;
  for (const c of candidates) {
    const head = joined.slice(0, c.index).trimEnd();
    const rest = joined.slice(c.index).trimStart();
    const score = c.level * 40 - (Math.abs(c.index - middle) / joined.length) * 100;
    if ((best === null || score > best.score) && fits(head) && fits(rest)) best = { head, rest, score };
  }
  if (best) parts.splice(parts.length - 2, 2, best.head, best.rest);
}

/** Divide UN texto en trozos que caben cada uno (cada trozo se mide con `fits`). */
export function splitToFit(text: string, fits: (t: string) => boolean): string[] {
  const parts: string[] = [];
  let rest = text.trim();
  while (rest.length > 0) {
    if (fits(rest)) {
      parts.push(rest);
      break;
    }
    const { head, rest: next } = firstChunk(rest, fits);
    parts.push(head);
    rest = next.trimStart();
  }
  rebalanceTail(parts, fits);
  return parts;
}

/**
 * Reparte `paragraphs` (un versículo por elemento, ya con su número) en páginas. Cada página es un párrafo continuo.
 * El aviso de derechos solo va en la ÚLTIMA página: si esta no cabe con él, se vuelve a dividir.
 */
export function paginate(paragraphs: readonly string[], fits: Fits): string[] {
  const cache = new Map<string, boolean>();
  const measure = (text: string, last: boolean): boolean => {
    const key = `${last ? "1" : "0"}|${text}`;
    let value = cache.get(key);
    if (value === undefined) {
      value = fits(text, last);
      cache.set(key, value);
    }
    return value;
  };
  const normal = (t: string): boolean => measure(t, false);

  const pages: string[] = [];
  let current = "";
  for (const raw of paragraphs) {
    const verse = raw.trim();
    if (verse.length === 0) continue;
    const joined = current ? `${current} ${verse}` : verse;
    if (normal(joined)) {
      current = joined;
      continue;
    }
    if (current) {
      pages.push(current);
      current = "";
    }
    if (normal(verse)) {
      current = verse;
      continue;
    }
    const parts = splitToFit(verse, normal);
    current = parts.pop() ?? "";
    pages.push(...parts);
  }
  if (current) pages.push(current);

  const last = pages[pages.length - 1];
  if (last !== undefined && !measure(last, true)) {
    pages.pop();
    pages.push(...splitToFit(last, (t) => measure(t, true)));
  }
  return pages;
}
