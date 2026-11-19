using Dapper;
using Microsoft.Data.Sqlite;
using OakProyektor.Core.Library;

namespace OakProyektor.Data;

/// <summary>
/// Resuelve los pasajes de un servicio al CARGARLO. Un pasaje se guarda por referencia canónica (número de libro 1 a 66, capítulo, versículos y la
/// abreviatura de la Biblia preferida como TEXTO), sin clave foránea: aquí se busca esa Biblia entre las instaladas. Reglas:
/// <list type="bullet">
/// <item>La abreviatura se compara tal cual (<c>Bibles.Abbreviation</c> es única); no se adivina por mayúsculas ni por nombre.</item>
/// <item>Si la Biblia no está instalada, o no tiene el pasaje, el resultado es un aviso (<see cref="PassageResolution.Warning"/>); el elemento del
/// servicio NO se toca ni se marca como perdido. Si la Biblia por defecto sí lo tiene, se OFRECE (<see cref="PassageResolution.Offered"/>), sin aplicarla.</item>
/// <item>Recibe una conexión ya abierta por el llamador: quien abre conexiones para los repositorios usa <c>OpenWhenReadyAsync</c> (ver CLAUDE.md).</item>
/// </list>
/// </summary>
public static class PassageResolver
{
    public static async Task<PassageResolution> ResolveAsync(
        SqliteConnection connection, PassageReference reference, string? defaultBibleAbbreviation, CancellationToken cancellationToken)
    {
        var preferred = await LookupAsync(connection, reference, reference.BibleAbbreviation, cancellationToken).ConfigureAwait(false);
        if (preferred.IsResolved) return preferred;

        // La preferida no sirve: si la Biblia por defecto (otra) tiene el pasaje, se ofrece. Nunca se aplica sola.
        if (defaultBibleAbbreviation is { Length: > 0 } fallback && fallback != reference.BibleAbbreviation)
        {
            var alternative = await LookupAsync(connection, reference, fallback, cancellationToken).ConfigureAwait(false);
            if (alternative.IsResolved) return preferred with { Offered = alternative };
        }
        return preferred;
    }

    /// <summary>Resuelve todos los pasajes de un servicio (en el orden del servicio). Los elementos que no son pasajes no aparecen.</summary>
    public static async Task<IReadOnlyList<(long ItemId, PassageResolution Resolution)>> ResolvePlaylistAsync(
        SqliteConnection connection, long playlistId, string? defaultBibleAbbreviation, CancellationToken cancellationToken)
    {
        var rows = (await connection.QueryAsync<PassageRow>(new CommandDefinition(
            """
            SELECT Id, BookNumber, Chapter, VerseStart, VerseEnd, BibleAbbreviation
            FROM PlaylistItems
            WHERE PlaylistId = @playlistId AND ItemType = 'PASSAGE'
            ORDER BY SequenceIndex, Id
            """,
            new { playlistId }, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        var results = new List<(long, PassageResolution)>(rows.Count);
        foreach (var row in rows)
        {
            var reference = new PassageReference((int)row.BookNumber, (int)row.Chapter, (int)row.VerseStart, row.VerseEnd is { } end ? (int)end : null, row.BibleAbbreviation);
            results.Add((row.Id, await ResolveAsync(connection, reference, defaultBibleAbbreviation, cancellationToken).ConfigureAwait(false)));
        }
        return results;
    }

    private static async Task<PassageResolution> LookupAsync(
        SqliteConnection connection, PassageReference reference, string abbreviation, CancellationToken ct)
    {
        var bibleId = await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
            "SELECT Id FROM Bibles WHERE Abbreviation = @abbreviation", new { abbreviation }, cancellationToken: ct)).ConfigureAwait(false);
        if (bibleId is null) return new PassageResolution(PassageStatus.BibleNotInstalled, abbreviation, null, null, 0);

        var bookId = await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
            "SELECT Id FROM Books WHERE BibleId = @bibleId AND BookNumber = @book",
            new { bibleId, book = reference.BookNumber }, cancellationToken: ct)).ConfigureAwait(false);
        if (bookId is null) return new PassageResolution(PassageStatus.PassageNotFound, abbreviation, bibleId, null, 0);

        var found = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM Verses WHERE BookId = @bookId AND Chapter = @chapter AND VerseNumber BETWEEN @first AND @last",
            new { bookId, chapter = reference.Chapter, first = reference.VerseStart, last = reference.LastVerse }, cancellationToken: ct)).ConfigureAwait(false);

        // Una Biblia puede omitir versículos sueltos (numeraciones distintas): basta con que exista alguno del rango.
        return new PassageResolution(found > 0 ? PassageStatus.Resolved : PassageStatus.PassageNotFound, abbreviation, bibleId, bookId, (int)found);
    }

    // Dapper devuelve INTEGER como Int64 (ver CLAUDE.md): el DTO usa long y se convierte aquí.
    private sealed record PassageRow(long Id, long BookNumber, long Chapter, long VerseStart, long? VerseEnd, string BibleAbbreviation);
}
