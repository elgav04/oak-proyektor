using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using OakProyektor.Core.Scripture;

namespace OakProyektor.Data.Scripture;

/// <summary>
/// Importador de Biblias. REGLAS (CLAUDE.md):
/// <list type="bullet">
/// <item>Actualiza EN EL SITIO y conserva el <c>BibleId</c>: upsert de <c>Bibles</c> (por abreviatura), <c>Books</c> (por Biblia y número) y <c>Verses</c>
/// (por libro, capítulo y versículo) dentro de UNA transacción. Nunca borra la Biblia para importarla de nuevo; nunca usa REPLACE.</item>
/// <item>Los versículos que la fuente YA NO trae (p. ej. al cambiar el perfil de numeración) se ELIMINAN con un DELETE explícito en la misma transacción,
/// y se avisa de los elementos de servicios o del historial que apuntaban a ellos. Una reimportación que eliminaría más del 1 % se rechaza salvo
/// <see cref="BibleImportRequest.AllowLargeDeletion"/> (protege de importar un archivo equivocado o truncado).</item>
/// <item>Ni los informes, ni los logs, ni los mensajes de error, ni el progreso llevan texto bíblico: solo conteos y referencias.</item>
/// </list>
/// </summary>
public sealed class BibleImporter(ISqliteConnectionFactory connections, IDatabaseReadiness readiness, ILogger<BibleImporter> logger)
{
    private const int ProgressStep = 500;

    public async Task<ImportReport> ImportAsync(BibleImportRequest request, IProgress<ImportProgress>? progress, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var (format, profile) = Validate(request);

        // 1) Leer y preparar en memoria (sin tocar la BD): si algo falla aquí, no se ha cambiado nada.
        progress?.Report(new ImportProgress("reading", 0, 1));
        BibleSourceData source;
        try { source = await format.ReadAsync(request.Path, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // Ningún mensaje de estas excepciones lleva el contenido del archivo (solo posiciones y rutas).
            logger.LogError(ex, "No se pudo leer la fuente {Format}", format.Id);
            throw new BibleException(BibleErrorCodes.SourceInvalid, $"No se pudo leer el archivo como «{format.Id}»: {ex.Message}", ex);
        }
        progress?.Report(new ImportProgress("reading", 1, 1));

        var warnings = new List<string>(source.Notes);
        var cleaned = 0;
        var prepared = new List<SourceVerse>(source.Verses.Count);
        var index = 0;
        foreach (var verse in source.Verses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (verse.Chapter < 1 || verse.VerseNumber < 1 || CanonicalBooks.Find(verse.BookNumber) is null)
            {
                warnings.Add($"Versículo con numeración inválida descartado: libro {verse.BookNumber}, capítulo {verse.Chapter}, versículo {verse.VerseNumber}.");
                continue;
            }
            var text = VerseCleaner.Clean(verse.Text);
            if (!string.Equals(text, verse.Text, StringComparison.Ordinal)) cleaned++;
            prepared.Add(verse with { Text = text });
            if (++index % 2000 == 0) progress?.Report(new ImportProgress("cleaning", index, source.Verses.Count));
        }
        progress?.Report(new ImportProgress("cleaning", source.Verses.Count, source.Verses.Count));

        var numbering = NumberingProfileApplier.Apply(prepared, profile);
        var incoming = numbering.Verses;
        foreach (var skipped in numbering.SkippedMerges) warnings.Add($"Unión de numeración no aplicada (faltan versículos en la fuente): {skipped}.");
        if (incoming.Count == 0) throw new BibleException(BibleErrorCodes.SourceInvalid, "La fuente no contiene ningún versículo.");

        // 2) Escribir TODO en una transacción.
        await using var connection = await connections.OpenWhenReadyAsync(readiness, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var storedProfile = profile.Id == NumberingProfiles.NoneId ? null : profile.Id;
        var existing = (await connection.QueryAsync<ExistingBible>(new CommandDefinition(
            "SELECT Id, NumberingProfile FROM Bibles WHERE Abbreviation = @abbreviation", new { abbreviation = request.Abbreviation }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)).FirstOrDefault();

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO Bibles (Abbreviation, Name, Language, NumberingProfile) VALUES (@abbreviation, @name, @language, @profile)
            ON CONFLICT(Abbreviation) DO UPDATE SET Name = excluded.Name, Language = excluded.Language, NumberingProfile = excluded.NumberingProfile
            """,
            new { abbreviation = request.Abbreviation, name = request.Name, language = request.Language, profile = storedProfile }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        var bibleId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT Id FROM Bibles WHERE Abbreviation = @abbreviation", new { abbreviation = request.Abbreviation }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        var created = existing is null;
        if (request.Attribution is not null)
            await connection.ExecuteAsync(new CommandDefinition("UPDATE Bibles SET Attribution = @attribution WHERE Id = @bibleId",
                new { attribution = request.Attribution, bibleId }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (existing is not null && !string.Equals(existing.NumberingProfile ?? NumberingProfiles.NoneId, profile.Id, StringComparison.Ordinal))
            warnings.Add($"El perfil de numeración cambió de «{existing.NumberingProfile ?? NumberingProfiles.NoneId}» a «{profile.Id}»: se eliminan los versículos que dejan de existir.");

        // Libros: upsert por (BibleId, BookNumber) con el nombre y testamento canónicos.
        var booksInSource = incoming.Select(v => v.BookNumber).Distinct().Order().ToList();
        foreach (var number in booksInSource)
        {
            var book = CanonicalBooks.Get(number);
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO Books (BibleId, BookNumber, Name, Testament) VALUES (@bibleId, @number, @name, @testament)
                ON CONFLICT(BibleId, BookNumber) DO UPDATE SET Name = excluded.Name, Testament = excluded.Testament
                WHERE Name IS NOT excluded.Name OR Testament IS NOT excluded.Testament
                """,
                new { bibleId, number, name = book.Name, testament = book.Testament }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        var bookIds = (await connection.QueryAsync<(long Id, long BookNumber)>(new CommandDefinition(
            "SELECT Id, BookNumber FROM Books WHERE BibleId = @bibleId", new { bibleId }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false))
            .ToDictionary(r => (int)r.BookNumber, r => r.Id);

        // Qué hay ya en la BD, para insertar solo lo nuevo, actualizar solo lo que cambió y eliminar lo que ya no existe.
        var current = new Dictionary<(int Book, int Chapter, int Verse), string>();
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT b.BookNumber, v.Chapter, v.VerseNumber, v.Text FROM Verses v JOIN Books b ON b.Id = v.BookId WHERE b.BibleId = $bibleId";
            read.Parameters.AddWithValue("$bibleId", bibleId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                current[(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2))] = reader.GetString(3);
        }

        var incomingKeys = new HashSet<(int, int, int)>(incoming.Count);
        var toWrite = new List<(SourceVerse Verse, bool IsNew)>();
        var unchanged = 0;
        foreach (var verse in incoming)
        {
            var key = (verse.BookNumber, verse.Chapter, verse.VerseNumber);
            incomingKeys.Add(key);
            if (!current.TryGetValue(key, out var old)) toWrite.Add((verse, true));
            else if (!string.Equals(old, verse.Text, StringComparison.Ordinal)) toWrite.Add((verse, false));
            else unchanged++;
        }
        var toDelete = current.Keys.Where(k => !incomingKeys.Contains(k)).OrderBy(k => k).ToList();

        // Protección: una reimportación que elimina muchos versículos casi siempre es un archivo equivocado o truncado.
        var allowed = Math.Max(10, current.Count / 100);
        if (toDelete.Count > allowed && !request.AllowLargeDeletion)
            throw new BibleException(BibleErrorCodes.TooManyDeletions,
                $"Esta importación eliminaría {toDelete.Count} versículos de {request.Abbreviation} (más del 1 %). ¿Es el archivo correcto? Si es intencionado, permite la eliminación masiva.");

        // Elementos que apuntaban a versículos que se eliminan: se averiguan ANTES de borrar.
        var affected = toDelete.Count == 0
            ? []
            : await FindAffectedItemsAsync(connection, transaction, request.Abbreviation, toDelete, cancellationToken).ConfigureAwait(false);

        // Escritura: comandos preparados y reutilizados (31 000 filas con un comando por fila serían mucho más lentas).
        var done = 0;
        var total = toWrite.Count + toDelete.Count;
        await using (var upsert = connection.CreateCommand())
        {
            upsert.Transaction = transaction;
            upsert.CommandText =
                """
                INSERT INTO Verses (BookId, Chapter, VerseNumber, Text) VALUES ($book, $chapter, $verse, $text)
                ON CONFLICT(BookId, Chapter, VerseNumber) DO UPDATE SET Text = excluded.Text WHERE Text IS NOT excluded.Text
                """;
            var pBook = upsert.Parameters.Add("$book", SqliteType.Integer);
            var pChapter = upsert.Parameters.Add("$chapter", SqliteType.Integer);
            var pVerse = upsert.Parameters.Add("$verse", SqliteType.Integer);
            var pText = upsert.Parameters.Add("$text", SqliteType.Text);
            await upsert.PrepareAsync(cancellationToken).ConfigureAwait(false);
            foreach (var (verse, _) in toWrite)
            {
                pBook.Value = bookIds[verse.BookNumber];
                pChapter.Value = verse.Chapter;
                pVerse.Value = verse.VerseNumber;
                pText.Value = verse.Text;
                await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                if (++done % ProgressStep == 0) progress?.Report(new ImportProgress("saving", done, total));
            }
        }

        var booksDeleted = 0;
        if (toDelete.Count > 0)
        {
            await using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM Verses WHERE BookId = $book AND Chapter = $chapter AND VerseNumber = $verse";   // explícito, nunca por reemplazo
            var pBook = delete.Parameters.Add("$book", SqliteType.Integer);
            var pChapter = delete.Parameters.Add("$chapter", SqliteType.Integer);
            var pVerse = delete.Parameters.Add("$verse", SqliteType.Integer);
            await delete.PrepareAsync(cancellationToken).ConfigureAwait(false);
            foreach (var (book, chapter, verse) in toDelete)
            {
                pBook.Value = bookIds[book];
                pChapter.Value = chapter;
                pVerse.Value = verse;
                await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                if (++done % ProgressStep == 0) progress?.Report(new ImportProgress("saving", done, total));
            }

            // Libros que la fuente ya no trae (sin versículos tras lo anterior): se eliminan explícitamente.
            foreach (var (number, id) in bookIds.Where(b => !booksInSource.Contains(b.Key)).ToList())
            {
                booksDeleted += await connection.ExecuteAsync(new CommandDefinition("DELETE FROM Books WHERE Id = @id", new { id }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
                bookIds.Remove(number);
            }
        }
        progress?.Report(new ImportProgress("saving", total, total));

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        // 3) Validar (avisa, no falla) y comprobar el índice de búsqueda.
        progress?.Report(new ImportProgress("validating", 0, 1));
        var (ftsOk, ftsNote) = await CheckFtsAsync(connection, cancellationToken).ConfigureAwait(false);
        var validation = await BibleValidator.ValidateAsync(connection, bibleId, cancellationToken).ConfigureAwait(false);
        progress?.Report(new ImportProgress("validating", 1, 1));

        foreach (var item in affected)
            warnings.Add($"Apuntaba a un versículo eliminado: {item.Kind} #{item.Id}{(item.PlaylistId is { } p ? $" (servicio {p})" : "")} → {item.Reference}");
        if (cleaned > 0) warnings.Add($"{cleaned} versículos se limpiaron (espacios y caracteres de control).");

        clock.Stop();
        var report = new ImportReport(
            bibleId, created, format.Id, profile.Id,
            incoming.Count, toWrite.Count(w => w.IsNew), toWrite.Count(w => !w.IsNew), unchanged, toDelete.Count, booksDeleted,
            cleaned, source.JoinedVerses, source.DuplicateChaptersDiscarded,
            numbering.AppliedMerges, numbering.SkippedMerges, affected, validation, ftsOk, ftsNote, warnings, clock.ElapsedMilliseconds);

        logger.LogInformation(
            "Biblia {Abbreviation} importada (id {BibleId}, formato {Format}, perfil {Profile}): {Verses} versículos ({Inserted} nuevos, {Updated} actualizados, {Unchanged} sin cambios, {Deleted} eliminados) en {Ms} ms; FTS {Fts}",
            request.Abbreviation, bibleId, format.Id, profile.Id, report.SourceVerses, report.Inserted, report.Updated, report.Unchanged, report.Deleted, report.ElapsedMs, ftsOk ? "ok" : "con problemas");
        return report;
    }

    // Dapper devuelve INTEGER como Int64 (ver CLAUDE.md).
    private sealed record ExistingBible(long Id, string? NumberingProfile);

    private static (IBibleSourceFormat Format, NumberingProfile Profile) Validate(BibleImportRequest request)
    {
        static BibleException Invalid(string message) => new(BibleErrorCodes.InvalidRequest, message);

        if (string.IsNullOrWhiteSpace(request.Abbreviation) || request.Abbreviation.Trim() != request.Abbreviation || request.Abbreviation.Length > 20)
            throw Invalid("La abreviatura es obligatoria, sin espacios al inicio ni al final, de 20 caracteres como máximo.");
        if (string.IsNullOrWhiteSpace(request.Name)) throw Invalid("El nombre de la Biblia es obligatorio.");
        if (string.IsNullOrWhiteSpace(request.Language)) throw Invalid("El idioma de la Biblia es obligatorio.");
        if (string.IsNullOrWhiteSpace(request.Path) || !File.Exists(request.Path))
            throw new BibleException(BibleErrorCodes.FileMissing, "No existe el archivo de la Biblia indicado.");

        var format = request.FormatId is null ? BibleSourceFormats.Default : BibleSourceFormats.Find(request.FormatId)
            ?? throw Invalid($"Formato desconocido «{request.FormatId}». Formatos: {string.Join(", ", BibleSourceFormats.All.Where(f => f.CanImport).Select(f => f.Id))}.");
        if (!format.CanImport) throw Invalid($"El formato «{format.Id}» solo sirve para verificar, no para importar.");
        if (!NumberingProfiles.TryGet(request.NumberingProfileId, out var profile))
            throw Invalid($"Perfil de numeración desconocido «{request.NumberingProfileId}». Perfiles: {string.Join(", ", NumberingProfiles.All.Select(p => p.Id))}.");
        return (format, profile);
    }

    /// <summary>Elementos de servicios (pasajes) y del historial de esa Biblia cuyo rango incluye algún versículo eliminado.</summary>
    private static async Task<IReadOnlyList<AffectedItem>> FindAffectedItemsAsync(
        SqliteConnection connection, SqliteTransaction transaction, string abbreviation, IReadOnlyList<(int Book, int Chapter, int Verse)> deleted, CancellationToken ct)
    {
        var byChapter = deleted.GroupBy(d => (d.Book, d.Chapter)).ToDictionary(g => g.Key, g => g.Select(d => d.Verse).ToList());
        bool Hits(long book, long chapter, long? first, long? last) =>
            byChapter.TryGetValue(((int)book, (int)chapter), out var verses) && (first is null || verses.Any(v => v >= first && v <= (last ?? first)));
        static string Ref(long book, long chapter, long? first, long? last) =>
            $"{CanonicalBooks.Find((int)book)?.Usfm ?? "#" + book} {chapter.ToString(CultureInfo.InvariantCulture)}" +
            (first is null ? "" : ":" + first.Value.ToString(CultureInfo.InvariantCulture) + (last is not null && last != first ? "-" + last.Value.ToString(CultureInfo.InvariantCulture) : ""));

        var result = new List<AffectedItem>();
        var items = await connection.QueryAsync<(long Id, long PlaylistId, long Book, long Chapter, long First, long? Last)>(new CommandDefinition(
            "SELECT Id, PlaylistId, BookNumber, Chapter, VerseStart, VerseEnd FROM PlaylistItems WHERE ItemType = 'PASSAGE' AND BibleAbbreviation = @abbreviation",
            new { abbreviation }, transaction, cancellationToken: ct)).ConfigureAwait(false);
        foreach (var i in items)
            if (Hits(i.Book, i.Chapter, i.First, i.Last)) result.Add(new AffectedItem("playlist", i.Id, i.PlaylistId, Ref(i.Book, i.Chapter, i.First, i.Last)));

        var history = await connection.QueryAsync<(long Id, long Book, long Chapter, long? First, long? Last)>(new CommandDefinition(
            "SELECT Id, BookNumber, Chapter, VerseStart, VerseEnd FROM CitationHistory WHERE BibleAbbreviation = @abbreviation",
            new { abbreviation }, transaction, cancellationToken: ct)).ConfigureAwait(false);
        foreach (var h in history)
            if (Hits(h.Book, h.Chapter, h.First, h.Last)) result.Add(new AffectedItem("history", h.Id, null, Ref(h.Book, h.Chapter, h.First, h.Last)));
        return result;
    }

    /// <summary>
    /// <c>integrity-check</c> del FTS (CLAUDE.md). Si falla se intenta reconstruir el índice una vez y se vuelve a comprobar; el resultado se informa (no lanza).
    /// </summary>
    public static async Task<(bool Ok, string? Note)> CheckFtsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await connection.ExecuteAsync(new CommandDefinition("INSERT INTO VersesFts(VersesFts, rank) VALUES('integrity-check', 1)", cancellationToken: cancellationToken)).ConfigureAwait(false);
            return (true, null);
        }
        catch (SqliteException first)
        {
            try
            {
                await connection.ExecuteAsync(new CommandDefinition("INSERT INTO VersesFts(VersesFts) VALUES('rebuild')", cancellationToken: cancellationToken)).ConfigureAwait(false);
                await connection.ExecuteAsync(new CommandDefinition("INSERT INTO VersesFts(VersesFts, rank) VALUES('integrity-check', 1)", cancellationToken: cancellationToken)).ConfigureAwait(false);
                return (true, "El índice de búsqueda estaba desincronizado y se reconstruyó (integrity-check correcto después).");
            }
            catch (SqliteException second)
            {
                return (false, $"integrity-check del índice de búsqueda falló ({first.SqliteErrorCode}) y la reconstrucción también ({second.SqliteErrorCode}).");
            }
        }
    }
}
