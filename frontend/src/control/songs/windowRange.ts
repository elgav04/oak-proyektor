/**
 * Ventana de una lista virtualizada de filas de alto fijo: qué filas hay que pintar según el desplazamiento. Función pura. Con una lista de miles de canciones solo
 * se pintan las visibles (más un margen), nunca todas (CLAUDE.md: «Listas largas: virtualizar»).
 */
export function windowRange(scrollTop: number, viewport: number, rowHeight: number, count: number, overscan = 6): { start: number; end: number } {
  if (count <= 0 || rowHeight <= 0) return { start: 0, end: 0 };
  // Sin altura medible (aún sin layout, o un entorno sin él): se pinta un primer bloque razonable.
  if (!(viewport > 0)) return { start: 0, end: Math.min(count, 40) };
  const first = Math.max(0, Math.floor(Math.max(0, scrollTop) / rowHeight) - overscan);
  const last = Math.min(count, Math.ceil((Math.max(0, scrollTop) + viewport) / rowHeight) + overscan);
  return { start: Math.min(first, count), end: Math.max(Math.min(first, count), last) };
}
