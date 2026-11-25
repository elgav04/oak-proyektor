using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OakProyektor.Core.Songs;

/// <summary>
/// Nombres de sección en español y sus alias (sin distinguir mayúsculas ni tildes ni guiones: «Pre-coro», «precoro» y «PRE CORO» son lo mismo). Funciones puras.
/// El tipo de una sección (<see cref="SongSectionKinds"/>) se DERIVA de su etiqueta con <see cref="KindOf"/>; la etiqueta se escribe en su forma canónica
/// (<see cref="CanonicalLabel"/>: «verso 1» → «Verso 1») para que un ciclo texto → modelo → texto sea estable.
/// </summary>
public static partial class SectionAliases
{
    private sealed record Alias(string Key, string Kind, string Display);

    private static readonly Alias[] Table =
    [
        new("verso", SongSectionKinds.Verse, "Verso"),
        new("estrofa", SongSectionKinds.Verse, "Estrofa"),
        new("coro", SongSectionKinds.Chorus, "Coro"),
        new("estribillo", SongSectionKinds.Chorus, "Estribillo"),
        new("precoro", SongSectionKinds.PreChorus, "Pre-coro"),
        new("preestribillo", SongSectionKinds.PreChorus, "Pre-estribillo"),
        new("puente", SongSectionKinds.Bridge, "Puente"),
        new("final", SongSectionKinds.Ending, "Final"),
        new("coda", SongSectionKinds.Ending, "Coda"),
        new("outro", SongSectionKinds.Ending, "Outro"),
        new("intro", SongSectionKinds.Intro, "Intro"),
        new("introduccion", SongSectionKinds.Intro, "Introducción"),
        new("interludio", SongSectionKinds.Interlude, "Interludio"),
        new("instrumental", SongSectionKinds.Interlude, "Instrumental"),
    ];

    private static readonly Dictionary<string, Alias> ByKey = Table.ToDictionary(a => a.Key, StringComparer.Ordinal);

    /// <summary>Abreviaturas de la línea «Orden:» («V1 C V2 C»). «E» es Estribillo (= coro).</summary>
    private static readonly Dictionary<string, string> Abbreviations = new(StringComparer.Ordinal)
    {
        ["V"] = SongSectionKinds.Verse,
        ["C"] = SongSectionKinds.Chorus,
        ["E"] = SongSectionKinds.Chorus,
        ["P"] = SongSectionKinds.Bridge,
        ["PC"] = SongSectionKinds.PreChorus,
        ["F"] = SongSectionKinds.Ending,
        ["I"] = SongSectionKinds.Intro,
    };

    /// <summary>Clave de comparación: minúsculas, sin tildes y solo letras y dígitos («Pre-coro 2» → «precoro2»). Dos etiquetas con la misma clave son la MISMA sección.</summary>
    public static string Key(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        string decomposed;
        try { decomposed = text.Normalize(NormalizationForm.FormD); }
        catch (ArgumentException) { decomposed = text; }
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>¿La etiqueta es solo un nombre conocido con número opcional («Verso 1», «coro», «Pre-coro 2»)? Devuelve el nombre canónico y el número.</summary>
    public static bool TryParseAlias(string? label, out string display, out string kind, out int? number)
    {
        display = ""; kind = SongSectionKinds.Other; number = null;
        var key = Key(label);
        if (key.Length == 0) return false;
        foreach (var alias in Table)
        {
            if (!key.StartsWith(alias.Key, StringComparison.Ordinal)) continue;
            var rest = key.AsSpan(alias.Key.Length);
            if (rest.Length > 3) continue;
            if (rest.Length == 0) { display = alias.Display; kind = alias.Kind; return true; }
            if (!int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) continue;
            display = alias.Display; kind = alias.Kind; number = n;
            return true;
        }
        return false;
    }

    /// <summary>Etiqueta en su forma canónica: espacios colapsados y, si es un nombre conocido con número opcional, escrito como «Verso 1» / «Pre-coro» / «Coro». El resto se conserva tal cual.</summary>
    public static string CanonicalLabel(string? raw)
    {
        var collapsed = SongTextNormalizer.NormalizeSingleLine(raw);
        if (TryParseAlias(collapsed, out var display, out _, out var number))
            return number is { } n ? $"{display} {n.ToString(CultureInfo.InvariantCulture)}" : display;
        return collapsed;
    }

    /// <summary>
    /// Tipo de sección derivado de la etiqueta: por su primera palabra («Coro final» → coro; «Verso1» → verso; «Pre-coro 2» → pre-coro). Lo desconocido es <c>other</c>.
    /// </summary>
    public static string KindOf(string? label)
    {
        var tokens = Words().Matches(Fold(label)).Select(m => m.Value).ToList();
        if (tokens.Count == 0) return SongSectionKinds.Other;
        var first = StripDigits(tokens[0]);
        if (first == "pre" && tokens.Count > 1 && ByKey.TryGetValue("pre" + StripDigits(tokens[1]), out var pre)) return pre.Kind;
        return ByKey.TryGetValue(first, out var alias) ? alias.Kind : SongSectionKinds.Other;
    }

    /// <summary>Tipo que representa una abreviatura de la línea «Orden:» («V», «C», «PC»…); null si no es una.</summary>
    public static string? KindOfAbbreviation(string letters) => Abbreviations.TryGetValue(letters.ToUpperInvariant(), out var kind) ? kind : null;

    /// <summary>Número final de una etiqueta con nombre conocido («Verso 2» → 2; «Coro» → null; «Mi canción» → null).</summary>
    public static int? NumberOf(string? label) => TryParseAlias(label, out _, out _, out var n) ? n : null;

    private static string Fold(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        try
        {
            var sb = new StringBuilder();
            foreach (var c in text.Normalize(NormalizationForm.FormD))
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }
        catch (ArgumentException) { return text.ToLowerInvariant(); }
    }

    private static string StripDigits(string token) => token.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Words();
}
