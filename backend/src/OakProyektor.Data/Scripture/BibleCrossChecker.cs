using System.Globalization;
using System.Text;
using Dapper;
using OakProyektor.Core.Scripture;

namespace OakProyektor.Data.Scripture;

/// <summary>
/// Verificación cruzada: compara una Biblia YA importada con OTRO archivo (que NO se importa) y reporta, solo con conteos y referencias, cuántos
/// versículos coinciden y cuáles difieren. Las diferencias se clasifican, de menor a mayor: (a) solo espacios o puntuación, (b) solo tildes o
/// mayúsculas, (c) palabras distintas. Antes de comparar se aplica al archivo la misma limpieza y el mismo perfil de numeración con que se importó la Biblia.
/// <para>Defecto conocido de una fuente: en el Salmo 119 una fuente puede arrastrar el rótulo de la estrofa siguiente (el nombre de la letra hebrea) pegado
/// al final de los versículos 8, 16, …, 168. Es UNA palabra de más al final; se cuenta aparte (<c>KnownLabelVerses</c>) y no como diferencia de palabras.</para>
/// </summary>
public sealed class BibleCrossChecker(ISqliteConnectionFactory connections, IDatabaseReadiness readiness)
{
    /// <summary>Resultado esperado: menos de este número de versículos con palabras distintas (sin contar los rótulos del Salmo 119).</summary>
    public const int ExpectedWordDifferencesBelow = 40;
    public const int ExpectedPsalm119Labels = 21;
    private const int Psalm = 19, PsalmChapter = 119, StanzaLength = 8;

    public async Task<CrossCheckReport> CompareAsync(string abbreviation, string path, string? formatId, int maxReferences, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) throw new BibleException(BibleErrorCodes.FileMissing, "No existe el archivo con el que comparar.");
        var format = formatId is null ? BibleSourceFormats.All[1] : BibleSourceFormats.Find(formatId)
            ?? throw new BibleException(BibleErrorCodes.InvalidRequest, $"Formato desconocido «{formatId}».");

        // Lo que hay en la BD.
        var database = new Dictionary<(int, int, int), string>();
        string? storedProfile;
        await using (var connection = await connections.OpenWhenReadyAsync(readiness, cancellationToken).ConfigureAwait(false))
        {
            var bible = (await connection.QueryAsync<(long Id, string? Profile)>(new CommandDefinition(
                "SELECT Id, NumberingProfile FROM Bibles WHERE Abbreviation = @abbreviation", new { abbreviation }, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();
            if (bible.Count == 0) throw new BibleException(BibleErrorCodes.NotFound, $"La Biblia «{abbreviation}» no está instalada.");
            storedProfile = bible[0].Profile;

            await using var read = connection.CreateCommand();
            read.CommandText = "SELECT b.BookNumber, v.Chapter, v.VerseNumber, v.Text FROM Verses v JOIN Books b ON b.Id = v.BookId WHERE b.BibleId = $id";
            read.Parameters.AddWithValue("$id", bible[0].Id);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                database[(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2))] = reader.GetString(3);
        }

        // Lo que hay en el archivo, con la misma limpieza y el mismo perfil de numeración.
        BibleSourceData data;
        try { data = await format.ReadAsync(path, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException or IOException)
        { throw new BibleException(BibleErrorCodes.SourceInvalid, $"No se pudo leer el archivo como «{format.Id}»: {ex.Message}", ex); }
        NumberingProfiles.TryGet(storedProfile, out var profile);
        var cleaned = data.Verses.Select(v => v with { Text = VerseCleaner.Clean(v.Text) }).ToList();
        var file = NumberingProfileApplier.Apply(cleaned, profile).Verses.ToDictionary(v => (v.BookNumber, v.Chapter, v.VerseNumber), v => v.Text);

        var identical = 0; var punctuation = 0; var accent = 0; var words = 0; var labels = 0;
        var accentRefs = new List<string>(); var wordRefs = new List<string>(); var labelRefs = new List<string>();
        foreach (var key in database.Keys.Where(file.ContainsKey).Order())
        {
            var a = database[key]; var b = file[key];
            if (a == b) { identical++; continue; }
            if (Letters(a) == Letters(b)) { punctuation++; continue; }
            if (Fold(a) == Fold(b)) { accent++; AddRef(accentRefs, key, maxReferences); continue; }
            if (IsStanzaLabel(key, a, b)) { labels++; AddRef(labelRefs, key, maxReferences); continue; }
            words++; AddRef(wordRefs, key, maxReferences);
        }

        var onlyDatabase = database.Keys.Where(k => !file.ContainsKey(k)).Order().ToList();
        var onlyFile = file.Keys.Where(k => !database.ContainsKey(k)).Order().ToList();

        var warnings = new List<string>();
        if (words >= ExpectedWordDifferencesBelow)
            warnings.Add($"{words} versículos con palabras distintas; se esperaban menos de {ExpectedWordDifferencesBelow}. Revisa las referencias.");
        if (labels != 0 && labels != ExpectedPsalm119Labels)
            warnings.Add($"{labels} versículos del Salmo 119 con una palabra de más al final; se esperaban {ExpectedPsalm119Labels} (8, 16, … 168).");
        if (onlyDatabase.Count + onlyFile.Count > 0)
            warnings.Add($"{onlyDatabase.Count} versículos solo en la BD y {onlyFile.Count} solo en el archivo: numeración distinta (¿perfil de numeración?).");

        return new CrossCheckReport(
            format.Id, profile.Id, database.Count, file.Count, identical, punctuation, accent, words, labels, onlyDatabase.Count, onlyFile.Count,
            accentRefs, wordRefs, labelRefs, [.. onlyDatabase.Take(maxReferences).Select(Ref)], [.. onlyFile.Take(maxReferences).Select(Ref)], warnings);
    }

    private static void AddRef(List<string> list, (int Book, int Chapter, int Verse) key, int max) { if (list.Count < max) list.Add(Ref(key)); }

    private static string Ref((int Book, int Chapter, int Verse) k) =>
        $"{CanonicalBooks.Find(k.Book)?.Usfm ?? "#" + k.Book} {k.Chapter.ToString(CultureInfo.InvariantCulture)}:{k.Verse.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Solo letras y dígitos (ignora espacios y puntuación).</summary>
    private static string Letters(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text) if (char.IsLetterOrDigit(ch)) sb.Append(ch);
        return sb.ToString();
    }

    /// <summary>Solo letras y dígitos, sin tildes y sin distinguir mayúsculas.</summary>
    private static string Fold(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Nombres de las letras hebreas que encabezan las estrofas del Salmo 119 (en español, sin tildes ni mayúsculas): la estrofa N empieza en el versículo 8N-7, así que el
    /// rótulo que una fuente defectuosa pega al final del versículo 8N es el de la estrofa N+1 (Bet tras el 8, Guímel tras el 16… Tau tras el 168). Son nombres de letras, no texto bíblico.
    /// </summary>
    private static readonly string[] StanzaLabels =
        ["bet", "guimel", "dalet", "he", "vau", "zain", "chet", "tet", "yod", "caf", "lamed", "mem", "nun", "samec", "ayin", "pe", "tsade", "cof", "resh", "sin", "tau"];

    /// <summary>El rótulo (plegado) que corresponde detrás del versículo indicado del Salmo 119; null si no es el final de una estrofa con rótulo siguiente.</summary>
    public static string? StanzaLabelFor(int verse) =>
        verse % StanzaLength == 0 && verse / StanzaLength is >= 1 and var n && n <= StanzaLabels.Length ? StanzaLabels[n - 1] : null;

    /// <summary>
    /// Salmo 119, versículo múltiplo de 8: un lado es el otro más el RÓTULO de la estrofa siguiente al final. Se compara por LETRAS (sin espacios ni puntuación): la fuente que lo
    /// arrastra suele perder además los espacios tras la puntuación, y el rótulo queda pegado a la última palabra, así que contar palabras no sirve.
    /// </summary>
    private static bool IsStanzaLabel((int Book, int Chapter, int Verse) key, string a, string b)
    {
        if (key.Book != Psalm || key.Chapter != PsalmChapter || StanzaLabelFor(key.Verse) is not { } label) return false;
        var (foldedA, foldedB) = (Fold(a), Fold(b));
        var (shorter, longer) = foldedA.Length <= foldedB.Length ? (foldedA, foldedB) : (foldedB, foldedA);
        return longer.Length == shorter.Length + label.Length && longer.StartsWith(shorter, StringComparison.Ordinal) && longer.EndsWith(label, StringComparison.Ordinal);
    }
}
