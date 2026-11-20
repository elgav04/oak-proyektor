/**
 * Mayor tamaño entero en [min, max] para el que `fits` es verdadero (se asume monótono: si cabe a N, cabe a menos). Si ni `min` cabe, devuelve `min`.
 * Búsqueda binaria: ≤ 9 mediciones para el rango 8–400, sin bucles largos en el hilo principal.
 */
export function fitFontSize(min: number, max: number, fits: (size: number) => boolean): number {
  let lo = Math.min(min, max);
  let hi = Math.max(min, max);
  if (fits(hi)) return hi;
  if (!fits(lo)) return lo;
  while (hi - lo > 1) {
    const mid = Math.floor((lo + hi) / 2);
    if (fits(mid)) lo = mid;
    else hi = mid;
  }
  return lo;
}
