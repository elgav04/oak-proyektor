/**
 * Las réplicas aplican un snapshot solo si su secuencia es ESTRICTAMENTE mayor que la última aplicada (hallazgo I3):
 * así un snapshot viejo que llega tarde, o repetido, nunca pisa a uno más nuevo.
 */
export function isNewer(last: number | null, next: number): boolean {
  return last === null || next > last;
}