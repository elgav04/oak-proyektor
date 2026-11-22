import type { BibleVerseDto, TextTheme } from "@/ipc/protocol";
import { BIBLE_DEFAULT_THEME } from "./defaults";
import { availableHeight, createBlocksMeasurer, inkBleed, safetyMargin } from "./measure";
import { buildPassage, projectionTheme } from "./slide";

/**
 * PRUEBA DE LAYOUT REAL (diagnóstico). Mide con el motor de layout del navegador (WebView2 en la app real; happy-dom NO mide) textos de longitud conocida,
 * con la fuente y el tema reales sobre el lienzo de 1920×1080, y comprueba que la paginación nunca recorta ni pierde texto. Todo el texto es SINTÉTICO
 * (palabras inventadas): ningún texto bíblico. `tools/bible-layout-test.ps1` la ejecuta dentro de la app real y además proyecta cada página en la
 * ventana de salida para comparar con lo que la salida realmente pinta.
 */

export interface LayoutCase {
  name: string;
  verses: BibleVerseDto[];
  theme: TextTheme;
  superscript: boolean;
  showAttribution: boolean;
  attribution: string;
}

export interface PageReport {
  blocks: string[];
  /** Alto (px del lienzo) que ocupa el texto de la página según la medición del panel. */
  measuredHeight: number;
  fits: boolean;
}

export interface CaseReport {
  name: string;
  fontFamily: string;
  fontSize: number;
  limit: number;
  inkBleed: number;
  /** Holgura de seguridad (≈ 0,25 renglones) reservada al paginar, además del sangrado de la tinta. */
  safetyMargin: number;
  pages: PageReport[];
  /** Al unir las páginas se recupera el texto original (sin perder ni recortar nada). */
  lossless: boolean;
  /** Ninguna página (salvo las de un solo carácter forzado) supera el alto disponible. */
  allFit: boolean;
  ms: number;
  /** Tema que se manda a la salida (con el ajuste automático como red de seguridad). */
  projectionTheme: TextTheme;
}

export interface LayoutReport {
  userAgent: string;
  cases: CaseReport[];
  /** Fuentes de las que se pidió el superíndice (para informar de la fuente real que los pinta). */
  ok: boolean;
  totalMs: number;
}

// ───────────── texto sintético determinista ─────────────

function rng(seed: number): () => number {
  let s = seed >>> 0;
  return () => {
    s = (Math.imul(s, 1664525) + 1013904223) >>> 0;
    return s / 4294967296;
  };
}

const SYLLABLES = ["ba", "lu", "mo", "ren", "ti", "sa", "ko", "vir", "an", "del", "pu", "me", "ra", "ni", "tol", "esa", "u", "gro", "fi", "zan", "ce", "dor"];

function word(rand: () => number): string {
  const n = 1 + Math.floor(rand() * 3);
  let w = "";
  for (let i = 0; i < n; i++) w += SYLLABLES[Math.floor(rand() * SYLLABLES.length)] ?? "ba";
  return w;
}

/** Texto inventado de ≈ `chars` caracteres, con comas y puntos, terminado en punto. */
export function syntheticText(chars: number, seed: number): string {
  const rand = rng(seed);
  let out = "";
  let sentenceWords = 0;
  while (out.length < chars) {
    let w = word(rand);
    sentenceWords++;
    if (sentenceWords === 1) w = w.charAt(0).toUpperCase() + w.slice(1);
    const r = rand();
    if (sentenceWords > 5 && r < 0.18) {
      w += ".";
      sentenceWords = 0;
    } else if (r > 0.82) w += ",";
    out += (out ? " " : "") + w;
  }
  return out.replace(/[,\s]+$/, "") + ".";
}

function verse(chapter: number, number: number, text: string): BibleVerseDto {
  return { bookNumber: 43, bookName: "Libro de prueba", chapter, verseNumber: number, text };
}

const BASE: TextTheme = { ...BIBLE_DEFAULT_THEME };
const SHADOW_HEAVY: TextTheme = { ...BASE, outlineWidth: 8, outlineColor: "#000000", shadowBlur: 24, shadowOffset: 8, shadowColor: "#000000CC", lineHeight: 1.15 };

export function layoutCases(): LayoutCase[] {
  const plain = (name: string, verses: BibleVerseDto[], theme: TextTheme = BASE, extra: Partial<LayoutCase> = {}): LayoutCase => ({
    name, verses, theme, superscript: false, showAttribution: false, attribution: "", ...extra,
  });
  const range = (from: number, count: number, chars: number, seed: number): BibleVerseDto[] =>
    Array.from({ length: count }, (_, i) => verse(3, from + i, syntheticText(chars, seed + i)));
  const longWords = `${syntheticText(120, 7)} ${"Anticonstitucionalmentemuyextraordinariamente".repeat(1)} ${syntheticText(80, 8)} ${"x".repeat(150)} ${syntheticText(100, 9)}`;
  return [
    plain("corto", [verse(3, 16, syntheticText(60, 1))], BASE, { superscript: true }),
    plain("tipico", [verse(3, 16, syntheticText(170, 2))], BASE, { superscript: true }),
    plain("rango-3-versiculos", range(16, 3, 160, 3), BASE, { superscript: true }),
    plain("muy-largo-1100", [verse(1, 1, syntheticText(1100, 4))], BASE, { superscript: true }),
    plain("muy-largo-3000", [verse(1, 1, syntheticText(3000, 5))], BASE, { superscript: true }),
    plain("palabras-largas", [verse(1, 1, longWords)], BASE, { superscript: true }),
    plain("sin-espacios-900", [verse(1, 1, "z".repeat(900))], BASE, { superscript: true }),
    plain("superindice-163-174", range(163, 12, 90, 20), BASE, { superscript: true }),
    plain("contorno-y-sombra", [verse(1, 1, syntheticText(700, 6))], SHADOW_HEAVY, { superscript: true }),
    plain("con-atribucion", [verse(3, 16, syntheticText(400, 30))], BASE, {
      superscript: true, showAttribution: true, attribution: `Aviso de prueba inventado. ${syntheticText(180, 31)}`,
    }),
    plain("fuente-grande-110", [verse(3, 16, syntheticText(300, 40))], { ...BASE, fontSize: 110, minFontSize: 60 }, { superscript: true }),
    plain("margenes-anchos", [verse(3, 16, syntheticText(500, 41))], { ...BASE, marginLeft: 300, marginRight: 300, align: "left" }, { superscript: true }),
    plain("serif-sin-superindice", [verse(3, 16, syntheticText(600, 42))], { ...BASE, fontFamily: "Georgia", align: "left", verticalAlign: "top" }),
  ];
}

/** Texto de la página sin superíndices ni espacios de relleno, para comparar con el original. */
const normalize = (text: string): string => text.replace(/[\u2070\u00b9\u00b2\u00b3\u2074-\u2079]/g, "").replace(/\s+/g, "");

export function runLayoutCheck(doc: Document = document): LayoutReport {
  const started = performance.now();
  const cases: CaseReport[] = layoutCases().map((c) => {
    const measurer = createBlocksMeasurer(c.theme, doc);
    try {
      const t0 = performance.now();
      const built = buildPassage(
        c.verses,
        { bibleName: "PRB", attribution: c.attribution, showAttribution: c.showAttribution, superscript: c.superscript },
        (blocks) => measurer.fits(blocks),
      );
      const ms = performance.now() - t0;
      const limit = availableHeight(c.theme);
      const pages = built.pages.map((blocks) => {
        const measuredHeight = measurer.height(blocks);
        return { blocks, measuredHeight, fits: measuredHeight <= limit };
      });
      const original = c.verses.map((v) => v.text).join("");
      const rebuilt = built.pages.map((p) => p[0] ?? "").join("");
      return {
        name: c.name,
        fontFamily: c.theme.fontFamily,
        fontSize: c.theme.fontSize,
        limit,
        inkBleed: inkBleed(c.theme),
        safetyMargin: safetyMargin(c.theme),
        pages,
        lossless: normalize(rebuilt) === normalize(original),
        allFit: pages.every((p) => p.fits),
        ms: Math.round(ms * 10) / 10,
        projectionTheme: projectionTheme(c.theme),
      };
    } finally {
      measurer.dispose();
    }
  });
  return { userAgent: navigator.userAgent, cases, ok: cases.every((c) => c.lossless && c.allFit), totalMs: Math.round(performance.now() - started) };
}

declare global {
  interface Window {
    /** Solo para `tools/bible-layout-test.ps1` (lo invoca por la depuración remota de WebView2). */
    __oakLayoutCheck?: () => LayoutReport;
  }
}

if (typeof window !== "undefined") window.__oakLayoutCheck = () => runLayoutCheck();
