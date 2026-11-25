using System.Globalization;
using System.Text;

namespace OakProyektor.Core.Songs;

/// <summary>
/// Limpieza mínima del texto de canciones (como <c>VerseCleaner</c> en la Biblia): NFC, saltos de línea unificados, espacios colapsados y recortados por línea,
/// sin caracteres de control ni invisibles. NUNCA toca puntuación, tildes ni mayúsculas. Funciones puras que nunca lanzan.
/// </summary>
public static class SongTextNormalizer
{
    // Los caracteres especiales se escriben por su número (no como literales ni escapes \u): así el archivo fuente no contiene caracteres invisibles que un editor pueda alterar.
    private const int NextLine = 0x0085, LineSeparator = 0x2028, ParagraphSeparator = 0x2029;

    /// <summary>
    /// Normaliza <paramref name="input"/>: líneas separadas por <c>\n</c>, cada una con sus espacios colapsados y recortados. Los caracteres de control (salvo los saltos de línea y
    /// los espacios), los invisibles (ancho cero, BOM, marcas de dirección Unicode, guion blando) y los sustitutos sueltos se QUITAN y se cuentan en <paramref name="removed"/>.
    /// </summary>
    public static string Normalize(string? input, out int removed)
    {
        removed = 0;
        if (string.IsNullOrEmpty(input)) return "";

        var result = new StringBuilder(input.Length);
        var pendingSpace = false;
        var lineHasText = false;

        void NewLine()
        {
            result.Append('\n');
            pendingSpace = false;
            lineHasText = false;
        }

        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (c == '\r')
            {
                if (i + 1 < input.Length && input[i + 1] == '\n') i++;
                NewLine();
                continue;
            }
            if (c == '\n' || c == NextLine || c == LineSeparator || c == ParagraphSeparator)
            {
                NewLine();
                continue;
            }

            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < input.Length && char.IsLowSurrogate(input[i + 1]))
                {
                    if (pendingSpace && lineHasText) result.Append(' ');
                    pendingSpace = false;
                    result.Append(c).Append(input[++i]);
                    lineHasText = true;
                }
                else removed++;
                continue;
            }
            if (char.IsLowSurrogate(c)) { removed++; continue; }

            if (c == '\t' || char.IsWhiteSpace(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator)
            {
                if (lineHasText) pendingSpace = true;
                continue;
            }
            if (char.IsControl(c) || IsInvisible(c)) { removed++; continue; }

            if (pendingSpace) result.Append(' ');
            pendingSpace = false;
            result.Append(c);
            lineHasText = true;
        }

        var text = result.ToString();
        try { return text.Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { return text; }   // no debería pasar (ya no quedan sustitutos sueltos); por si acaso
    }

    /// <summary>Caracteres invisibles que no aportan nada a una letra y pueden engañar (marcas de dirección) o romper la búsqueda.</summary>
    private static bool IsInvisible(char c)
    {
        int n = c;
        return n == 0x00AD                    // guion blando
            || (n >= 0x200B && n <= 0x200F)   // ancho cero, ZWNJ, ZWJ, marcas de dirección
            || (n >= 0x202A && n <= 0x202E)   // incrustaciones y anulaciones de dirección
            || n == 0x2060                    // unidor de palabras
            || (n >= 0x2066 && n <= 0x2069)   // aislamientos de dirección
            || n == 0xFEFF;                   // BOM / espacio de ancho cero sin salto
    }

    /// <summary>
    /// Cuerpo de una sección: normalizado, sin líneas en blanco al principio ni al final y con cada grupo de líneas en blanco reducido a UNA (el corte manual de diapositiva).
    /// </summary>
    public static string NormalizeBody(string? input, out int removed) => JoinBody(Normalize(input, out removed).Split('\n'));

    /// <summary>Une las líneas (ya normalizadas) de un cuerpo: recorta los bordes en blanco y reduce los grupos de líneas en blanco a una sola.</summary>
    internal static string JoinBody(IReadOnlyList<string> lines)
    {
        var start = 0;
        var end = lines.Count - 1;
        while (start <= end && lines[start].Length == 0) start++;
        while (end >= start && lines[end].Length == 0) end--;
        if (start > end) return "";

        var sb = new StringBuilder();
        var lastBlank = false;
        for (var i = start; i <= end; i++)
        {
            var line = lines[i];
            if (line.Length == 0)
            {
                if (lastBlank) continue;
                lastBlank = true;
            }
            else lastBlank = false;
            if (i > start) sb.Append('\n');
            sb.Append(line);
        }
        return sb.ToString();
    }

    /// <summary>Título y metadatos de una sola línea: normalizados, con los saltos de línea convertidos en espacios. Vacío si no queda nada.</summary>
    public static string NormalizeSingleLine(string? input)
    {
        var text = Normalize(input, out _);
        return text.Contains('\n') ? string.Join(' ', text.Split('\n', StringSplitOptions.RemoveEmptyEntries)) : text;
    }
}
