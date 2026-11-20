using System.Globalization;
using System.Text;

namespace OakProyektor.Core.Scripture;

/// <summary>Unir los versículos <c>FirstVerse</c> a <c>LastVerse</c> de un capítulo en uno solo (el primero). Solo referencias: nunca contiene texto bíblico.</summary>
public sealed record VerseMerge(int BookNumber, int Chapter, int FirstVerse, int LastVerse)
{
    public string Label => $"{CanonicalBooks.Get(BookNumber).Usfm} {Chapter.ToString(CultureInfo.InvariantCulture)}:{FirstVerse.ToString(CultureInfo.InvariantCulture)}-{LastVerse.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>
/// «Perfil de numeración»: lista de uniones de versículos que se aplican AL IMPORTAR para que una fuente que numera distinto (p. ej. un versículo
/// partido en dos) coincida con la numeración de la Biblia impresa del operador. Se define en código, por referencias; se activa o no por Biblia
/// (el id del perfil se guarda en <c>Bibles.NumberingProfile</c>; ausente = sin perfil).
/// </summary>
public sealed record NumberingProfile(string Id, string Description, IReadOnlyList<VerseMerge> Merges);

public static class NumberingProfiles
{
    public const string NoneId = "none";

    /// <summary>Sin uniones: la numeración de la fuente se respeta tal cual.</summary>
    public static NumberingProfile None { get; } = new(NoneId, "Sin perfil: se respeta la numeración de la fuente.", []);

    /// <summary>
    /// Une Salmos 47:9+10 y 3 Juan 14+15 (la numeración de la RVR1960 impresa que usa un total de 31 102 versículos, frente a las
    /// fuentes que los parten en 31 104). SOLO se activa si el operador lo pide: comprueba tu Biblia impresa con <c>--show-verse</c>.
    /// </summary>
    public static NumberingProfile MergePsalm47And3John { get; } = new(
        "merge-ps47-3jn",
        "Une Salmos 47:9-10 y 3 Juan 1:14-15 en un solo versículo cada uno (31 102 versículos en total).",
        [new VerseMerge(19, 47, 9, 10), new VerseMerge(64, 1, 14, 15)]);

    public static IReadOnlyList<NumberingProfile> All { get; } = [None, MergePsalm47And3John];

    /// <summary>El perfil con ese id (sin distinguir mayúsculas); <c>null</c>, vacío o «none» = <see cref="None"/>. Un id desconocido devuelve false.</summary>
    public static bool TryGet(string? id, out NumberingProfile profile)
    {
        if (string.IsNullOrWhiteSpace(id)) { profile = None; return true; }
        var found = All.FirstOrDefault(p => string.Equals(p.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
        profile = found ?? None;
        return found is not null;
    }
}

public sealed record NumberingResult(IReadOnlyList<SourceVerse> Verses, IReadOnlyList<string> AppliedMerges, IReadOnlyList<string> SkippedMerges);

public static class NumberingProfileApplier
{
    /// <summary>
    /// Aplica las uniones. Cada unión se aplica solo si la fuente tiene TODOS los versículos del tramo; si falta alguno (la fuente ya los trae unidos,
    /// o es una Biblia incompleta) no se toca y se informa en <c>SkippedMerges</c>. Une con un espacio, en orden de versículo.
    /// </summary>
    public static NumberingResult Apply(IReadOnlyList<SourceVerse> verses, NumberingProfile profile)
    {
        if (profile.Merges.Count == 0) return new NumberingResult(verses, [], []);

        var map = verses.ToDictionary(v => (v.BookNumber, v.Chapter, v.VerseNumber));
        var applied = new List<string>();
        var skipped = new List<string>();
        var removed = new HashSet<(int, int, int)>();
        var replaced = new Dictionary<(int, int, int), string>();

        foreach (var merge in profile.Merges)
        {
            var parts = new List<string>();
            var complete = true;
            for (var v = merge.FirstVerse; v <= merge.LastVerse; v++)
            {
                if (map.TryGetValue((merge.BookNumber, merge.Chapter, v), out var verse)) parts.Add(verse.Text);
                else { complete = false; break; }
            }
            if (!complete) { skipped.Add(merge.Label); continue; }

            var sb = new StringBuilder();
            foreach (var part in parts) { if (sb.Length > 0) sb.Append(' '); sb.Append(part); }
            replaced[(merge.BookNumber, merge.Chapter, merge.FirstVerse)] = sb.ToString();
            for (var v = merge.FirstVerse + 1; v <= merge.LastVerse; v++) removed.Add((merge.BookNumber, merge.Chapter, v));
            applied.Add(merge.Label);
        }

        var result = new List<SourceVerse>(verses.Count);
        foreach (var verse in verses)
        {
            var key = (verse.BookNumber, verse.Chapter, verse.VerseNumber);
            if (removed.Contains(key)) continue;
            result.Add(replaced.TryGetValue(key, out var text) ? verse with { Text = text } : verse);
        }
        return new NumberingResult(result, applied, skipped);
    }
}
