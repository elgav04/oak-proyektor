// Dígitos Unicode en superíndice (decisión del operador: sin tocar la salida, que sigue «tonta»). Escapes \u para no depender de la codificación del archivo.
// Si una fuente no los trae, el navegador cae a otra: la prueba de layout real (tools/bible-layout-test.ps1) informa qué fuente los pinta.
const SUPERSCRIPT_DIGITS = ["⁰", "¹", "²", "³", "⁴", "⁵", "⁶", "⁷", "⁸", "⁹"] as const;

/** «16» → «¹⁶». Solo convierte dígitos ASCII. */
export function toSuperscript(value: number | string): string {
  return String(value).replace(/[0-9]/g, (d) => SUPERSCRIPT_DIGITS[Number(d)] ?? d);
}

/** ¿El texto son solo dígitos en superíndice (un número de versículo)? */
export function isSuperscriptNumber(text: string): boolean {
  return /^[\u2070\u00b9\u00b2\u00b3\u2074-\u2079]+$/.test(text);
}
