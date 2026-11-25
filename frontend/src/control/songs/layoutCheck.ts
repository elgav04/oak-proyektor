import type { TextTheme } from "@/ipc/protocol";
import { syntheticText } from "../bible/layoutCheck";
import { availableHeight, createBlocksMeasurer, inkBleed, safetyMargin } from "../bible/measure";
import { projectionTheme } from "../bible/slide";
import { SONG_DEFAULT_THEME } from "./defaults";
import { deriveSlides, paginateChunk, splitChunks, uniqueChunks, warmChunks, type SongSlide } from "./slides";

/**
 * PRUEBA DE LAYOUT REAL de las canciones (diagnóstico). Mide con el motor de layout del navegador (WebView2 en la app real; happy-dom NO mide) letras de forma
 * conocida con el TEMA DE CANCIONES sobre el lienzo de 1920×1080, y comprueba que la derivación de diapositivas nunca recorta ni pierde texto y que respeta los cortes
 * manuales. Todo el texto es SINTÉTICO (palabras inventadas). `tools/song-layout-test.ps1` la ejecuta dentro de la app real y además proyecta cada diapositiva en la
 * ventana de salida para comparar con lo que la salida realmente pinta (la misma comprobación que la de la Biblia).
 */

export interface SongLayoutCase {
  name: string;
  /** Secciones (texto con líneas en blanco = cortes manuales) y orden de reproducción (posiciones). */
  sections: { label: string; text: string }[];
  order: number[];
  theme: TextTheme;
}

export interface PageReport {
  blocks: string[];
  measuredHeight: number;
  fits: boolean;
}

export interface SongCaseReport {
  name: string;
  fontFamily: string;
  fontSize: number;
  limit: number;
  inkBleed: number;
  safetyMargin: number;
  pages: PageReport[];
  boundaries: string[];
  /** Cada trozo (entre cortes manuales) recupera TODO su texto al unir sus diapositivas, y ninguna diapositiva mezcla dos trozos. */
  lossless: boolean;
  allFit: boolean;
  manualCuts: number;
  /** Mediciones de layout que hizo (la sección repetida en el orden NO se vuelve a medir). */
  ms: number;
  projectionTheme: TextTheme;
}

export interface SongLayoutReport {
  userAgent: string;
  cases: SongCaseReport[];
  ok: boolean;
  totalMs: number;
  /** Lo más que tardó UNA medición (el cálculo se hace en tandas entre mediciones: este es el trabajo mínimo que no se puede partir). */
  maxSingleMeasureMs: number;
  /** El cálculo por tandas de la canción completa: tandas, la más larga de trabajo seguido y las veces que cedió el hilo. */
  sliced: { slides: number; yields: number; maxSliceMs: number; totalMs: number };
  /** ¿Funciona el almacenamiento local de la página (donde se guarda el borrador sin guardar)? */
  localStorageWorks: boolean;
}

const BASE: TextTheme = { ...SONG_DEFAULT_THEME };
const SHADOW_HEAVY: TextTheme = { ...BASE, outlineWidth: 8, shadowBlur: 24, shadowOffset: 8, lineHeight: 1.15 };

const lines = (count: number, seed: number, chars = 38): string => Array.from({ length: count }, (_, i) => syntheticText(chars, seed + i)).join("\n");

export function songLayoutCases(): SongLayoutCase[] {
  const one = (name: string, text: string, theme: TextTheme = BASE): SongLayoutCase => ({ name, sections: [{ label: "Verso 1", text }], order: [0], theme });
  const longWords = `${syntheticText(60, 7)} Anticonstitucionalmentemuyextraordinariamente ${syntheticText(50, 8)} ${"x".repeat(120)}\n${syntheticText(40, 9)}`;
  const full: SongLayoutCase = {
    name: "cancion-completa-con-repeticiones",
    sections: [
      { label: "Verso 1", text: `${lines(4, 100)}\n\n${lines(3, 110)}` },
      { label: "Coro", text: `${lines(4, 200)}\n\n${lines(4, 210)}` },
      { label: "Verso 2", text: lines(9, 300) },
      { label: "Puente", text: lines(3, 400) },
      { label: "Final", text: lines(2, 500) },
    ],
    order: [0, 1, 2, 1, 3, 1, 4],
    theme: BASE,
  };
  return [
    one("estrofa-corta-4-lineas", lines(4, 1)),
    one("dos-trozos-corte-manual", `${lines(3, 10)}\n\n${lines(3, 20)}`),
    one("tres-cortes-manuales", `${lines(2, 30)}\n\n${lines(2, 40)}\n\n${lines(2, 50)}\n\n${lines(2, 60)}`),
    one("estrofa-larga-14-lineas-corte-automatico", lines(14, 70)),
    one("linea-larguisima", syntheticText(900, 80)),
    one("palabras-largas", longWords),
    one("sin-espacios-700", "z".repeat(700)),
    one("contorno-y-sombra", lines(7, 90), SHADOW_HEAVY),
    one("fuente-grande-110", lines(5, 120), { ...BASE, fontSize: 110, minFontSize: 60 }),
    one("margenes-anchos-a-la-izquierda", lines(6, 130), { ...BASE, marginLeft: 300, marginRight: 300, align: "left" }),
    one("abajo-a-la-izquierda", lines(5, 140), { ...BASE, fontSize: 48, align: "left", verticalAlign: "bottom", marginLeft: 100, marginBottom: 100, minFontSize: 28 }),
    one("serif-arriba", lines(8, 150), { ...BASE, fontFamily: "Georgia", align: "left", verticalAlign: "top" }),
    full,
  ];
}

const normalize = (text: string): string => text.replace(/\s+/g, "");

/** Calcula las diapositivas de un caso con la medición REAL y las informa. */
function runCase(c: SongLayoutCase, doc: Document): { report: SongCaseReport; slides: SongSlide[]; singleMax: number } {
  const measurer = createBlocksMeasurer(c.theme, doc);
  try {
    let singleMax = 0;
    const fits = (text: string): boolean => {
      const t0 = performance.now();
      const ok = measurer.fits([text]);
      singleMax = Math.max(singleMax, performance.now() - t0);
      return ok;
    };
    const t0 = performance.now();
    const cache = new Map<string, string[]>();
    const pagesOf = (chunk: string): string[] => {
      let pages = cache.get(chunk);
      if (!pages) { pages = paginateChunk(chunk, fits); cache.set(chunk, pages); }
      return pages;
    };
    const sources = c.sections.map((s, i) => ({ uid: `s${String(i)}`, label: s.label, text: s.text }));
    const slides = deriveSlides(sources, c.order, pagesOf);
    const ms = performance.now() - t0;
    const limit = availableHeight(c.theme);
    const pages = slides.map((s) => ({ blocks: [s.text], measuredHeight: measurer.height([s.text]), fits: false }));
    pages.forEach((p) => { p.fits = p.measuredHeight <= limit; });

    // Sin pérdida: cada trozo (entre cortes manuales) recupera todo su texto, y ninguna diapositiva mezcla dos trozos (su texto sale de UN solo trozo).
    let lossless = true;
    let manualCuts = 0;
    const chunks = c.sections.flatMap((s) => splitChunks(s.text));
    for (const section of c.sections) manualCuts += Math.max(0, splitChunks(section.text).length - 1);
    for (const chunk of chunks) if (normalize(pagesOf(chunk).join("")) !== normalize(chunk)) lossless = false;
    const chunkNorms = chunks.map(normalize);
    for (const s of slides) if (!chunkNorms.some((n) => n.includes(normalize(s.text)))) lossless = false;

    return {
      slides,
      singleMax,
      report: {
        name: c.name, fontFamily: c.theme.fontFamily, fontSize: c.theme.fontSize, limit, inkBleed: inkBleed(c.theme), safetyMargin: safetyMargin(c.theme),
        pages, boundaries: slides.map((s) => s.boundary), lossless, allFit: pages.every((p) => p.fits), manualCuts,
        ms: Math.round(ms * 10) / 10, projectionTheme: projectionTheme(c.theme),
      },
    };
  } finally {
    measurer.dispose();
  }
}

/** La canción completa con el cálculo POR TANDAS (como lo hace el controlador): cuánto trabaja seguido y cuántas veces cede el hilo. */
async function runSliced(doc: Document): Promise<SongLayoutReport["sliced"]> {
  const c = songLayoutCases().find((x) => x.name === "cancion-completa-con-repeticiones");
  if (!c) return { slides: 0, yields: 0, maxSliceMs: 0, totalMs: 0 };
  const big: SongLayoutCase = { ...c, sections: Array.from({ length: 40 }, (_, i) => ({ label: `Parte ${String(i + 1)}`, text: `${lines(5, 600 + i * 7)}\n\n${lines(4, 900 + i * 7)}` })), order: Array.from({ length: 40 }, (_, i) => i) };
  const measurer = createBlocksMeasurer(big.theme, doc);
  try {
    const cache = new Map<string, string[]>();
    const sources = big.sections.map((s, i) => ({ uid: `s${String(i)}`, label: s.label, text: s.text }));
    const pagesOf = (chunk: string): string[] => {
      let pages = cache.get(chunk);
      if (!pages) { pages = paginateChunk(chunk, (t) => measurer.fits([t])); cache.set(chunk, pages); }
      return pages;
    };
    let yields = 0;
    let maxSlice = 0;
    let sliceStart = performance.now();
    const started = sliceStart;
    // Presupuesto 0 a propósito (cede tras cada trozo): medir es tan rápido que con el real (12 ms) quizá no hiciera falta ceder ni una vez; así el mecanismo se comprueba SIEMPRE.
    await warmChunks(uniqueChunks(sources, big.order), pagesOf, {
      budgetMs: 0,
      now: () => performance.now(),
      yieldNow: async () => { maxSlice = Math.max(maxSlice, performance.now() - sliceStart); yields++; await new Promise<void>((r) => { setTimeout(r, 0); }); sliceStart = performance.now(); },
      cancelled: () => false,
    });
    maxSlice = Math.max(maxSlice, performance.now() - sliceStart);
    const slides = deriveSlides(sources, big.order, pagesOf);
    return { slides: slides.length, yields, maxSliceMs: Math.round(maxSlice * 10) / 10, totalMs: Math.round(performance.now() - started) };
  } finally {
    measurer.dispose();
  }
}

function storageWorks(): boolean {
  try {
    const key = "oak-proyektor.layout-check";
    window.localStorage.setItem(key, "1");
    const ok = window.localStorage.getItem(key) === "1";
    window.localStorage.removeItem(key);
    return ok;
  } catch {
    return false;
  }
}

export async function runSongLayoutCheck(doc: Document = document): Promise<SongLayoutReport> {
  const started = performance.now();
  let maxSingle = 0;
  const cases = songLayoutCases().map((c) => {
    const r = runCase(c, doc);
    maxSingle = Math.max(maxSingle, r.singleMax);
    return r.report;
  });
  const sliced = await runSliced(doc);
  return {
    userAgent: navigator.userAgent,
    cases,
    ok: cases.every((c) => c.lossless && c.allFit),
    totalMs: Math.round(performance.now() - started),
    maxSingleMeasureMs: Math.round(maxSingle * 10) / 10,
    sliced,
    localStorageWorks: storageWorks(),
  };
}

declare global {
  interface Window {
    /** Solo para `tools/song-layout-test.ps1` (lo invoca por la depuración remota de WebView2). Devuelve una promesa. */
    __oakSongLayoutCheck?: () => Promise<SongLayoutReport>;
  }
}

if (typeof window !== "undefined") window.__oakSongLayoutCheck = () => runSongLayoutCheck();
