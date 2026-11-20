using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using OakProyektor.Core.Scripture;

namespace OakProyektor.Data.Scripture;

/// <summary>
/// Validación de una Biblia YA importada. AVISA, no falla: devuelve conteos y referencias (códigos USFM), nunca texto. Comprueba lo que se puede
/// saber sin una tabla de versículos por capítulo: que estén los 66 libros y sus capítulos (cifra del canon), que no haya versículos vacíos, que cada
/// capítulo empiece en 1 y no tenga huecos, y la diferencia del total frente a los 31 102 versículos habituales del canon protestante.
/// </summary>
public static class BibleValidator
{
    /// <summary>Total habitual de versículos de la numeración protestante de 66 libros.</summary>
    public const int StandardVerseTotal = 31_102;
    public const int MaxListedReferences = 50;

    public static async Task<ValidationReport> ValidateAsync(SqliteConnection connection, long bibleId, CancellationToken cancellationToken)
    {
        var books = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(DISTINCT b.Id) FROM Books b JOIN Verses v ON v.BookId = b.Id WHERE b.BibleId = @bibleId", new { bibleId }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        var verses = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM Verses v JOIN Books b ON b.Id = v.BookId WHERE b.BibleId = @bibleId", new { bibleId }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        var empty = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM Verses v JOIN Books b ON b.Id = v.BookId WHERE b.BibleId = @bibleId AND length(trim(v.Text)) = 0", new { bibleId }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        // Por libro: capítulos distintos y el mayor (si no coinciden, hay capítulos intermedios sin versículos).
        var perBook = (await connection.QueryAsync<(long BookNumber, long Chapters, long MaxChapter)>(new CommandDefinition(
            """
            SELECT b.BookNumber AS BookNumber, COUNT(DISTINCT v.Chapter) AS Chapters, MAX(v.Chapter) AS MaxChapter
            FROM Books b JOIN Verses v ON v.BookId = b.Id
            WHERE b.BibleId = @bibleId
            GROUP BY b.BookNumber
            """, new { bibleId }, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToDictionary(r => (int)r.BookNumber);

        var chapters = perBook.Values.Sum(r => (int)r.Chapters);
        var missing = new List<string>();
        var mismatches = new List<string>();
        foreach (var book in CanonicalBooks.All)
        {
            if (!perBook.TryGetValue(book.Number, out var row)) { missing.Add(book.Usfm); continue; }
            if (row.Chapters != book.Chapters || row.MaxChapter != book.Chapters)
                mismatches.Add($"{book.Usfm}: {row.Chapters} capítulos con versículos, el mayor es el {row.MaxChapter} (el canon tiene {book.Chapters})");
        }
        foreach (var number in perBook.Keys.Where(n => n is < 1 or > CanonicalBooks.Count))
            mismatches.Add($"Libro con número fuera del canon: {number}");

        // Capítulos cuyo primer versículo no es el 1 o que tienen huecos.
        var gaps = (await connection.QueryAsync<(long BookNumber, long Chapter, long Count, long Min, long Max)>(new CommandDefinition(
            """
            SELECT b.BookNumber AS BookNumber, v.Chapter AS Chapter, COUNT(*) AS Count, MIN(v.VerseNumber) AS Min, MAX(v.VerseNumber) AS Max
            FROM Verses v JOIN Books b ON b.Id = v.BookId
            WHERE b.BibleId = @bibleId
            GROUP BY b.BookNumber, v.Chapter
            HAVING MIN(v.VerseNumber) <> 1 OR MAX(v.VerseNumber) <> COUNT(*)
            ORDER BY b.BookNumber, v.Chapter
            """, new { bibleId }, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();
        var gapRefs = gaps.Take(MaxListedReferences).Select(g =>
            $"{BookCode((int)g.BookNumber)} {g.Chapter}: {g.Count} versículos entre el {g.Min} y el {g.Max}").ToList();

        var difference = (int)verses - StandardVerseTotal;
        var warnings = new List<string>();
        if (books != CanonicalBooks.Count) warnings.Add($"Hay {books} libros con versículos; el canon tiene {CanonicalBooks.Count}.");
        if (chapters != CanonicalBooks.TotalChapters) warnings.Add($"Hay {chapters} capítulos; el canon tiene {CanonicalBooks.TotalChapters}.");
        if (empty > 0) warnings.Add($"{empty} versículos vacíos.");
        if (mismatches.Count > 0) warnings.Add($"{mismatches.Count} libros con un número de capítulos distinto del canon.");
        if (gaps.Count > 0) warnings.Add($"{gaps.Count} capítulos con huecos o que no empiezan en el versículo 1.");
        if (difference != 0)
            warnings.Add($"El total de versículos ({verses.ToString(CultureInfo.InvariantCulture)}) difiere del habitual del canon protestante ({StandardVerseTotal.ToString(CultureInfo.InvariantCulture)}) en {difference:+#;-#}. " +
                         "Puede ser una diferencia de numeración (versículos unidos o partidos) o una Biblia incompleta.");

        return new ValidationReport((int)books, chapters, (int)verses, (int)empty, missing, mismatches, gaps.Count, gapRefs, difference, warnings);
    }

    private static string BookCode(int number) => CanonicalBooks.Find(number)?.Usfm ?? $"#{number}";
}
