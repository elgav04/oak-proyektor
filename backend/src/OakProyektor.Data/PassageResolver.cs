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

    /// <summary>
    /// Resuelve todos los pasajes de un servicio (en el orden del servicio) con UNA sola consulta: cada pasaje se une con su Biblia preferida y con la Biblia por
    /// defecto (por abreviatura, tal cual) y con el libro de cada una (por número canónico), y cuenta los versículos del rango con el índice único de `Verses`.
    /// Da EXACTAMENTE el mismo resultado que <see cref="ResolveAsync"/> pasaje a pasaje (un test lo compara en todas las combinaciones); antes eran hasta 6 consultas
    /// por pasaje, todas en el carril en vivo. Los elementos que no son pasajes no aparecen. <paramref name="onQuery"/> cuenta las consultas (pruebas y diagnóstico).
    /// </summary>
    public static async Task<IReadOnlyList<(long ItemId, PassageResolution Resolution)>> ResolvePlaylistAsync(
        SqliteConnection connection, long playlistId, string? defaultBibleAbbreviation, CancellationToken cancellationToken, Action? onQuery = null)
    {
        // La por defecto solo cuenta si existe y es OTRA abreviatura (comparación ordinal, como en ResolveAsync).
        var fallback = defaultBibleAbbreviation is { Length: > 0 } d ? d : null;
        onQuery?.Invoke();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT pi.Id, pi.BibleAbbreviation,
                   pb.Id, pk.Id,
                   CASE WHEN pk.Id IS NULL THEN 0 ELSE (SELECT COUNT(*) FROM Verses v WHERE v.BookId = pk.Id AND v.Chapter = pi.Chapter
                                                        AND v.VerseNumber BETWEEN pi.VerseStart AND COALESCE(pi.VerseEnd, pi.VerseStart)) END,
                   db.Id, dk.Id,
                   CASE WHEN dk.Id IS NULL THEN 0 ELSE (SELECT COUNT(*) FROM Verses v WHERE v.BookId = dk.Id AND v.Chapter = pi.Chapter
                                                        AND v.VerseNumber BETWEEN pi.VerseStart AND COALESCE(pi.VerseEnd, pi.VerseStart)) END
            FROM PlaylistItems pi
            LEFT JOIN Bibles pb ON pb.Abbreviation = pi.BibleAbbreviation
            LEFT JOIN Books  pk ON pk.BibleId = pb.Id AND pk.BookNumber = pi.BookNumber
            LEFT JOIN Bibles db ON @fallback IS NOT NULL AND db.Abbreviation = @fallback
            LEFT JOIN Books  dk ON dk.BibleId = db.Id AND dk.BookNumber = pi.BookNumber
            WHERE pi.PlaylistId = @playlistId AND pi.ItemType = 'PASSAGE'
            ORDER BY pi.SequenceIndex, pi.Id
            """;
        command.Parameters.AddWithValue("@playlistId", playlistId);
        command.Parameters.AddWithValue("@fallback", (object?)fallback ?? DBNull.Value);

        // Con ADO a mano: columnas calculadas (COUNT) en una consulta que puede no devolver filas (ver CLAUDE.md: Dapper las toma por blob).
        var results = new List<(long, PassageResolution)>();
        await using var r = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var abbreviation = r.GetString(1);
            var preferred = Build(abbreviation, r.IsDBNull(2) ? null : r.GetInt64(2), r.IsDBNull(3) ? null : r.GetInt64(3), (int)r.GetInt64(4));
            if (!preferred.IsResolved && fallback is not null && fallback != abbreviation)
            {
                var alternative = Build(fallback, r.IsDBNull(5) ? null : r.GetInt64(5), r.IsDBNull(6) ? null : r.GetInt64(6), (int)r.GetInt64(7));
                if (alternative.IsResolved) preferred = preferred with { Offered = alternative };
            }
            results.Add((r.GetInt64(0), preferred));
        }
        return results;
    }

    /// <summary>Las mismas reglas que <see cref="LookupAsync"/>, a partir de lo leído en la consulta única.</summary>
    private static PassageResolution Build(string abbreviation, long? bibleId, long? bookId, int found)
    {
        if (bibleId is null) return new PassageResolution(PassageStatus.BibleNotInstalled, abbreviation, null, null, 0);
        if (bookId is null) return new PassageResolution(PassageStatus.PassageNotFound, abbreviation, bibleId, null, 0);
        return new PassageResolution(found > 0 ? PassageStatus.Resolved : PassageStatus.PassageNotFound, abbreviation, bibleId, bookId, found);
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

}
