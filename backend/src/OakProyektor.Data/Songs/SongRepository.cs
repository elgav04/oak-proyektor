using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OakProyektor.Core.Songs;
using OakProyektor.Data.Repositories;

namespace OakProyektor.Data.Songs;

public sealed record SongRepositoryOptions
{
    /// <summary>Cuánto espera cada comando de escritura a que otro escritor suelte el bloqueo (segundos). Pasado ese tiempo: <c>song.busy</c> y no se cambia nada.</summary>
    public int WriteTimeoutSeconds { get; init; } = 10;
}

/// <summary>
/// Repositorio de canciones. Toda conexión sale de la fábrica y espera a la BD (<c>OpenWhenReadyAsync</c>). Cada escritura es UNA transacción DIFERIDA
/// (<c>BeginTransaction(deferred: true)</c>: el BEGIN por defecto de Microsoft.Data.Sqlite es IMMEDIATE y esperaría 30 s al bloqueo sin respetar el tiempo del comando) cuya
/// PRIMERA sentencia es una escritura, para tomar el bloqueo ya (con el tiempo acotado) y no fallar más tarde por «instantánea caducada».
/// Los registros llevan SOLO identificadores y conteos: nunca letra, títulos ni etiquetas.
/// </summary>
public sealed class SongRepository(
    ISqliteConnectionFactory connections, IDatabaseReadiness readiness, ILogger<SongRepository>? logger = null, SongRepositoryOptions? options = null, TimeProvider? clock = null) : ISongRepository
{
    private readonly ILogger<SongRepository> _logger = logger ?? NullLogger<SongRepository>.Instance;
    private readonly SongRepositoryOptions _options = options ?? new SongRepositoryOptions();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    // ───────────────────────────── lectura ─────────────────────────────

    public async Task<SongDetail?> GetAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenWhenReadyAsync(readiness, cancellationToken).ConfigureAwait(false);
        return await LoadAsync(connection, null, id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SongListResult> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenWhenReadyAsync(readiness, cancellationToken).ConfigureAwait(false);
        // Con ADO a mano y no con Dapper: la columna calculada (COALESCE) no tiene tipo declarado y, con la tabla VACÍA (primer arranque), Dapper la toma por blob
        // y falla al armar el registro («Error interno» al abrir la pestaña sin ninguna canción).
        var rows = new List<SummaryRow>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT Id, Title, Author, COALESCE(UpdatedAt, CreatedAt) AS UpdatedAt FROM Songs";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                rows.Add(new SummaryRow(reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3)));
        }

        // Orden por título sin distinguir mayúsculas ni tildes (el NOCASE de SQLite solo cubre ASCII): «Él» y «el» van juntos.
        var sorted = rows.Select(r => (Row: r, Key: SortKey(r.Title))).OrderBy(x => x.Key, StringComparer.Ordinal).ThenBy(x => x.Row.Id).Select(x => x.Row).ToList();
        var truncated = sorted.Count > SongLimits.MaxListed;
        return new SongListResult([.. sorted.Take(SongLimits.MaxListed).Select(r => new SongSummary(r.Id, r.Title, r.Author, ParseDate(r.UpdatedAt), null))], truncated);
    }

    public async Task<SongSearchResult> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var match = FtsQueryBuilder.Build(query, prefixOnLastWord: true);
        if (match is null) throw new SongException(SongErrorCodes.InvalidRequest, "Escribe al menos una palabra para buscar.");
        limit = limit <= 0 ? SongLimits.DefaultSearchLimit : Math.Min(limit, SongLimits.MaxSearchLimit);

        await using var connection = await connections.OpenWhenReadyAsync(readiness, cancellationToken).ConfigureAwait(false);
        var found = new List<SongSummary>();
        var truncated = false;
        // ADO a mano: con Dapper, snippet() sin filas se declara como blob y el mapeo a un record falla (como en la búsqueda de la Biblia).
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT s.Id, s.Title, s.Author, COALESCE(s.UpdatedAt, s.CreatedAt), snippet(SongsFts, -1, '«', '»', '…', 12)
            FROM SongsFts
            JOIN Songs s ON s.Id = SongsFts.rowid
            WHERE SongsFts MATCH $match
            ORDER BY bm25(SongsFts, 10.0, 3.0, 1.0), s.Title COLLATE NOCASE, s.Id
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$match", match);
        command.Parameters.AddWithValue("$limit", limit + 1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (found.Count == limit) { truncated = true; break; }
            found.Add(new SongSummary(reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), ParseDate(reader.GetString(3)),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }
        return new SongSearchResult(query, truncated, found);
    }

    public async Task<IReadOnlyList<SongRevisionInfo>> GetHistoryAsync(long songId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenWhenReadyAsync(readiness, cancellationToken).ConfigureAwait(false);
        if (await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT COUNT(*) FROM Songs WHERE Id = @songId", new { songId }, cancellationToken: cancellationToken)).ConfigureAwait(false) == 0)
            throw NotFound();
        // ADO a mano: una consulta de columnas calculadas (json_array_length) SIN filas la declara Dapper como blob y el mapeo a un record falla (ver CLAUDE.md).
        var revisions = new List<SongRevisionInfo>();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, CreatedAt, Reason, COALESCE(json_array_length(Snapshot, '$.sections'), 0)
            FROM SongRevisions WHERE SongId = $songId ORDER BY Id DESC
            """;
        command.Parameters.AddWithValue("$songId", songId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            revisions.Add(new SongRevisionInfo(reader.GetInt64(0), ParseDate(reader.GetString(1)), reader.GetString(2), (int)reader.GetInt64(3)));
        return revisions;
    }

    // ───────────────────────────── escritura ─────────────────────────────

    public async Task<SongSaveResult> SaveAsync(SongSaveRequest request, CancellationToken cancellationToken)
    {
        var prepared = Prepare(request);
        try
        {
            await using var connection = await connections.OpenWhenReadyAsync(readiness, cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: true);
            SongSaveResult result;
            if (request.Id is null) result = await CreateAsync(connection, transaction, prepared, cancellationToken).ConfigureAwait(false);
            else
            {
                if (request.ExpectedRevision is not { } expected)
                    throw new SongException(SongErrorCodes.InvalidRequest, "Falta la revisión de la canción que se está guardando (expectedRevision).");
                await LockAndCheckRevisionAsync(connection, transaction, request.Id.Value, expected, cancellationToken).ConfigureAwait(false);
                result = await UpdateAsync(connection, transaction, request.Id.Value, expected, prepared, request.Reason, cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Canción {SongId} {Action}: revisión {Revision}, {Sections} secciones, {Items} elementos en el orden",
                result.SongId, result.Created ? "creada" : "guardada", result.Revision, prepared.Sections.Count, prepared.Arrangement.Count);
            return result;
        }
        catch (SqliteException ex) when (IsBusy(ex))
        {
            _logger.LogWarning("Se abandonó el guardado de la canción {SongId}: la base de datos está ocupada", request.Id);
            throw Busy(ex);
        }
    }

    public async Task<SongSaveResult> RestoreRevisionAsync(long songId, long? revisionId, int expectedRevision, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await connections.OpenWhenReadyAsync(readiness, cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: true);
            await LockAndCheckRevisionAsync(connection, transaction, songId, expectedRevision, cancellationToken).ConfigureAwait(false);

            var revision = revisionId is { } wanted
                ? await connection.QuerySingleOrDefaultAsync<RevisionContent>(new CommandDefinition("SELECT Id, Snapshot FROM SongRevisions WHERE Id = @wanted AND SongId = @songId", new { wanted, songId }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)
                : await connection.QueryFirstOrDefaultAsync<RevisionContent>(new CommandDefinition("SELECT Id, Snapshot FROM SongRevisions WHERE SongId = @songId ORDER BY Id DESC LIMIT 1", new { songId }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (revision is null) throw new SongException(SongErrorCodes.NotFound, "No hay una copia previa que restaurar para esta canción.");

            Snapshot snapshot;
            try { snapshot = JsonSerializer.Deserialize<Snapshot>(revision.Snapshot, SnapshotJson) ?? throw new JsonException("vacío"); }
            catch (JsonException) { throw new SongException(SongErrorCodes.InvalidRequest, "La copia previa está dañada y no se puede restaurar."); }

            // Las secciones que siguen existiendo se actualizan EN SU SITIO (conservan su id); las que se borraron después de la copia se vuelven a crear.
            var existing = (await connection.QueryAsync<long>(new CommandDefinition("SELECT Id FROM SongSections WHERE SongId = @songId", new { songId }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToHashSet();
            var request = new SongSaveRequest(songId, expectedRevision, snapshot.Title, snapshot.Author, snapshot.Copyright, snapshot.CcliNumber, snapshot.Notes,
                snapshot.Background is { } b ? new SongBackground(b.Kind, b.Path, b.Color, b.Muted) : null,
                [.. snapshot.Sections.Select(s => new SongSectionInput(existing.Contains(s.Id) ? s.Id : null, s.Label, s.Kind, s.Text))],
                snapshot.Arrangement, SongSaveReason.Restore);

            Prepared prepared;
            try { prepared = Prepare(request); }
            catch (SongException ex) when (ex.Code == SongErrorCodes.InvalidRequest) { throw new SongException(SongErrorCodes.InvalidRequest, "La copia previa ya no cumple las reglas actuales y no se puede restaurar."); }

            var result = await UpdateAsync(connection, transaction, songId, expectedRevision, prepared, SongSaveReason.Restore, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Canción {SongId}: restaurada la copia {RevisionId} (revisión {Revision})", songId, revision.Id, result.Revision);
            return result;
        }
        catch (SqliteException ex) when (IsBusy(ex))
        {
            _logger.LogWarning("Se abandonó la restauración de la canción {SongId}: la base de datos está ocupada", songId);
            throw Busy(ex);
        }
    }

    public async Task<SongDeleteResult> DeleteAsync(long id, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await connections.OpenWhenReadyAsync(readiness, cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: true);
            // Primera sentencia = escritura (toma el bloqueo con el tiempo acotado). Si no toca ninguna fila, la canción no existe.
            if (await connection.ExecuteAsync(Write("UPDATE Songs SET Revision = Revision WHERE Id = @id", new { id }, transaction, cancellationToken)).ConfigureAwait(false) == 0)
                throw NotFound();
            var used = (int)await connection.ExecuteScalarAsync<long>(Write("SELECT COUNT(DISTINCT PlaylistId) FROM PlaylistItems WHERE SongId = @id", new { id }, transaction, cancellationToken)).ConfigureAwait(false);
            // Sus elementos de servicio pasan a «faltantes» (FK SET NULL, el título se conserva); secciones, orden y copias se van en cascada.
            await connection.ExecuteAsync(Write("DELETE FROM Songs WHERE Id = @id", new { id }, transaction, cancellationToken)).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Canción {SongId} borrada (estaba en {Services} servicio(s))", id, used);
            return new SongDeleteResult(id, used);
        }
        catch (SqliteException ex) when (IsBusy(ex))
        {
            _logger.LogWarning("Se abandonó el borrado de la canción {SongId}: la base de datos está ocupada", id);
            throw Busy(ex);
        }
    }

    // ───────────────────────────── núcleo de la escritura ─────────────────────────────

    private async Task<SongSaveResult> CreateAsync(SqliteConnection c, SqliteTransaction tx, Prepared p, CancellationToken ct)
    {
        if (p.Sections.Any(s => s.Id is not null))
            throw new SongException(SongErrorCodes.InvalidRequest, "Una canción nueva no puede traer identificadores de sección.");

        var now = Now();
        var id = await c.ExecuteScalarAsync<long>(Write(
            """
            INSERT INTO Songs (Title, Author, Copyright, CcliNumber, Notes, UpdatedAt, BackgroundKind, BackgroundPath, BackgroundColor, BackgroundMuted)
            VALUES (@Title, @Author, @Copyright, @Ccli, @Notes, @now, @BgKind, @BgPath, @BgColor, @BgMuted);
            SELECT last_insert_rowid();
            """, MetadataParameters(p, now), tx, ct)).ConfigureAwait(false);
        var arrangementId = await c.ExecuteScalarAsync<long>(Write(
            "INSERT INTO SongArrangements (SongId, Name, IsDefault) VALUES (@id, 'Principal', 1); SELECT last_insert_rowid();", new { id }, tx, ct)).ConfigureAwait(false);

        var sectionIds = new List<long>(p.Sections.Count);
        for (var i = 0; i < p.Sections.Count; i++)
            sectionIds.Add(await InsertSectionAsync(c, tx, id, i, p.Sections[i].Section, ct).ConfigureAwait(false));
        await InsertArrangementAsync(c, tx, arrangementId, p.Arrangement, sectionIds, ct).ConfigureAwait(false);
        return new SongSaveResult(id, 1, true);
    }

    private async Task<SongSaveResult> UpdateAsync(SqliteConnection c, SqliteTransaction tx, long id, int expected, Prepared p, SongSaveReason reason, CancellationToken ct)
    {
        // Las secciones que llegan con id deben ser de ESTA canción; ninguna se repite.
        var current = (await c.QueryAsync<long>(Write("SELECT Id FROM SongSections WHERE SongId = @id", new { id }, tx, ct)).ConfigureAwait(false)).ToHashSet();
        var kept = p.Sections.Where(s => s.Id is not null).Select(s => s.Id!.Value).ToList();
        if (kept.Any(k => !current.Contains(k)))
            throw new SongException(SongErrorCodes.InvalidRequest, "Una de las secciones no pertenece a esta canción.");
        if (kept.Distinct().Count() != kept.Count)
            throw new SongException(SongErrorCodes.InvalidRequest, "Una sección aparece dos veces en la petición.");

        // 1) Copia previa: el estado ACTUAL, antes de tocar nada. Sin repetir: si ese mismo contenido ya está en la última copia no se crea otra, y si está en una más antigua
        //    se SUBE al final (se reemplaza por la nueva): guardar sin cambios o deshacer y rehacer una y otra vez no gasta las 20 copias de versiones distintas.
        var before = await LoadAsync(c, tx, id, ct).ConfigureAwait(false) ?? throw NotFound();
        var snapshot = Snapshot.Of(before);
        var copies = (await c.QueryAsync<RevisionContent>(Write("SELECT Id, Snapshot FROM SongRevisions WHERE SongId = @id ORDER BY Id DESC", new { id }, tx, ct)).ConfigureAwait(false)).ToList();
        var same = copies.Where(r => SameContent(r.Snapshot, snapshot)).Select(r => r.Id).ToList();
        if (same.Count == 0 || same[0] != copies[0].Id)
        {
            foreach (var duplicate in same) await c.ExecuteAsync(Write("DELETE FROM SongRevisions WHERE Id = @duplicate", new { duplicate }, tx, ct)).ConfigureAwait(false);
            await c.ExecuteAsync(Write("INSERT INTO SongRevisions (SongId, Reason, Snapshot) VALUES (@id, @reason, @snapshot)",
                new { id, reason = ReasonName(reason), snapshot = JsonSerializer.Serialize(snapshot, SnapshotJson) }, tx, ct)).ConfigureAwait(false);
        }

        var arrangementId = await c.ExecuteScalarAsync<long?>(Write("SELECT Id FROM SongArrangements WHERE SongId = @id AND IsDefault = 1", new { id }, tx, ct)).ConfigureAwait(false)
            ?? await c.ExecuteScalarAsync<long>(Write("INSERT INTO SongArrangements (SongId, Name, IsDefault) VALUES (@id, 'Principal', 1); SELECT last_insert_rowid();", new { id }, tx, ct)).ConfigureAwait(false);

        // 2) El orden se rehace entero; después se borran las secciones que ya no vienen (el orden ya no las usa: la clave foránea no se queja).
        await c.ExecuteAsync(Write("DELETE FROM SongArrangementItems WHERE ArrangementId IN (SELECT Id FROM SongArrangements WHERE SongId = @id)", new { id }, tx, ct)).ConfigureAwait(false);
        foreach (var gone in current.Except(kept))
            await c.ExecuteAsync(Write("DELETE FROM SongSections WHERE Id = @gone", new { gone }, tx, ct)).ConfigureAwait(false);

        // 3) Etiquetas provisionales (con un carácter de control, que ninguna etiqueta válida puede llevar): intercambiar las etiquetas de dos secciones no choca con el índice único.
        foreach (var k in kept)
            await c.ExecuteAsync(Write("UPDATE SongSections SET Label = char(1) || Id WHERE Id = @k", new { k }, tx, ct)).ConfigureAwait(false);

        // 4) Las existentes se actualizan EN SU SITIO; las nuevas se insertan.
        var sectionIds = new List<long>(p.Sections.Count);
        for (var i = 0; i < p.Sections.Count; i++)
        {
            var (inputId, section) = p.Sections[i];
            if (inputId is { } existing)
            {
                await c.ExecuteAsync(Write("UPDATE SongSections SET Position = @i, Kind = @Kind, Label = @Label, Text = @Text WHERE Id = @existing AND SongId = @id",
                    new { i, section.Kind, section.Label, section.Text, existing, id }, tx, ct)).ConfigureAwait(false);
                sectionIds.Add(existing);
            }
            else sectionIds.Add(await InsertSectionAsync(c, tx, id, i, section, ct).ConfigureAwait(false));
        }
        await InsertArrangementAsync(c, tx, arrangementId, p.Arrangement, sectionIds, ct).ConfigureAwait(false);

        // 5) Metadatos y revisión (al final: el índice de búsqueda ya ve las secciones nuevas).
        var changed = await c.ExecuteAsync(Write(
            """
            UPDATE Songs SET Title = @Title, Author = @Author, Copyright = @Copyright, CcliNumber = @Ccli, Notes = @Notes, UpdatedAt = @now,
                             BackgroundKind = @BgKind, BackgroundPath = @BgPath, BackgroundColor = @BgColor, BackgroundMuted = @BgMuted,
                             Revision = Revision + 1
            WHERE Id = @id AND Revision = @expected
            """, new { id, expected, p.Title, p.Author, p.Copyright, p.Ccli, p.Notes, now = Now(), p.BgKind, p.BgPath, p.BgColor, p.BgMuted }, tx, ct)).ConfigureAwait(false);
        if (changed == 0) throw new SongException(SongErrorCodes.Conflict, "La canción cambió mientras la editabas. Vuelve a abrirla para ver la versión actual.");

        // 6) Solo se conservan las últimas copias previas.
        await c.ExecuteAsync(Write(
            "DELETE FROM SongRevisions WHERE SongId = @id AND Id NOT IN (SELECT Id FROM SongRevisions WHERE SongId = @id ORDER BY Id DESC LIMIT @keep)",
            new { id, keep = SongLimits.MaxRevisionsPerSong }, tx, ct)).ConfigureAwait(false);
        return new SongSaveResult(id, expected + 1, false);
    }

    /// <summary>Toma el bloqueo de escritura ya (tiempo acotado) y comprueba la revisión sin cambiar nada: <c>song.notFound</c> si no existe, <c>song.conflict</c> si caducó.</summary>
    private async Task LockAndCheckRevisionAsync(SqliteConnection c, SqliteTransaction tx, long id, int expected, CancellationToken ct)
    {
        var touched = await c.ExecuteAsync(Write("UPDATE Songs SET Revision = Revision WHERE Id = @id AND Revision = @expected", new { id, expected }, tx, ct)).ConfigureAwait(false);
        if (touched > 0) return;
        var exists = await c.ExecuteScalarAsync<long>(Write("SELECT COUNT(*) FROM Songs WHERE Id = @id", new { id }, tx, ct)).ConfigureAwait(false);
        if (exists == 0) throw NotFound();
        throw new SongException(SongErrorCodes.Conflict, "La canción cambió mientras la editabas. Vuelve a abrirla para ver la versión actual.");
    }

    private async Task<long> InsertSectionAsync(SqliteConnection c, SqliteTransaction tx, long songId, int position, ParsedSection s, CancellationToken ct) =>
        await c.ExecuteScalarAsync<long>(Write(
            "INSERT INTO SongSections (SongId, Position, Kind, Label, Text) VALUES (@songId, @position, @Kind, @Label, @Text); SELECT last_insert_rowid();",
            new { songId, position, s.Kind, s.Label, s.Text }, tx, ct)).ConfigureAwait(false);

    private async Task InsertArrangementAsync(SqliteConnection c, SqliteTransaction tx, long arrangementId, IReadOnlyList<int> arrangement, List<long> sectionIds, CancellationToken ct)
    {
        for (var position = 0; position < arrangement.Count; position++)
        {
            var sectionId = sectionIds[arrangement[position]];
            await c.ExecuteAsync(Write("INSERT INTO SongArrangementItems (ArrangementId, Position, SectionId) VALUES (@arrangementId, @position, @sectionId)",
                new { arrangementId, position, sectionId }, tx, ct)).ConfigureAwait(false);
        }
    }

    // ───────────────────────────── validación y normalización ─────────────────────────────

    private sealed record Prepared(
        string Title, string? Author, string? Copyright, string? Ccli, string? Notes,
        string? BgKind, string? BgPath, string? BgColor, bool BgMuted,
        IReadOnlyList<(long? Id, ParsedSection Section)> Sections, IReadOnlyList<int> Arrangement);

    private static object MetadataParameters(Prepared p, string now) =>
        new { p.Title, p.Author, p.Copyright, p.Ccli, p.Notes, now, p.BgKind, p.BgPath, p.BgColor, p.BgMuted };

    /// <summary>Normaliza y valida ANTES de abrir la BD. Un problema es <c>song.invalidRequest</c> (o <c>tooManySections</c> / <c>textTooLarge</c>) y no toca nada. Sin letra en los mensajes.</summary>
    private static Prepared Prepare(SongSaveRequest request)
    {
        if (request.Sections is null || request.Arrangement is null)
            throw new SongException(SongErrorCodes.InvalidRequest, "Faltan las secciones o el orden de la canción.");
        if (request.Sections.Count > SongLimits.MaxSections)
            throw new SongException(SongErrorCodes.TooManySections, $"Hay demasiadas secciones ({request.Sections.Count}; el máximo es {SongLimits.MaxSections}).");
        if (request.Sections.Any(s => (s.Text?.Length ?? 0) > SongLimits.MaxSectionChars * 4))
            throw new SongException(SongErrorCodes.TextTooLarge, $"Una sección es demasiado larga (el máximo es {SongLimits.MaxSectionChars} caracteres).");

        var title = SongTextNormalizer.NormalizeSingleLine(request.Title);
        var author = NullIfEmpty(SongTextNormalizer.NormalizeSingleLine(request.Author));
        var copyright = NullIfEmpty(SongTextNormalizer.NormalizeSingleLine(request.Copyright));
        var ccli = NullIfEmpty(SongTextNormalizer.NormalizeSingleLine(request.CcliNumber));
        var notes = NullIfEmpty(SongTextNormalizer.NormalizeBody(request.Notes, out _));
        var sections = request.Sections.Select(s => (s.Id, Section: SongValidator.NormalizeSection(s.Label, s.Text))).ToList();

        var problems = new List<string>();
        problems.AddRange(SongValidator.ValidateMetadata(title, author, copyright, ccli, notes));
        if (SongValidator.ValidateBackground(request.Background) is { } backgroundProblem) problems.Add(backgroundProblem);
        problems.AddRange(SongValidator.ValidateStructure([.. sections.Select(s => s.Section)], request.Arrangement));
        if (problems.Count > 0)
        {
            if (sections.Any(s => s.Section.Text.Length > SongLimits.MaxSectionChars))
                throw new SongException(SongErrorCodes.TextTooLarge, string.Join(" ", problems));
            throw new SongException(SongErrorCodes.InvalidRequest, string.Join(" ", problems));
        }

        var bg = request.Background;
        return new Prepared(title, author, copyright, ccli, notes,
            bg?.Kind, bg?.Kind == "color" ? null : bg?.Path.NullIfBlank(), bg?.Color, bg?.Muted ?? true,
            sections, request.Arrangement);
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    // ───────────────────────────── lectura compartida ─────────────────────────────

    private static async Task<SongDetail?> LoadAsync(SqliteConnection c, SqliteTransaction? tx, long id, CancellationToken ct)
    {
        var row = await c.QuerySingleOrDefaultAsync<SongRow>(new CommandDefinition(
            """
            SELECT Id, Title, Author, Copyright, CcliNumber, Notes, Revision, CreatedAt, COALESCE(UpdatedAt, CreatedAt) AS UpdatedAt,
                   BackgroundKind, BackgroundPath, BackgroundColor, BackgroundMuted
            FROM Songs WHERE Id = @id
            """, new { id }, tx, cancellationToken: ct)).ConfigureAwait(false);
        if (row is null) return null;

        var sections = (await c.QueryAsync<SectionRow>(new CommandDefinition(
            "SELECT Id, Kind, Label, Text FROM SongSections WHERE SongId = @id ORDER BY Position, Id", new { id }, tx, cancellationToken: ct)).ConfigureAwait(false))
            .Select(s => new SongSection(s.Id, s.Kind, s.Label, s.Text)).ToList();
        var indexOf = new Dictionary<long, int>();
        for (var i = 0; i < sections.Count; i++) indexOf[sections[i].Id] = i;

        var items = await c.QueryAsync<long>(new CommandDefinition(
            """
            SELECT i.SectionId FROM SongArrangementItems i
            JOIN SongArrangements a ON a.Id = i.ArrangementId AND a.IsDefault = 1
            WHERE a.SongId = @id ORDER BY i.Position, i.Id
            """, new { id }, tx, cancellationToken: ct)).ConfigureAwait(false);
        var arrangement = items.Where(indexOf.ContainsKey).Select(s => indexOf[s]).ToList();

        var used = await c.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(DISTINCT PlaylistId) FROM PlaylistItems WHERE SongId = @id", new { id }, tx, cancellationToken: ct)).ConfigureAwait(false);

        var background = row.BackgroundKind is { } kind ? new SongBackground(kind, row.BackgroundPath, row.BackgroundColor, row.BackgroundMuted != 0) : null;
        return new SongDetail(row.Id, row.Title, row.Author, row.Copyright, row.CcliNumber, row.Notes, (int)row.Revision,
            ParseDate(row.CreatedAt), ParseDate(row.UpdatedAt), background, sections, arrangement, (int)used);
    }

    // ───────────────────────────── utilidades ─────────────────────────────

    private CommandDefinition Write(string sql, object parameters, SqliteTransaction tx, CancellationToken ct) =>
        new(sql, parameters, tx, commandTimeout: Math.Max(1, _options.WriteTimeoutSeconds), cancellationToken: ct);

    private string Now() => _clock.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseDate(string text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date) ? date : DateTimeOffset.UnixEpoch;

    /// <summary>Clave de orden por título: minúsculas y sin tildes.</summary>
    internal static string SortKey(string title)
    {
        try
        {
            var sb = new StringBuilder(title.Length);
            foreach (var c in title.Normalize(NormalizationForm.FormD))
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }
        catch (ArgumentException) { return title.ToLowerInvariant(); }
    }

    private static bool IsBusy(SqliteException ex) => ex.SqliteErrorCode is 5 or 6;   // SQLITE_BUSY, SQLITE_LOCKED

    private static SongException Busy(Exception inner) =>
        new(SongErrorCodes.Busy, "La base de datos está ocupada (otra operación larga, como una importación). No se cambió nada; inténtalo de nuevo en unos segundos.", inner);

    private static SongException NotFound() => new(SongErrorCodes.NotFound, "La canción no existe (puede que se haya borrado).");

    private static string ReasonName(SongSaveReason reason) => reason switch
    {
        SongSaveReason.Import => "import",
        SongSaveReason.Restore => "restore",
        _ => "save",
    };

    // Dapper devuelve INTEGER como Int64 (ver CLAUDE.md): los DTO de lectura usan long.
    private sealed record SongRow(long Id, string Title, string? Author, string? Copyright, string? CcliNumber, string? Notes, long Revision, string CreatedAt, string UpdatedAt,
        string? BackgroundKind, string? BackgroundPath, string? BackgroundColor, long BackgroundMuted);
    private sealed record SectionRow(long Id, string Kind, string Label, string Text);
    private sealed record SummaryRow(long Id, string Title, string? Author, string UpdatedAt);
    private sealed record RevisionContent(long Id, string Snapshot);

    // ───────────────────────────── copia previa (JSON) ─────────────────────────────

    private static readonly JsonSerializerOptions SnapshotJson = new(JsonSerializerDefaults.Web);

    /// <summary>Contenido de una copia previa (<c>SongRevisions.Snapshot</c>). Lleva letra: vive solo en la BD local del operador y nunca se registra.</summary>
    private sealed record Snapshot(int V, string Title, string? Author, string? Copyright, string? CcliNumber, string? Notes, SnapshotBackground? Background, List<SnapshotSection> Sections, List<int> Arrangement)
    {
        public static Snapshot Of(SongDetail d) => new(1, d.Title, d.Author, d.Copyright, d.CcliNumber, d.Notes,
            d.Background is { } b ? new SnapshotBackground(b.Kind, b.Path, b.Color, b.Muted) : null,
            [.. d.Sections.Select(s => new SnapshotSection(s.Id, s.Kind, s.Label, s.Text))], [.. d.Arrangement]);
    }

    /// <summary>¿La copia guardada tiene el MISMO contenido que <paramref name="current"/>? Se ignoran los ids de sección (una sección borrada y vuelta a crear al restaurar tiene otro id).</summary>
    private static bool SameContent(string stored, Snapshot current)
    {
        try
        {
            var other = JsonSerializer.Deserialize<Snapshot>(stored, SnapshotJson);
            return other is not null && ContentKey(other) == ContentKey(current);
        }
        catch (JsonException) { return false; }
    }

    private static string ContentKey(Snapshot s) => JsonSerializer.Serialize(s with { Sections = [.. s.Sections.Select(x => x with { Id = 0 })] }, SnapshotJson);

    private sealed record SnapshotSection(long Id, string Kind, string Label, string Text);
    private sealed record SnapshotBackground(string Kind, string? Path, string? Color, bool Muted);
}

internal static class SongStringExtensions
{
    public static string? NullIfBlank(this string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
