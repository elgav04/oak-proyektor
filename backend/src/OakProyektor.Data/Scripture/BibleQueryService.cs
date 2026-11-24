using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;
using OakProyektor.Core.Scripture;

namespace OakProyektor.Data.Scripture;

public interface IBibleQueryService
{
    Task<IReadOnlyList<BibleInfo>> ListBiblesAsync(CancellationToken cancellationToken);

    /// <summary>Resuelve una referencia escrita («Jn 3:16», «Sal 23», «Mt 5:3,5,7») en una Biblia (nula = la predeterminada). Con <paramref name="recordHistory"/> la anota en el historial.</summary>
    Task<BiblePassage> LookupAsync(string reference, string? bible, bool recordHistory, CancellationToken cancellationToken);

    Task<BiblePassage> GetChapterAsync(string? bible, int bookNumber, int chapter, CancellationToken cancellationToken);

    /// <summary>Búsqueda de texto libre (FTS5, sin tildes). Palabras sueltas = todas deben aparecer; entre comillas = frase exacta.</summary>
    Task<BibleSearchResult> SearchAsync(string query, string? bible, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<CitationEntry>> GetHistoryAsync(int limit, CancellationToken cancellationToken);

    Task SetAttributionAsync(string bible, string attribution, CancellationToken cancellationToken);
}

/// <summary>Consultas de Biblias. Toda conexión sale de la fábrica y espera a la BD (<c>OpenWhenReadyAsync</c>).</summary>
public sealed partial class BibleQueryService(ISqliteConnectionFactory connections, IDatabaseReadiness readiness, BibleQueryOptions? options = null, ICitationRecorder? recorder = null) : IBibleQueryService
{
    public const int DefaultSearchLimit = 50;
    public const int MaxSearchLimit = 200;
    public const int MaxAttributionLength = 4000;
    /// <summary>Máximo de versículos por consulta de pasaje (un capítulo entero ≤ 176). «Sal 1-150» (≈ 2 500) se rechaza con <c>bible.rangeTooLarge</c> en vez de mandar un JSON enorme al panel (H4).</summary>
    public const int MaxPassageVerses = 200;
    /// <summary>Texto máximo que se procesa de una búsqueda y términos máximos de la consulta FTS (un pegado enorme no debe armar una consulta gigante; H13).</summary>
    public const int MaxQueryChars = 1000;
    public const int MaxQueryTerms = 32;

    private readonly BibleQueryOptions _options = options ?? BibleQueryOptions.Default;

    public async Task<IReadOnlyList<BibleInfo>> ListBiblesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenWhenReadyAsync(readiness, cancellationToken).ConfigureAwait(false);
        // Lectura con ADO a mano: con Dapper, una consulta de columnas calculadas sin filas (ninguna Biblia instalada) falla al construir el mapeo.
        var list = new List<BibleInfo>();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT bi.Id, bi.Abbreviation, bi.Name, bi.Language, bi.Attribution, bi.NumberingProfile,
                   (SELECT COUNT(*) FROM Books b WHERE b.BibleId = bi.Id),
                   (SELECT COUNT(*) FROM Verses v JOIN Books b ON b.Id = v.BookId WHERE b.BibleId = bi.Id)
            FROM Bibles bi ORDER BY bi.Id
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            list.Add(new BibleInfo(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5), (int)reader.GetInt64(6), (int)reader.GetInt64(7)));
        return list;
    }

    public async Task<BiblePassage> LookupAsync(string reference, string? bible, bool recordHistory, CancellationToken cancellationToken)
    {
        var parsed = ReferenceParser.Parse(reference);
        if (parsed.Reference is not { } parsedReference)
            throw new BibleException(BibleErrorCodes.InvalidReference, parsed.Error!.Message);

        await using var connection = await connections.OpenWhenReadyAsync(readiness, cancellationToken).ConfigureAwait(false);
        var installed = await ResolveBibleAsync(connection, bible, cancellationToken).ConfigureAwait(false);
        var book = CanonicalBooks.Get(parsedReference.BookNumber);

        var bookId = await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
            "SELECT Id FROM Books WHERE BibleId = @bibleId AND BookNumber = @number", new { bibleId = installed.Id, number = book.Number }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        var verses = new List<BibleVerse>();
        if (bookId is not null)
        {
            foreach (var segment in parsedReference.Segments)
            {
                // Se pide como mucho UNO más que lo que queda de cupo: basta para saber que se pasó, sin leer miles de filas.
                verses.AddRange(await ReadSegmentAsync(connection, bookId.Value, book, segment, MaxPassageVerses - verses.Count + 1, cancellationToken).ConfigureAwait(false));
                if (verses.Count > MaxPassageVerses)
                    throw new BibleException(BibleErrorCodes.RangeTooLarge, $"Rango demasiado grande: «{parsedReference.Format()}» tiene más de {MaxPassageVerses} versículos. Acótalo (por ejemplo, un capítulo o unos pocos versículos).");
            }
        }
        var label = parsedReference.Format();
        if (verses.Count == 0)
            throw new BibleException(BibleErrorCodes.PassageNotFound, $"{label} no existe en {installed.Abbreviation}.");

        if (recordHistory) await RecordHistoryAsync(connection, installed.Abbreviation, parsedReference, label, cancellationToken).ConfigureAwait(false);   // sin anotador: en línea (consola, pruebas); con él: en segundo plano (H5)
        return new BiblePassage(installed.Abbreviation, installed.Attribution, label, verses);
    }

    public async Task<BiblePassage> GetChapterAsync(string? bible, int bookNumber, int chapter, CancellationToken cancellationToken)
    {
        var book = CanonicalBooks.Find(bookNumber) ?? throw new BibleException(BibleErrorCodes.InvalidRequest, "El número de libro debe estar entre 1 y 66.");
        if (chapter < 1 || chapter > book.Chapters)
            throw new BibleException(BibleErrorCodes.InvalidReference, $"{book.Name} tiene {book.Chapters} capítulo{(book.Chapters == 1 ? "" : "s")}; no existe el {chapter}.");

        await using var connection = await connections.OpenWhenReadyAsync(readiness, cancellationToken).ConfigureAwait(false);
        var installed = await ResolveBibleAsync(connection, bible, cancellationToken).ConfigureAwait(false);
        var bookId = await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
            "SELECT Id FROM Books WHERE BibleId = @bibleId AND BookNumber = @number", new { bibleId = installed.Id, number = bookNumber }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        var verses = bookId is null ? [] : await ReadSegmentAsync(connection, bookId.Value, book, new ReferenceSegment(chapter, null, chapter, null), MaxPassageVerses + 1, cancellationToken).ConfigureAwait(false);
        var label = new ParsedReference(bookNumber, [new ReferenceSegment(chapter, null, chapter, null)]).Format();
        if (verses.Count == 0) throw new BibleException(BibleErrorCodes.PassageNotFound, $"{label} no existe en {installed.Abbreviation}.");
        return new BiblePassage(installed.Abbreviation, installed.Attribution, label, verses);
    }

    public async Task<BibleSearchResult> SearchAsync(string query, string? bible, int limit, CancellationToken cancellationToken)
    {
        var match = BuildMatch(query);
        if (match is null) throw new BibleException(BibleErrorCodes.EmptyQuery, "Escribe al menos una palabra para buscar.");
        limit = limit <= 0 ? DefaultSearchLimit : Math.Min(limit, MaxSearchLimit);

        await using var connection = await connections.OpenWhenReadyAsync(readiness, cancellationToken).ConfigureAwait(false);
        string? abbreviation = null;
        if (bible is not null) abbreviation = (await ResolveBibleAsync(connection, bible, cancellationToken).ConfigureAwait(false)).Abbreviation;

        // El FTS devuelve el rowid del versículo; snippet() arma el fragmento con las coincidencias entre « y ». ADO a mano: con Dapper, snippet() sin filas
        // se declara como blob y el mapeo a un record falla.
        var hits = new List<BibleSearchHit>();
        var truncated = false;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT bi.Abbreviation, b.BookNumber, v.Chapter, v.VerseNumber, snippet(VersesFts, 0, '«', '»', '…', 16)
                FROM VersesFts
                JOIN Verses v ON v.Id = VersesFts.rowid
                JOIN Books b ON b.Id = v.BookId
                JOIN Bibles bi ON bi.Id = b.BibleId
                WHERE VersesFts MATCH $match AND ($abbreviation IS NULL OR bi.Abbreviation = $abbreviation)
                ORDER BY bm25(VersesFts), bi.Id, b.BookNumber, v.Chapter, v.VerseNumber
                LIMIT $limit
                """;
            command.Parameters.AddWithValue("$match", match);
            command.Parameters.AddWithValue("$abbreviation", (object?)abbreviation ?? DBNull.Value);
            command.Parameters.AddWithValue("$limit", limit + 1);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (hits.Count == limit) { truncated = true; break; }
                var book = reader.GetInt32(1);
                hits.Add(new BibleSearchHit(reader.GetString(0), book, CanonicalBooks.Find(book)?.Name ?? "?", reader.GetInt32(2), reader.GetInt32(3), reader.GetString(4)));
            }
        }
        return new BibleSearchResult(query, truncated, hits);
    }

    public async Task<IReadOnlyList<CitationEntry>> GetHistoryAsync(int limit, CancellationToken cancellationToken)
    {
        var cap = Math.Clamp(_options.HistoryLimit(), 0, BibleQueryOptions.MaxHistoryLimit);
        limit = limit <= 0 ? cap : Math.Min(limit, cap);
        if (limit == 0) return [];

        await using var connection = await connections.OpenWhenReadyAsync(readiness, cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<HistoryRow>(new CommandDefinition(
            "SELECT Id, BibleAbbreviation, BookNumber, Chapter, VerseStart, VerseEnd, Label, CitedAt FROM CitationHistory ORDER BY CitedAt DESC, Id DESC LIMIT @limit",
            new { limit }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return [.. rows.Select(r => new CitationEntry(r.Id, r.BibleAbbreviation, (int)r.BookNumber, CanonicalBooks.Find((int)r.BookNumber)?.Name ?? "?",
            (int)r.Chapter, r.VerseStart is { } s ? (int)s : null, r.VerseEnd is { } e ? (int)e : null, r.Label, r.CitedAt))];
    }

    public async Task SetAttributionAsync(string bible, string attribution, CancellationToken cancellationToken)
    {
        if (attribution.Length > MaxAttributionLength)
            throw new BibleException(BibleErrorCodes.InvalidRequest, $"El aviso de derechos admite {MaxAttributionLength} caracteres como máximo.");
        await using var connection = await connections.OpenWhenReadyAsync(readiness, cancellationToken).ConfigureAwait(false);
        var rows = await connection.ExecuteAsync(new CommandDefinition("UPDATE Bibles SET Attribution = @attribution WHERE Abbreviation = @bible",
            new { attribution, bible }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (rows == 0) throw new BibleException(BibleErrorCodes.NotFound, $"La Biblia «{bible}» no está instalada.");
    }

    // ───────────────────────────── internos ─────────────────────────────

    private async Task<BibleLite> ResolveBibleAsync(SqliteConnection connection, string? abbreviation, CancellationToken ct)
    {
        var wanted = abbreviation ?? _options.DefaultBible();
        if (wanted is not null)
        {
            var found = (await connection.QueryAsync<BibleLite>(new CommandDefinition(
                "SELECT Id, Abbreviation, Name, Language, Attribution, NumberingProfile FROM Bibles WHERE Abbreviation = @wanted", new { wanted }, cancellationToken: ct)).ConfigureAwait(false)).FirstOrDefault();
            if (found is not null) return found;
            if (abbreviation is not null) throw new BibleException(BibleErrorCodes.NotFound, $"La Biblia «{abbreviation}» no está instalada.");
            // La predeterminada de los ajustes ya no está instalada: se cae a la primera instalada en vez de fallar.
        }
        var first = (await connection.QueryAsync<BibleLite>(new CommandDefinition(
            "SELECT Id, Abbreviation, Name, Language, Attribution, NumberingProfile FROM Bibles ORDER BY Id LIMIT 1", cancellationToken: ct)).ConfigureAwait(false)).FirstOrDefault();
        return first ?? throw new BibleException(BibleErrorCodes.NotFound, "No hay ninguna Biblia instalada.");
    }

    private static async Task<IReadOnlyList<BibleVerse>> ReadSegmentAsync(SqliteConnection connection, long bookId, CanonicalBook book, ReferenceSegment s, int maxRows, CancellationToken ct)
    {
        string where;
        object parameters;
        if (s.IsWholeChapters)
        {
            where = "Chapter BETWEEN @c1 AND @c2";
            parameters = new { bookId, c1 = s.StartChapter, c2 = s.EndChapter };
        }
        else if (s.StartChapter == s.EndChapter)
        {
            where = "Chapter = @c1 AND VerseNumber BETWEEN @v1 AND @v2";
            parameters = new { bookId, c1 = s.StartChapter, v1 = s.StartVerse, v2 = s.EndVerse };
        }
        else
        {
            where = "((Chapter = @c1 AND VerseNumber >= @v1) OR (Chapter > @c1 AND Chapter < @c2) OR (Chapter = @c2 AND VerseNumber <= @v2))";
            parameters = new { bookId, c1 = s.StartChapter, c2 = s.EndChapter, v1 = s.StartVerse, v2 = s.EndVerse };
        }
        var rows = await connection.QueryAsync<VerseRow>(new CommandDefinition(
            $"SELECT Chapter, VerseNumber, Text FROM Verses WHERE BookId = @bookId AND {where} ORDER BY Chapter, VerseNumber LIMIT {maxRows.ToString(System.Globalization.CultureInfo.InvariantCulture)}", parameters, cancellationToken: ct)).ConfigureAwait(false);
        return [.. rows.Select(r => new BibleVerse(book.Number, book.Name, (int)r.Chapter, (int)r.VerseNumber, r.Text))];
    }

    /// <summary>
    /// Anota la cita con TODOS sus tramos y recorta el historial al límite configurado (con límite 0 no se guarda nada). Con un <see cref="ICitationRecorder"/> en segundo plano
    /// solo se encola (H5: el carril en vivo no espera al bloqueo de escritura de SQLite); sin él se escribe aquí, por la conexión de la consulta.
    /// </summary>
    private async Task RecordHistoryAsync(SqliteConnection connection, string bible, ParsedReference reference, string label, CancellationToken ct)
    {
        var limit = Math.Clamp(_options.HistoryLimit(), 0, BibleQueryOptions.MaxHistoryLimit);
        if (limit == 0) return;
        var citation = new CitationToRecord(bible, reference, label, limit);
        if (recorder is not null) { recorder.Record(citation); return; }
        await CitationHistoryStore.WriteAsync(connection, citation, null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Expresión FTS5 segura a partir de lo que escribió el usuario: solo letras, dígitos y marcas combinantes (nunca operadores ni comillas sueltas). Las palabras
    /// sueltas se piden todas (AND); lo que va entre comillas dobles es una frase. Devuelve null si no queda ninguna palabra.
    /// La entrada se normaliza a NFC (como el texto guardado: «e» + acento combinante = «é», que no parte la palabra), se procesan como mucho
    /// <see cref="MaxQueryChars"/> caracteres y se arman como mucho <see cref="MaxQueryTerms"/> términos (H13).
    /// </summary>
    public static string? BuildMatch(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        if (query.Length > MaxQueryChars) query = query[..MaxQueryChars];
        try { query = query.Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { /* secuencia Unicode inválida (sustituto suelto): se busca tal cual */ }
        var terms = new List<string>();
        var rest = query;
        foreach (Match phrase in QuotedPhrase().Matches(query))
        {
            var words = Words().Matches(phrase.Groups[1].Value).Select(m => m.Value).ToList();
            if (words.Count > 0) terms.Add("\"" + string.Join(' ', words) + "\"");
            rest = rest.Replace(phrase.Value, " ", StringComparison.Ordinal);
        }
        terms.AddRange(Words().Matches(rest).Select(m => "\"" + m.Value + "\""));
        if (terms.Count > MaxQueryTerms) terms.RemoveRange(MaxQueryTerms, terms.Count - MaxQueryTerms);
        return terms.Count == 0 ? null : string.Join(' ', terms);
    }

    [GeneratedRegex("\"([^\"]*)\"")]
    private static partial Regex QuotedPhrase();

    [GeneratedRegex(@"[\p{L}\p{N}\p{M}]+")]
    private static partial Regex Words();

    // Dapper devuelve INTEGER como Int64 (ver CLAUDE.md): los DTO de lectura usan long.
    private sealed record BibleLite(long Id, string Abbreviation, string Name, string Language, string Attribution, string? NumberingProfile);
    private sealed record VerseRow(long Chapter, long VerseNumber, string Text);
    private sealed record HistoryRow(long Id, string BibleAbbreviation, long BookNumber, long Chapter, long? VerseStart, long? VerseEnd, string Label, string CitedAt);
}
