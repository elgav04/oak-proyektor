using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using OakProyektor.Core.Scripture;

namespace OakProyektor.Data.Scripture;

/// <summary>
/// Importador de Biblias. REGLAS (CLAUDE.md):
/// <list type="bullet">
/// <item>Actualiza EN EL SITIO y conserva el <c>BibleId</c>: upsert de <c>Bibles</c> (por abreviatura, SIN distinguir mayúsculas), <c>Books</c> (por Biblia y número) y <c>Verses</c>
/// (por libro, capítulo y versículo) dentro de UNA transacción <c>BEGIN IMMEDIATE</c>. Nunca borra la Biblia para importarla de nuevo; nunca usa REPLACE.</item>
/// <item>Los versículos que la fuente YA NO trae (p. ej. al cambiar el perfil de numeración) se ELIMINAN con un DELETE explícito en la misma transacción,
/// y se avisa de los elementos de servicios o del historial que apuntaban a ellos.</item>
/// <item><b>Protecciones de una reimportación</b> (se comprueban ANTES de escribir nada; ver <see cref="BibleErrorCodes"/>): rechaza si eliminaría más del 1 % de los versículos o si
/// desaparecería un libro o capítulo entero (<see cref="BibleImportRequest.AllowLargeDeletion"/>); y rechaza si cambiaría más del 1 % de los textos guardados, si cambia el nombre de la
/// Biblia o si dejaría vacíos textos que no lo están (<see cref="BibleImportRequest.AllowLargeChange"/>): lo que pasa al importar OTRA traducción con la misma abreviatura. Un archivo
/// idéntico da 0 cambios y una corrección pequeña pasa sin indicadores.</item>
/// <item>Antes de una reimportación que vaya a modificar o borrar algo se hace una copia con <c>VACUUM INTO</c> y se conservan las últimas <see cref="BackupsToKeep"/>.</item>
/// <item>Sin <see cref="BibleImportRequest.NumberingProfileId"/> se CONSERVA el perfil guardado de la Biblia.</item>
/// <item>Ni los informes, ni los logs, ni los mensajes de error, ni el progreso llevan texto bíblico: solo conteos y referencias.</item>
/// </list>
/// </summary>
public sealed class BibleImporter(ISqliteConnectionFactory connections, IDatabaseReadiness readiness, ILogger<BibleImporter> logger)
{
    private const int ProgressStep = 500;
    /// <summary>Copias de seguridad de reimportaciones que se conservan (las más recientes).</summary>
    public const int BackupsToKeep = 5;
    public const string BackupPrefix = "oak-proyektor.bible-import.";
    public const int MaxAbbreviationLength = 20;

    public async Task<ImportReport> ImportAsync(BibleImportRequest request, IProgress<ImportProgress>? progress, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var (format, requestedProfile, abbreviation, name, language) = Validate(request);

        // 0) Lo que ya hay guardado de esa Biblia (sin distinguir mayúsculas): su perfil de numeración se conserva si no se indica otro.
        var stored = await ReadStoredBibleAsync(abbreviation, cancellationToken).ConfigureAwait(false);
        if (stored is not null) abbreviation = stored.Abbreviation;   // «rvr1960» y «RVR1960» son la MISMA Biblia: se usa la abreviatura guardada
        var warnings = new List<string>();
        NumberingProfile profile;
        var profileKept = false;
        if (requestedProfile is not null) profile = requestedProfile;
        else if (stored is not null)
        {
            profileKept = true;
            if (NumberingProfiles.TryGet(stored.NumberingProfile, out var keptProfile)) profile = keptProfile;
            else
            {
                profile = NumberingProfiles.None;
                warnings.Add($"El perfil de numeración guardado «{stored.NumberingProfile}» ya no existe: se aplica «none».");
            }
        }
        else profile = NumberingProfiles.None;

        // 1) Leer y preparar en memoria (sin tocar la BD): si algo falla aquí, no se ha cambiado nada.
        var prepared = await PrepareAsync(format, request.Path, progress, cancellationToken).ConfigureAwait(false);
        warnings.AddRange(prepared.Warnings);

        var numbering = NumberingProfileApplier.Apply(prepared.Verses, profile);
        var incoming = numbering.Verses;
        foreach (var skipped in numbering.SkippedMerges) warnings.Add($"Unión de numeración no aplicada (faltan versículos en la fuente): {skipped}.");
        if (incoming.Count == 0) throw new BibleException(BibleErrorCodes.SourceInvalid, "La fuente no contiene ningún versículo.");

        var incomingByKey = new Dictionary<(int Book, int Chapter, int Verse), SourceVerse>(incoming.Count);
        foreach (var verse in incoming) incomingByKey[(verse.BookNumber, verse.Chapter, verse.VerseNumber)] = verse;
        var booksInSource = incoming.Select(v => v.BookNumber).Distinct().Order().ToList();
        var storedProfileId = profile.Id == NumberingProfiles.NoneId ? null : profile.Id;

        // 2) Escribir TODO en una transacción BEGIN IMMEDIATE (toma el bloqueo de escritura al empezar: leer primero y escribir después con una transacción
        //    diferida podría fallar con SQLITE_BUSY_SNAPSHOT si otra conexión escribe en medio, y ese error no se arregla reintentando).
        await using var connection = await connections.OpenWhenReadyAsync(readiness, cancellationToken).ConfigureAwait(false);
        SqliteTransaction? transaction = null;
        Plan plan;
        string? backupPath = null;
        try
        {
            while (true)
            {
                transaction = await BeginImmediateAsync(connection, cancellationToken).ConfigureAwait(false);
                plan = await PlanAsync(connection, transaction, request, abbreviation, name, profile, profileKept ? stored?.NumberingProfile : null, profileKept, incomingByKey, booksInSource, cancellationToken).ConfigureAwait(false);
                if (plan.NeedsBackup && backupPath is null)
                {
                    if (request.BackupDirectory is null)
                    {
                        warnings.Add("No se hizo copia de seguridad previa: no hay carpeta de respaldos configurada.");
                        backupPath = "";   // ya decidido: no insistir
                    }
                    else
                    {
                        // VACUUM INTO no puede ir dentro de una transacción: se suelta, se copia y se vuelve a planificar (lo comprobado se repite con lo que haya ahora).
                        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                        await transaction.DisposeAsync().ConfigureAwait(false);
                        transaction = null;
                        backupPath = await BackupAsync(connection, request.BackupDirectory, abbreviation, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                }
                break;
            }

            var created = plan.Existing is null;
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO Bibles (Abbreviation, Name, Language, NumberingProfile) VALUES (@abbreviation, @name, @language, @profile)
                ON CONFLICT(Abbreviation) DO UPDATE SET Name = excluded.Name, Language = excluded.Language, NumberingProfile = excluded.NumberingProfile
                """,
                new { abbreviation, name, language, profile = storedProfileId }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            var bibleId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT Id FROM Bibles WHERE Abbreviation = @abbreviation", new { abbreviation }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (request.Attribution is not null)
                await connection.ExecuteAsync(new CommandDefinition("UPDATE Bibles SET Attribution = @attribution WHERE Id = @bibleId",
                    new { attribution = request.Attribution, bibleId }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (plan.Existing is not null && !string.Equals(plan.Existing.NumberingProfile ?? NumberingProfiles.NoneId, profile.Id, StringComparison.Ordinal))
                warnings.Add($"El perfil de numeración cambió de «{plan.Existing.NumberingProfile ?? NumberingProfiles.NoneId}» a «{profile.Id}»: se eliminan los versículos que dejan de existir.");
            if (plan.Existing is not null && !string.Equals(plan.Existing.Language, language, StringComparison.Ordinal))
                warnings.Add($"El idioma cambió de «{plan.Existing.Language}» a «{language}».");
            if (profileKept) warnings.Add($"No se indicó perfil de numeración: se conservó el guardado («{profile.Id}»).");

            // Libros: upsert por (BibleId, BookNumber) con el nombre y testamento canónicos.
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

            // Elementos que apuntaban a versículos que se eliminan: se averiguan ANTES de borrar.
            var affected = plan.ToDelete.Count == 0
                ? []
                : await FindAffectedItemsAsync(connection, transaction, abbreviation, plan.ToDelete, cancellationToken).ConfigureAwait(false);

            // Escritura: comandos preparados y reutilizados (31 000 filas con un comando por fila serían mucho más lentas).
            var toWrite = new List<SourceVerse>();
            foreach (var verse in incoming)
            {
                var key = (verse.BookNumber, verse.Chapter, verse.VerseNumber);
                if (!plan.ExistingKeys.Contains(key) || plan.UpdatedKeys.Contains(key)) toWrite.Add(verse);
            }
            var done = 0;
            var total = toWrite.Count + plan.ToDelete.Count;
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
                foreach (var verse in toWrite)
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
            if (plan.ToDelete.Count > 0)
            {
                await using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM Verses WHERE BookId = $book AND Chapter = $chapter AND VerseNumber = $verse";   // explícito, nunca por reemplazo
                var pBook = delete.Parameters.Add("$book", SqliteType.Integer);
                var pChapter = delete.Parameters.Add("$chapter", SqliteType.Integer);
                var pVerse = delete.Parameters.Add("$verse", SqliteType.Integer);
                await delete.PrepareAsync(cancellationToken).ConfigureAwait(false);
                foreach (var (book, chapter, verse) in plan.ToDelete)
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
            if (prepared.Cleaned > 0) warnings.Add($"{prepared.Cleaned} versículos se limpiaron (espacios y caracteres de control).");
            if (!string.IsNullOrEmpty(backupPath)) warnings.Add($"Copia de seguridad previa: {backupPath}");

            clock.Stop();
            var report = new ImportReport(
                bibleId, created, format.Id, profile.Id,
                incoming.Count, plan.Inserted, plan.Modified, plan.Unchanged, plan.ToDelete.Count, booksDeleted,
                prepared.Cleaned, prepared.Joined, prepared.DuplicateChapters,
                numbering.AppliedMerges, numbering.SkippedMerges, affected, validation, ftsOk, ftsNote, warnings, clock.ElapsedMilliseconds,
                plan.PreviousVerses, string.IsNullOrEmpty(backupPath) ? null : backupPath, profileKept, prepared.Discarded, prepared.DuplicateChaptersDifferent);

            logger.LogInformation(
                "Biblia {Abbreviation} importada (id {BibleId}, formato {Format}, perfil {Profile}{Kept}): {Verses} versículos ({Inserted} nuevos, {Updated} actualizados, {Unchanged} sin cambios, {Deleted} eliminados) en {Ms} ms; FTS {Fts}",
                abbreviation, bibleId, format.Id, profile.Id, profileKept ? " conservado" : "", report.SourceVerses, report.Inserted, report.Updated, report.Unchanged, report.Deleted, report.ElapsedMs, ftsOk ? "ok" : "con problemas");
            return report;
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync().ConfigureAwait(false);   // sin confirmar = se revierte todo
        }
    }

    // ───────────────────────────── lectura y preparación ─────────────────────────────

    /// <summary>Lo que sale de leer y limpiar la fuente. Va en su propio método para que los objetos leídos (el texto original) se puedan recoger antes de tocar la BD.</summary>
    private sealed record Prepared(List<SourceVerse> Verses, int Joined, int DuplicateChapters, int DuplicateChaptersDifferent, int Cleaned, int Discarded, List<string> Warnings);

    private async Task<Prepared> PrepareAsync(IBibleSourceFormat format, string path, IProgress<ImportProgress>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new ImportProgress("reading", 0, 1));
        BibleSourceData source;
        try { source = await format.ReadAsync(path, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // Ningún mensaje de estas excepciones lleva el contenido del archivo (solo posiciones, rutas y el aviso de codificación).
            logger.LogError(ex, "No se pudo leer la fuente {Format}", format.Id);
            throw new BibleException(BibleErrorCodes.SourceInvalid, $"No se pudo leer el archivo como «{format.Id}»: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // Texto no transcodificable u otra anomalía del contenido: se informa sin eco del contenido.
            logger.LogError(ex, "No se pudo interpretar la fuente {Format}", format.Id);
            throw new BibleException(BibleErrorCodes.SourceInvalid, $"No se pudo leer el archivo como «{format.Id}»: su contenido no es válido (¿codificación distinta de UTF-8 o archivo dañado?).", ex);
        }
        progress?.Report(new ImportProgress("reading", 1, 1));

        var warnings = new List<string>(source.Notes);
        var cleaned = 0;
        var invalid = new List<string>();      // numeración imposible (libro inexistente, capítulo o versículo menor que 1)
        var outOfCanon = new List<string>();   // capítulo mayor que los del libro o versículo mayor que 176: el parser de referencias nunca podría consultarlo
        var prepared = new List<SourceVerse>(source.Verses.Count);
        var index = 0;
        var invalidCount = 0;
        var outOfCanonCount = 0;
        foreach (var verse in source.Verses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var book = CanonicalBooks.Find(verse.BookNumber);
            if (verse.Chapter < 1 || verse.VerseNumber < 1 || book is null)
            {
                invalidCount++;
                if (invalid.Count < 10) invalid.Add(Reference(verse));
                continue;
            }
            if (verse.Chapter > book.Chapters || verse.VerseNumber > CanonicalBooks.MaxVersesInAChapter)
            {
                outOfCanonCount++;
                if (outOfCanon.Count < 10) outOfCanon.Add(Reference(verse));
                continue;
            }
            var text = VerseCleaner.Clean(verse.Text);
            var same = ReferenceEquals(text, verse.Text);
            if (!same && !string.Equals(text, verse.Text, StringComparison.Ordinal)) cleaned++;
            prepared.Add(same ? verse : verse with { Text = text });   // limpio = la misma cadena: no se duplica el texto en memoria
            if (++index % 2000 == 0) progress?.Report(new ImportProgress("cleaning", index, source.Verses.Count));
        }
        progress?.Report(new ImportProgress("cleaning", source.Verses.Count, source.Verses.Count));
        if (invalidCount > 0)
            warnings.Add($"{invalidCount} versículo(s) con numeración inválida descartado(s) (libro inexistente, capítulo o versículo menor que 1): {string.Join(", ", invalid)}{(invalidCount > invalid.Count ? ", …" : "")}.");
        if (outOfCanonCount > 0)
            warnings.Add($"{outOfCanonCount} versículo(s) fuera del canon descartado(s) (capítulo mayor que los del libro o versículo mayor que {CanonicalBooks.MaxVersesInAChapter}; no se podrían consultar): " +
                         $"{string.Join(", ", outOfCanon)}{(outOfCanonCount > outOfCanon.Count ? ", …" : "")}.");
        return new Prepared(prepared, source.JoinedVerses, source.DuplicateChaptersDiscarded, source.DuplicateChaptersDifferent, cleaned, invalidCount + outOfCanonCount, warnings);
    }

    private static string Reference(SourceVerse v) =>
        $"{CanonicalBooks.Find(v.BookNumber)?.Usfm ?? "#" + v.BookNumber.ToString(CultureInfo.InvariantCulture)} {v.Chapter.ToString(CultureInfo.InvariantCulture)}:{v.VerseNumber.ToString(CultureInfo.InvariantCulture)}";

    // ───────────────────────────── plan y protecciones ─────────────────────────────

    // Dapper devuelve INTEGER como Int64 (ver CLAUDE.md).
    private sealed record ExistingBible(long Id, string Abbreviation, string Name, string Language, string Attribution, string? NumberingProfile);

    private sealed class Plan
    {
        public ExistingBible? Existing { get; init; }
        public int PreviousVerses { get; init; }
        public required List<(int Book, int Chapter, int Verse)> ToDelete { get; init; }
        public required HashSet<(int, int, int)> ExistingKeys { get; init; }
        public required HashSet<(int, int, int)> UpdatedKeys { get; init; }
        public int Inserted { get; init; }
        public int Modified { get; init; }
        public int Unchanged { get; init; }
        public bool NeedsBackup { get; init; }
    }

    /// <summary>La Biblia ya guardada con esa abreviatura (sin distinguir mayúsculas), o null. Una conexión corta: no se retiene mientras se lee el archivo.</summary>
    private async Task<ExistingBible?> ReadStoredBibleAsync(string abbreviation, CancellationToken ct)
    {
        await using var connection = await connections.OpenWhenReadyAsync(readiness, ct).ConfigureAwait(false);
        return await FindExistingAsync(connection, null, abbreviation, ct).ConfigureAwait(false);
    }

    private static async Task<ExistingBible?> FindExistingAsync(SqliteConnection connection, SqliteTransaction? transaction, string abbreviation, CancellationToken ct)
    {
        var rows = (await connection.QueryAsync<ExistingBible>(new CommandDefinition(
            "SELECT Id, Abbreviation, Name, Language, Attribution, NumberingProfile FROM Bibles WHERE Abbreviation = @abbreviation COLLATE NOCASE ORDER BY (Abbreviation = @abbreviation) DESC, Id",
            new { abbreviation }, transaction, cancellationToken: ct)).ConfigureAwait(false)).ToList();
        if (rows.Count == 0) return null;
        if (rows.Count > 1 && rows[0].Abbreviation != abbreviation)
            throw new BibleException(BibleErrorCodes.InvalidRequest, $"Hay varias Biblias cuya abreviatura solo difiere en mayúsculas ({string.Join(", ", rows.Select(r => r.Abbreviation))}): escribe la abreviatura exacta.");
        return rows[0];
    }

    private static async Task<SqliteTransaction> BeginImmediateAsync(SqliteConnection connection, CancellationToken ct) =>
        // BEGIN IMMEDIATE: toma el bloqueo de escritura ya (espera hasta el tiempo de espera de SQLite si otra escritura está en curso). Fuera del hilo de quien llama: la espera es síncrona.
        await Task.Run(() => connection.BeginTransaction(deferred: false), ct).ConfigureAwait(false);

    /// <summary>
    /// Compara lo guardado con lo que llega (sin guardar los textos viejos: se comparan al leerlos) y aplica las protecciones. Lanza si hay que rechazar; no escribe nada.
    /// </summary>
    private static async Task<Plan> PlanAsync(
        SqliteConnection connection, SqliteTransaction transaction, BibleImportRequest request, string abbreviation, string name, NumberingProfile profile, string? keptStoredProfile, bool profileKept,
        Dictionary<(int Book, int Chapter, int Verse), SourceVerse> incoming, List<int> booksInSource, CancellationToken ct)
    {
        var existing = await FindExistingAsync(connection, transaction, abbreviation, ct).ConfigureAwait(false);
        // Si el perfil se conserva, el guardado no pudo cambiar entre la lectura previa y ahora (se compara el texto guardado tal cual, aunque sea un id que ya no existe).
        if (existing is not null && profileKept && !string.Equals(existing.NumberingProfile, keptStoredProfile, StringComparison.Ordinal))
            throw new BibleException(BibleErrorCodes.ImportFailed, "El perfil de numeración guardado cambió mientras se preparaba la importación: repítela.");

        var existingKeys = new HashSet<(int, int, int)>();
        var updatedKeys = new HashSet<(int, int, int)>();
        var toDelete = new List<(int Book, int Chapter, int Verse)>();
        var existingBooks = new HashSet<int>();
        var existingChapters = new HashSet<(int Book, int Chapter)>();
        int modified = 0, blanked = 0, unchanged = 0;
        if (existing is not null)
        {
            await using var read = connection.CreateCommand();
            read.Transaction = transaction;
            read.CommandText = "SELECT b.BookNumber, v.Chapter, v.VerseNumber, v.Text FROM Verses v JOIN Books b ON b.Id = v.BookId WHERE b.BibleId = $bibleId";
            read.Parameters.AddWithValue("$bibleId", existing.Id);
            await using var reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var key = (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
                existingKeys.Add(key);
                existingBooks.Add(key.Item1);
                existingChapters.Add((key.Item1, key.Item2));
                if (!incoming.TryGetValue(key, out var verse)) { toDelete.Add(key); continue; }
                var old = reader.GetString(3);   // el texto guardado solo se compara y se descarta: nunca se acumulan copias
                if (string.Equals(old, verse.Text, StringComparison.Ordinal)) { unchanged++; continue; }
                modified++;
                updatedKeys.Add(key);
                if (verse.Text.Length == 0 && old.Length > 0) blanked++;
            }
            toDelete.Sort();
        }
        var previous = existingKeys.Count;

        if (existing is not null)
        {
            // ── Libros y capítulos enteros que desaparecerían (aunque sean pocos versículos) ──
            var incomingChapters = new HashSet<(int, int)>(incoming.Keys.Select(k => (k.Book, k.Chapter)));
            var missingBooks = existingBooks.Where(b => !booksInSource.Contains(b)).Order().ToList();
            var missingChapters = existingChapters.Where(c => booksInSource.Contains(c.Book) && !incomingChapters.Contains(c)).Order().ToList();
            if ((missingBooks.Count > 0 || missingChapters.Count > 0) && !request.AllowLargeDeletion)
            {
                var refs = missingBooks.Select(b => CanonicalBooks.Find(b)?.Usfm ?? "#" + b).Concat(missingChapters.Select(c => $"{CanonicalBooks.Find(c.Book)?.Usfm ?? "#" + c.Book} {c.Chapter}")).Take(12).ToList();
                throw new BibleException(BibleErrorCodes.BooksWouldDisappear,
                    $"Esta importación haría desaparecer {missingBooks.Count} libro(s) y {missingChapters.Count} capítulo(s) enteros de {existing.Abbreviation} ({string.Join(", ", refs)}" +
                    $"{(missingBooks.Count + missingChapters.Count > refs.Count ? ", …" : "")}). ¿Es el archivo completo y correcto? Si es intencionado, permite la eliminación masiva.");
            }

            // ── Demasiados versículos eliminados ──
            var allowedDeletions = Math.Max(10, previous / 100);
            if (toDelete.Count > allowedDeletions && !request.AllowLargeDeletion)
                throw new BibleException(BibleErrorCodes.TooManyDeletions,
                    $"Esta importación eliminaría {toDelete.Count} versículos de {existing.Abbreviation} (más del 1 %). ¿Es el archivo correcto? Si es intencionado, permite la eliminación masiva.");

            // ── Sobrescritura silenciosa: otra traducción con la misma abreviatura cambia casi todos los textos sin borrar nada ──
            var reasons = new List<string>();
            if (!string.Equals(existing.Name.Trim(), name, StringComparison.Ordinal)) reasons.Add($"cambia el nombre de «{existing.Name}» a «{name}»");
            // Mínimo de 10 textos, pero NUNCA más de la mitad: en una Biblia pequeña el mínimo de 10 dejaría pasar el cambio de TODOS sus textos (otra traducción entera).
            var allowedChanges = Math.Min(Math.Max(10, previous / 100), Math.Max(1, previous / 2));
            var es = CultureInfo.GetCultureInfo("es");
            if (modified > allowedChanges)
                reasons.Add($"cambiaría el texto de {modified.ToString("N0", es)} versículos ({(100.0 * modified / Math.Max(1, previous)).ToString("0.#", es)} % de los {previous.ToString("N0", es)} guardados)");
            if (blanked > 0) reasons.Add($"dejaría VACÍOS {blanked} versículo(s) que ahora tienen texto");
            if (reasons.Count > 0 && !request.AllowLargeChange)
                throw new BibleException(BibleErrorCodes.TooManyChanges,
                    $"Esta importación sobrescribiría {existing.Abbreviation}: {string.Join("; ", reasons)}. ¿Es el archivo correcto (otra traducción con la misma abreviatura)? Si es intencionado, permite el cambio masivo.");
        }

        var inserted = incoming.Keys.Count(k => !existingKeys.Contains(k));
        var profileChanged = existing is not null && !string.Equals(existing.NumberingProfile ?? NumberingProfiles.NoneId, profile.Id, StringComparison.Ordinal);
        var attributionChanged = existing is not null && request.Attribution is not null && !string.Equals(existing.Attribution, request.Attribution, StringComparison.Ordinal);
        var nameDiffers = existing is not null && !string.Equals(existing.Name.Trim(), name, StringComparison.Ordinal);
        var needsBackup = existing is not null && (modified > 0 || toDelete.Count > 0 || profileChanged || attributionChanged || nameDiffers);
        return new Plan
        {
            Existing = existing, PreviousVerses = previous, ToDelete = toDelete, ExistingKeys = existingKeys, UpdatedKeys = updatedKeys,
            Inserted = inserted, Modified = modified, Unchanged = unchanged, NeedsBackup = needsBackup,
        };
    }

    // ───────────────────────────── copia de seguridad ─────────────────────────────

    /// <summary>Copia consistente (<c>VACUUM INTO</c>) de la BD en <paramref name="directory"/>; conserva solo las últimas <see cref="BackupsToKeep"/>. Si falla, no se importa nada.</summary>
    private async Task<string> BackupAsync(SqliteConnection connection, string directory, string abbreviation, CancellationToken ct)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        var safe = new string([.. abbreviation.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_')]);
        string path;
        try
        {
            Directory.CreateDirectory(directory);
            path = Path.Combine(directory, $"{BackupPrefix}{stamp}.{safe}.db");
            for (var n = 2; File.Exists(path); n++) path = Path.Combine(directory, $"{BackupPrefix}{stamp}-{n}.{safe}.db");   // nunca se pisa una copia existente
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new BibleException(BibleErrorCodes.ImportFailed, $"No se pudo preparar la carpeta de copias de seguridad ({ex.Message}); no se importó nada.", ex);
        }

        try
        {
            await connection.ExecuteAsync(new CommandDefinition("VACUUM INTO @path;", new { path }, cancellationToken: ct)).ConfigureAwait(false);
            if (!File.Exists(path) || new FileInfo(path).Length == 0) throw new IOException("La copia quedó vacía.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { /* best-effort: una copia a medias no debe quedar como si fuera válida */ }
            throw new BibleException(BibleErrorCodes.ImportFailed, $"No se pudo hacer la copia de seguridad previa a la reimportación ({ex.Message}); no se importó nada.", ex);
        }
        logger.LogInformation("Copia de seguridad previa a la reimportación: {Path}", path);
        PruneBackups(directory);
        return path;
    }

    private void PruneBackups(string directory)
    {
        try
        {
            // El sello de tiempo va primero en el nombre: ordenar por nombre = ordenar por fecha.
            var old = Directory.EnumerateFiles(directory, BackupPrefix + "*.db").OrderByDescending(Path.GetFileName, StringComparer.Ordinal).Skip(BackupsToKeep).ToList();
            foreach (var file in old)
            {
                try { File.Delete(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger.LogWarning(ex, "No se pudo borrar la copia antigua {Path}", file); }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger.LogWarning(ex, "No se pudo revisar la carpeta de copias de seguridad"); }
    }

    // ───────────────────────────── validación de la petición ─────────────────────────────

    private static (IBibleSourceFormat Format, NumberingProfile? Profile, string Abbreviation, string Name, string Language) Validate(BibleImportRequest request)
    {
        static BibleException Invalid(string message) => new(BibleErrorCodes.InvalidRequest, message);

        // La abreviatura se NORMALIZA (se recortan los espacios de los extremos) y no admite caracteres de control; se compara sin distinguir mayúsculas contra lo guardado.
        var abbreviation = (request.Abbreviation ?? "").Trim();
        if (abbreviation.Length == 0 || abbreviation.Length > MaxAbbreviationLength)
            throw Invalid($"La abreviatura es obligatoria y admite {MaxAbbreviationLength} caracteres como máximo.");
        if (abbreviation.Any(char.IsControl)) throw Invalid("La abreviatura no puede llevar caracteres de control.");
        if (string.IsNullOrWhiteSpace(request.Name)) throw Invalid("El nombre de la Biblia es obligatorio.");
        if (string.IsNullOrWhiteSpace(request.Language)) throw Invalid("El idioma de la Biblia es obligatorio.");
        if (string.IsNullOrWhiteSpace(request.Path) || !File.Exists(request.Path))
            throw new BibleException(BibleErrorCodes.FileMissing, "No existe el archivo de la Biblia indicado.");

        var format = request.FormatId is null ? BibleSourceFormats.Default : BibleSourceFormats.Find(request.FormatId)
            ?? throw Invalid($"Formato desconocido «{request.FormatId}». Formatos: {string.Join(", ", BibleSourceFormats.All.Where(f => f.CanImport).Select(f => f.Id))}.");
        if (!format.CanImport) throw Invalid($"El formato «{format.Id}» solo sirve para verificar, no para importar.");

        // Perfil: nulo o vacío = NO indicado (se conserva el guardado); cualquier otro valor, incluido «none», es una petición explícita.
        NumberingProfile? profile = null;
        if (!string.IsNullOrWhiteSpace(request.NumberingProfileId))
        {
            if (!NumberingProfiles.TryGet(request.NumberingProfileId, out var found))
                throw Invalid($"Perfil de numeración desconocido «{request.NumberingProfileId}». Perfiles: {string.Join(", ", NumberingProfiles.All.Select(p => p.Id))}.");
            profile = found;
        }
        return (format, profile, abbreviation, request.Name.Trim(), request.Language.Trim());
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

        // Historial: se comprueban TODOS los tramos de cada cita («Mt 5:3,5,7», «Gn 1-2», «Gn 1:30-2:3»), no solo el primero (H8).
        var history = (await connection.QueryAsync<(long Id, long Book, long Chapter, long? First, long? Last, string Label)>(new CommandDefinition(
            "SELECT Id, BookNumber, Chapter, VerseStart, VerseEnd, Label FROM CitationHistory WHERE BibleAbbreviation = @abbreviation",
            new { abbreviation }, transaction, cancellationToken: ct)).ConfigureAwait(false)).ToList();
        if (history.Count > 0)
        {
            var stored = (await connection.QueryAsync<(long HistoryId, long StartChapter, long? StartVerse, long EndChapter, long? EndVerse)>(new CommandDefinition(
                "SELECT s.HistoryId, s.StartChapter, s.StartVerse, s.EndChapter, s.EndVerse FROM CitationHistorySegments s JOIN CitationHistory h ON h.Id = s.HistoryId WHERE h.BibleAbbreviation = @abbreviation ORDER BY s.HistoryId, s.Position",
                new { abbreviation }, transaction, cancellationToken: ct)).ConfigureAwait(false))
                .GroupBy(s => s.HistoryId)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<ReferenceSegment>)[.. g.Select(s => new ReferenceSegment((int)s.StartChapter, (int?)s.StartVerse, (int)s.EndChapter, (int?)s.EndVerse))]);
            foreach (var h in history)
            {
                var segments = CitationHistoryStore.SegmentsOf(stored.GetValueOrDefault(h.Id), (int)h.Book, h.Label, (int)h.Chapter, (int?)h.First, (int?)h.Last);
                var hit = deleted.Any(d => d.Book == (int)h.Book && CitationHistoryStore.Contains(segments, d.Chapter, d.Verse));
                if (hit) result.Add(new AffectedItem("history", h.Id, null, h.Label));
            }
        }
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
