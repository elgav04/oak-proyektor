import type { BibleSettingsPayload, BibleShortNameDto, TextTheme } from "@/ipc/protocol";

/**
 * Fusión de la configuración al guardar (H11). La pantalla de configuración trabaja sobre un BORRADOR tomado al abrirla; mientras tanto la configuración real puede
 * cambiar desde otro sitio (la casilla «Proyectar al navegar» de la pestaña guarda sola). Mandar el borrador entero pisaba ese cambio. Aquí solo se aplica
 * lo que el operador CAMBIÓ en esta pantalla (borrador ≠ lo que había al abrirla); todo lo demás conserva el valor ACTUAL.
 *
 * @param initial lo que había al abrir la pantalla (o tras el último guardado)
 * @param draft   lo que hay ahora en el formulario
 * @param current la configuración actual de la réplica (la última confirmada por el backend)
 */
export function mergeSettings(initial: BibleSettingsPayload, draft: BibleSettingsPayload, current: BibleSettingsPayload): BibleSettingsPayload {
  const changed = <T,>(a: T, b: T): boolean => JSON.stringify(a) !== JSON.stringify(b);
  const pick = <T,>(before: T, mine: T, now: T): T => (changed(before, mine) ? mine : now);

  const theme = { ...current.theme } as Record<string, unknown>;
  for (const key of Object.keys(draft.theme) as (keyof TextTheme)[]) theme[key] = pick(initial.theme[key], draft.theme[key], current.theme[key]);

  // Nombres cortos: por Biblia (cada una cuenta como un valor aparte).
  const shortNames = new Map<string, string>(current.shortNames.map((n) => [n.bible, n.shortName]));
  const bibles = new Set([...initial.shortNames.map((n) => n.bible), ...draft.shortNames.map((n) => n.bible)]);
  for (const bible of bibles) {
    const before = initial.shortNames.find((n) => n.bible === bible)?.shortName ?? null;
    const mine = draft.shortNames.find((n) => n.bible === bible)?.shortName ?? null;
    if (!changed(before, mine)) continue;
    if (mine === null) shortNames.delete(bible);
    else shortNames.set(bible, mine);
  }

  return {
    defaultBible: pick(initial.defaultBible, draft.defaultBible, current.defaultBible),
    projectOnNavigate: pick(initial.projectOnNavigate, draft.projectOnNavigate, current.projectOnNavigate),
    superscriptNumbers: pick(initial.superscriptNumbers, draft.superscriptNumbers, current.superscriptNumbers),
    showAttribution: pick(initial.showAttribution, draft.showAttribution, current.showAttribution),
    transitionMs: pick(initial.transitionMs, draft.transitionMs, current.transitionMs),
    theme: theme as unknown as TextTheme,
    shortNames: [...shortNames].map(([bible, shortName]): BibleShortNameDto => ({ bible, shortName })),
  };
}
