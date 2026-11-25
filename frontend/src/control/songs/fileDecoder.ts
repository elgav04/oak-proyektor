/**
 * Lectura de un .txt de una canción en el panel: UTF-8 estricto y, si falla, Windows-1252 INDICÁNDOLO (la lección de los archivos de la Biblia con mojibake),
 * UTF-16 solo con BOM, y rechazo claro de lo que no es texto. Refleja `SongTextDecoder` del backend (que usa la orden de consola `--import-song`). Función pura:
 * recibe los bytes ya leídos (el tamaño se comprueba ANTES de leerlos).
 */

export const MAX_FILE_BYTES = 256 * 1024;

export type ForcedEncoding = "auto" | "utf-8" | "windows-1252" | "utf-16le" | "utf-16be";

export type DecodeResult =
  | { ok: true; text: string; encoding: string; fellBack: boolean; warnings: string[] }
  | { ok: false; message: string };

const startsWith = (bytes: Uint8Array, prefix: readonly number[]): boolean => prefix.every((b, i) => bytes[i] === b);
const ascii = (text: string): number[] => Array.from(text, (c) => c.charCodeAt(0));

function notTextReason(bytes: Uint8Array): string | null {
  if (startsWith(bytes, [0x50, 0x4b, 0x03, 0x04])) return "El archivo parece un documento de Word o PowerPoint (o un .zip), no un archivo de texto. Guárdalo como «Texto sin formato (.txt)» y vuelve a intentarlo.";
  if (startsWith(bytes, ascii("%PDF"))) return "El archivo es un PDF, no un archivo de texto.";
  if (startsWith(bytes, ascii("{\\rtf"))) return "El archivo es un RTF (texto con formato), no un archivo de texto sin formato. Guárdalo como .txt.";
  if (startsWith(bytes, [0xd0, 0xcf, 0x11, 0xe0])) return "El archivo parece un documento antiguo de Office (.doc/.ppt), no un archivo de texto. Guárdalo como .txt.";
  if (startsWith(bytes, [0x89, 0x50, 0x4e, 0x47]) || startsWith(bytes, [0xff, 0xd8, 0xff]) || startsWith(bytes, ascii("GIF8"))) return "El archivo es una imagen, no un archivo de texto.";
  if ((bytes[0] === 0xff && bytes[1] === 0xfe) || (bytes[0] === 0xfe && bytes[1] === 0xff)) return null;   // UTF-16 con BOM: los ceros son normales
  let controls = 0;
  for (const b of bytes) {
    if (b === 0) return "El archivo contiene bytes nulos: parece un archivo binario, no un archivo de texto.";
    if (b < 0x20 && b !== 0x09 && b !== 0x0a && b !== 0x0d && b !== 0x0c) controls++;
  }
  if (controls > 8 && controls * 50 > bytes.length) return "El archivo contiene muchos caracteres de control: parece un archivo binario, no un archivo de texto.";
  return null;
}

/** Secuencias típicas de un texto UTF-8 leído como Windows-1252 («Ã©», «Ã±», «Â¿», «â€™»). */
export function looksMojibake(text: string): boolean {
  return /Ã[ -¿ŒœŠšŸŽžƒˆ˜–-›€™]|Â[ -¿]|â€/.test(text);
}

function decodeAs(bytes: Uint8Array, label: string): string | null {
  try {
    return new TextDecoder(label, { fatal: true, ignoreBOM: true }).decode(bytes);
  } catch {
    return null;
  }
}

export function decodeSongFile(bytes: Uint8Array, forced: ForcedEncoding = "auto"): DecodeResult {
  if (bytes.length > MAX_FILE_BYTES) return { ok: false, message: `El archivo es demasiado grande para ser la letra de una canción (${String(Math.round(bytes.length / 1024))} KB; el máximo es ${String(MAX_FILE_BYTES / 1024)} KB).` };
  if (bytes.length === 0) return { ok: true, text: "", encoding: "utf-8", fellBack: false, warnings: [] };
  const notText = notTextReason(bytes);
  if (notText) return { ok: false, message: notText };

  const warnings: string[] = [];
  let text: string | null = null;
  let encoding = "utf-8";
  let fellBack = false;

  const hasUtf8Bom = bytes[0] === 0xef && bytes[1] === 0xbb && bytes[2] === 0xbf;
  const isLe = bytes[0] === 0xff && bytes[1] === 0xfe;
  const isBe = bytes[0] === 0xfe && bytes[1] === 0xff;

  if (forced === "utf-16le" || (forced === "auto" && isLe)) {
    text = decodeAs(bytes.subarray(isLe ? 2 : 0), "utf-16le");
    encoding = "utf-16le";
    if (text === null) return { ok: false, message: "El archivo dice estar en UTF-16 pero su contenido no es texto válido." };
  } else if (forced === "utf-16be" || (forced === "auto" && isBe)) {
    text = decodeAs(bytes.subarray(isBe ? 2 : 0), "utf-16be");
    encoding = "utf-16be";
    if (text === null) return { ok: false, message: "El archivo dice estar en UTF-16 pero su contenido no es texto válido." };
  } else if (forced === "windows-1252") {
    text = new TextDecoder("windows-1252").decode(bytes);
    encoding = "windows-1252";
    warnings.push("Se interpretó como Windows-1252 porque lo elegiste.");
  } else {
    const body = hasUtf8Bom ? bytes.subarray(3) : bytes;
    text = decodeAs(body, "utf-8");
    encoding = hasUtf8Bom ? "utf-8-bom" : "utf-8";
    if (text === null) {
      if (forced === "utf-8") return { ok: false, message: "El archivo no es UTF-8 válido: prueba con Windows-1252." };
      text = new TextDecoder("windows-1252").decode(body);
      encoding = "windows-1252";
      fellBack = true;
      warnings.push("El archivo no está en UTF-8: se interpretó como Windows-1252. Revisa que los acentos y la «ñ» se vean bien en la vista previa.");
    }
  }

  if (text.includes("�")) warnings.push("El texto contiene caracteres «�»: parte del archivo no se pudo leer bien.");
  if (looksMojibake(text)) warnings.push("Los acentos parecen dañados (por ejemplo «Ã©» en vez de «é»): el archivo se guardó con una codificación equivocada. Prueba con otra codificación.");
  return { ok: true, text, encoding, fellBack, warnings };
}
