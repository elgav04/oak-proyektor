using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OakProyektor.Core.Library;
using OakProyektor.Core.Playlists;
using OakProyektor.Data.Repositories;

namespace OakProyektor.Data.Playlists;

public sealed record PlaylistRepositoryOptions
{
    /// <summary>Cuánto espera cada comando de escritura a que otro escritor suelte el bloqueo (segundos). Pasado ese tiempo: <c>playlist.busy</c> y no se cambia nada.</summary>
    public int WriteTimeoutSeconds { get; init; } = 10;
}

/// <summary>
/// Repositorio de listas de culto. Mismas reglas que <c>SongRepository</c>: conexiones de la fábrica con <c>OpenWhenReadyAsync</c>; cada escritura es UNA transacción DIFERIDA
/// (<c>BeginTransaction(deferred: true)</c>) cuya PRIMERA sentencia es una escritura (toma el bloqueo ya, con el tiempo del comando acotado); los registros llevan SOLO ids y conteos.
/// Las lecturas con columnas calculadas (el título actual, <c>IsMissing</c>) se hacen con ADO a mano: Dapper toma por blob una columna calculada SIN filas.
/// </summary>
public sealed class PlaylistRepository(
    ISqliteConnectionFactory connections, IDatabaseReadiness readiness, ILogger<PlaylistRepository>? logger = null, PlaylistRepositoryOptions? options = null, TimeProvider? clock = null) : IPlaylistRepository
{
    private readonly ILogger<PlaylistRepository> _logger = logger ?? NullLogger<PlaylistRepository>.Instance;
    private readonly PlaylistRepositoryOptions _options = options ?? new PlaylistRepositoryOptions();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private const string ItemColumns = "pi.Id, pi.SequenceIndex, pi.ItemType, COALESCE(s.Title, pi.Title), pi.IsMissing, pi.SongId, pi.PresentationId, pi.BookNumber, pi.Chapter, pi.VerseStart, pi.VerseEnd, pi.BibleAbbreviation, pi.Note, pi.ClearOnActivate, pi.PlaylistId, pi.Title";

    // ───────────────────────────── lectura ─────────────────────────────

    public async Task<IReadOnlyList<Playlist>> GetAllAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenWhenReadyAsync(readiness, cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<SimpleRow>(new CommandDefinition(
            "SELECT Id, Name, ServiceDate FROM Playlists ORDER BY (ServiceDate IS NULL), ServiceDate DESC, Id", cancellationToken: cancellationToken)).ConfigureAwait(false);
        return [.. rows.Select(r => new Playlist(r.Id, r.Name, r.ServiceDate))];
    }

    public async Task<Playlist?> GetAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenWhenReadyAsync(readiness, cancellationToken).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<SimpleRow>(new CommandDefinition(
            "SELECT Id, Name, ServiceDate FROM Playlists WHERE Id = @id", new { id }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row is null ? null : new Playlist(row.Id, row.Name, row.ServiceDate);
    }

    public async Task<IReadOnlyList<PlaylistItem>> GetItemsAsync(long playlistId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenWhenReadyAsync(readiness, cancellationToken).ConfigureAwait(false);
        var rows = await ReadItemsAsync(connection, null, playlistId, cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(r => new PlaylistItem(r.Id, r.PlaylistId, r.SequenceIndex, r.ItemType, r.StoredTitle, r.SongId, r.PresentationId,
            r.BookNumber, r.Chapter, r.VerseStart, r.VerseEnd, r.BibleAbbreviation, r.IsMissing, r.Note, r.ClearOnActivate))];
    }

    public async Task<PlaylistListResult> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenWhenReadyAsync(readiness, cancellationToken).ConfigureAwait(false);
        var rows = new List<PlaylistSummary>();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT p.Id, p.Name, p.ServiceDate,
                   (SELECT COUNT(*) FROM PlaylistItems WHERE PlaylistId = p.Id),
                   (SELECT COUNT(*) FROM PlaylistItems WHERE PlaylistId = p.Id AND IsMissing = 1),
                   p.Revision
            FROM Playlists p
            ORDER BY (p.ServiceDate IS NULL), p.ServiceDate DESC, p.Name COLLATE NOCASE, p.Id
            LIMIT @limit
            """;
        command.Parameters.AddWithValue("@limit", PlaylistLimits.MaxListed + 1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            rows.Add(new PlaylistSummary(reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), (int)reader.GetInt64(3), (int)reader.GetInt64(4), (int)reader.GetInt64(5)));
        return new PlaylistListResult([.. rows.Take(PlaylistLimits.MaxListed)], rows.Count > PlaylistLimits.MaxListed);
    }

    public async Task<PlaylistDetail?> GetDetailAsync(long id, string? defaultBibleAbbreviation, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenWhenReadyAsync(readiness, cancellationToken).ConfigureAwait(false);
        // La cabecera se lee PRIMERO: si una escritura se cuela entre medias, los elementos serán más nuevos que la revisión que lleva la vista, y su próxima edición recibirá
        // `playlist.conflict` (el lado seguro: nunca una revisión más nueva con elementos viejos).
        var head = await connection.QuerySingleOrDefaultAsync<HeadRow>(new CommandDefinition(
            "SELECT Id, Name, ServiceDate, Notes, Revision, CreatedAt, UpdatedAt FROM Playlists WHERE Id = @id", new { id }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (head is null) return null;

        var rows = await ReadItemsAsync(connection, null, id, cancellationToken).ConfigureAwait(false);
        var resolutions = rows.Any(r => r.ItemType == PlaylistItemTypes.Passage)
            ? (await PassageResolver.ResolvePlaylistAsync(connection, id, defaultBibleAbbreviation, cancellationToken).ConfigureAwait(false)).ToDictionary(r => r.ItemId, r => r.Resolution)
            : new Dictionary<long, PassageResolution>();

        var items = new List<PlaylistItemDetail>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            resolutions.TryGetValue(r.Id, out var resolution);
            var resolutionName = r.ItemType == PlaylistItemTypes.Passage && resolution is not null ? PlaylistResolutions.Of(resolution.Status) : null;
            var offered = resolution?.Offered is { IsResolved: true } o ? o.PreferredBibleAbbreviation : null;
            items.Add(new PlaylistItemDetail(r.Id, i, TypeName(r.ItemType), r.Title, r.IsMissing, r.SongId, r.BookNumber, r.Chapter, r.VerseStart, r.VerseEnd, r.BibleAbbreviation,
                r.Note, r.ClearOnActivate, resolutionName, offered, PlaylistNavigation.IsProjectable(r.ItemType, r.IsMissing, resolutionName)));
        }
        return new PlaylistDetail(head.Id, head.Name, head.ServiceDate, head.Notes, (int)head.Revision, ParseDate(head.CreatedAt), ParseDate(head.UpdatedAt),
            items.Count(i => i.IsMissing), items);
    }

    // ───────────────────────────── escritura ─────────────────────────────

    public async Task<PlaylistWriteResult> CreateAsync(string name, string? serviceDate, string? notes, CancellationToken cancellationToken)
    {
        var cleanName = PlaylistRules.NormalizeName(name);
        var date = PlaylistRules.NormalizeDate(serviceDate);
        var cleanNotes = PlaylistRules.NormalizeNotes(notes);
        return await WriteAsync(async (c, tx) =>
        {
            var now = Now();
            var id = await c.ExecuteScalarAsync<long>(Write(
                "INSERT INTO Playlists (Name, ServiceDate, Notes, CreatedAt, UpdatedAt) VALUES (@cleanName, @date, @cleanNotes, @now, @now); SELECT last_insert_rowid();",
                new { cleanName, date, cleanNotes, now }, tx, cancellationToken)).ConfigureAwait(false);
            _logger.LogInformation("Lista {PlaylistId} creada", id);
            return new PlaylistWriteResult(id, 1, null);
        }, "la creación de una lista", cancellationToken).ConfigureAwait(false);
    }

    public async Task<PlaylistWriteResult> UpdateAsync(long id, string name, string? serviceDate, string? notes, int expectedRevision, CancellationToken cancellationToken)
    {
        var cleanName = PlaylistRules.NormalizeName(name);
        var date = PlaylistRules.NormalizeDate(serviceDate);
        var cleanNotes = PlaylistRules.NormalizeNotes(notes);
        return await WriteAsync(async (c, tx) =>
        {
            await LockAndCheckAsync(c, tx, id, expectedRevision, cancellationToken).ConfigureAwait(false);
            await c.ExecuteAsync(Write("UPDATE Playlists SET Name = @cleanName, ServiceDate = @date, Notes = @cleanNotes WHERE Id = @id", new { cleanName, date, cleanNotes, id }, tx, cancellationToken)).ConfigureAwait(false);
            var revision = await BumpAsync(c, tx, id, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Lista {PlaylistId} actualizada: revisión {Revision}", id, revision);
            return new PlaylistWriteResult(id, revision, null);
        }, "la actualización de una lista", cancellationToken).ConfigureAwait(false);
    }

    public async Task<PlaylistWriteResult> AddItemAsync(long playlistId, PlaylistItemInput item, int? position, CancellationToken cancellationToken)
    {
        var prepared = PrepareItem(item);
        return await WriteAsync(async (c, tx) =>
        {
            await LockAndCheckAsync(c, tx, playlistId, null, cancellationToken).ConfigureAwait(false);
            var ids = await ItemIdsAsync(c, tx, playlistId, cancellationToken).ConfigureAwait(false);
            if (ids.Count >= PlaylistLimits.MaxItems)
                throw new PlaylistException(PlaylistErrorCodes.Full, $"La lista ya tiene {PlaylistLimits.MaxItems} elementos (el máximo). Quita alguno o crea otra lista.");
            if (position is { } p && (p < 0 || p > ids.Count))
                throw new PlaylistException(PlaylistErrorCodes.InvalidRequest, $"La posición debe estar entre 0 y {ids.Count}.");

            var title = prepared.Title;
            if (prepared.ItemType == PlaylistItemTypes.Song)
            {
                // El título instantánea sale de la canción (si no existe, no se crea un elemento que nace «faltante»).
                title = await c.ExecuteScalarAsync<string?>(Write("SELECT Title FROM Songs WHERE Id = @SongId", new { prepared.SongId }, tx, cancellationToken)).ConfigureAwait(false)
                    ?? throw new PlaylistException(PlaylistErrorCodes.InvalidRequest, "La canción que se quiere añadir no existe.");
            }

            var itemId = await c.ExecuteScalarAsync<long>(Write(
                """
                INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title, SongId, BookNumber, Chapter, VerseStart, VerseEnd, BibleAbbreviation, Note, ClearOnActivate)
                VALUES (@playlistId, @seq, @ItemType, @title, @SongId, @BookNumber, @Chapter, @VerseStart, @VerseEnd, @BibleAbbreviation, @Note, @ClearOnActivate);
                SELECT last_insert_rowid();
                """,
                new { playlistId, seq = ids.Count, prepared.ItemType, title, prepared.SongId, prepared.BookNumber, prepared.Chapter, prepared.VerseStart, prepared.VerseEnd, prepared.BibleAbbreviation, prepared.Note, ClearOnActivate = prepared.ClearOnActivate ? 1 : 0 },
                tx, cancellationToken)).ConfigureAwait(false);

            var total = ids.Count + 1;
            if (position is { } at && at < ids.Count)
            {
                ids.Insert(at, itemId);
                await ReindexAsync(c, tx, ids, cancellationToken).ConfigureAwait(false);
            }
            var revision = await BumpAsync(c, tx, playlistId, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Lista {PlaylistId}: elemento {ItemId} ({Type}) añadido; {Items} elementos, revisión {Revision}", playlistId, itemId, prepared.ItemType, total, revision);
            return new PlaylistWriteResult(playlistId, revision, itemId);
        }, "añadir un elemento", cancellationToken).ConfigureAwait(false);
    }

    public async Task<PlaylistWriteResult> MoveItemAsync(long playlistId, long itemId, int toIndex, int expectedRevision, CancellationToken cancellationToken) =>
        await WriteAsync(async (c, tx) =>
        {
            var revision = await LockAndCheckAsync(c, tx, playlistId, expectedRevision, cancellationToken).ConfigureAwait(false);
            var ids = await ItemIdsAsync(c, tx, playlistId, cancellationToken).ConfigureAwait(false);
            var from = ids.IndexOf(itemId);
            if (from < 0) throw ItemNotFound();
            if (toIndex < 0 || toIndex >= ids.Count) throw new PlaylistException(PlaylistErrorCodes.InvalidRequest, $"La posición debe estar entre 0 y {ids.Count - 1}.");
            if (from == toIndex) return new PlaylistWriteResult(playlistId, revision, itemId);   // sin cambio: no sube la revisión (no invalida a nadie)

            ids.RemoveAt(from);
            ids.Insert(toIndex, itemId);
            await ReindexAsync(c, tx, ids, cancellationToken).ConfigureAwait(false);
            revision = await BumpAsync(c, tx, playlistId, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Lista {PlaylistId}: elemento {ItemId} movido de {From} a {To}, revisión {Revision}", playlistId, itemId, from, toIndex, revision);
            return new PlaylistWriteResult(playlistId, revision, itemId);
        }, "mover un elemento", cancellationToken).ConfigureAwait(false);

    public async Task<PlaylistWriteResult> RemoveItemAsync(long playlistId, long itemId, int expectedRevision, CancellationToken cancellationToken) =>
        await WriteAsync(async (c, tx) =>
        {
            await LockAndCheckAsync(c, tx, playlistId, expectedRevision, cancellationToken).ConfigureAwait(false);
            var deleted = await c.ExecuteAsync(Write("DELETE FROM PlaylistItems WHERE Id = @itemId AND PlaylistId = @playlistId", new { itemId, playlistId }, tx, cancellationToken)).ConfigureAwait(false);
            if (deleted == 0) throw ItemNotFound();
            await ReindexAsync(c, tx, await ItemIdsAsync(c, tx, playlistId, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            var revision = await BumpAsync(c, tx, playlistId, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Lista {PlaylistId}: elemento {ItemId} quitado, revisión {Revision}", playlistId, itemId, revision);
            return new PlaylistWriteResult(playlistId, revision, itemId);
        }, "quitar un elemento", cancellationToken).ConfigureAwait(false);

    public async Task<PlaylistWriteResult> UpdateItemAsync(long playlistId, long itemId, PlaylistItemUpdate update, int expectedRevision, CancellationToken cancellationToken) =>
        await WriteAsync(async (c, tx) =>
        {
            var revision = await LockAndCheckAsync(c, tx, playlistId, expectedRevision, cancellationToken).ConfigureAwait(false);
            var row = await c.QuerySingleOrDefaultAsync<EditableRow>(Write(
                "SELECT ItemType, Title, Note, ClearOnActivate, BibleAbbreviation FROM PlaylistItems WHERE Id = @itemId AND PlaylistId = @playlistId",
                new { itemId, playlistId }, tx, cancellationToken)).ConfigureAwait(false) ?? throw ItemNotFound();

            // Primero se valida (un campo que no corresponde al tipo es un error aunque no cambie nada); después se compara con lo guardado, ya normalizado.
            var changes = new List<(string Column, object? Value)>();
            switch (row.ItemType)
            {
                case PlaylistItemTypes.Note:
                    if (update.BibleAbbreviation is not null) throw Invalid("Una nota no tiene Biblia.");
                    if (update.Title is not null && PlaylistRules.NormalizeItemTitle(update.Title) is var title && title != row.Title) changes.Add(("Title", title));
                    if (update.Note is not null && PlaylistRules.NormalizeItemNote(update.Note) is var note && note != row.Note) changes.Add(("Note", note));
                    if (update.ClearOnActivate is { } clear && clear != (row.ClearOnActivate != 0)) changes.Add(("ClearOnActivate", clear ? 1 : 0));
                    break;
                case PlaylistItemTypes.Passage:
                    if (update.Title is not null || update.Note is not null || update.ClearOnActivate is not null) throw Invalid("Un pasaje solo permite cambiar su Biblia preferida.");
                    if (update.BibleAbbreviation is not null && NormalizeBible(update.BibleAbbreviation) is var bible && bible != row.BibleAbbreviation) changes.Add(("BibleAbbreviation", bible));
                    break;
                default:
                    throw Invalid("Este tipo de elemento no tiene campos editables.");
            }

            // Sin cambios: ni se escribe ni se sube la revisión (no invalida las demás vistas) ni se avisa a nadie.
            if (changes.Count == 0) return new PlaylistWriteResult(playlistId, revision, itemId, Changed: false);

            foreach (var (column, value) in changes)
                await c.ExecuteAsync(Write($"UPDATE PlaylistItems SET {column} = @value WHERE Id = @itemId", new { value, itemId }, tx, cancellationToken)).ConfigureAwait(false);
            revision = await BumpAsync(c, tx, playlistId, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Lista {PlaylistId}: elemento {ItemId} editado ({Fields} campo(s)), revisión {Revision}", playlistId, itemId, changes.Count, revision);
            return new PlaylistWriteResult(playlistId, revision, itemId);
        }, "editar un elemento", cancellationToken).ConfigureAwait(false);

    public async Task<PlaylistDuplicateResult> DuplicateAsync(long id, string name, string? serviceDate, CancellationToken cancellationToken)
    {
        var cleanName = PlaylistRules.NormalizeName(name);
        var date = PlaylistRules.NormalizeDate(serviceDate);
        return await WriteAsync(async (c, tx) =>
        {
            var now = Now();
            // Primera sentencia = escritura; si no inserta ninguna fila, la lista de origen no existe.
            var inserted = await c.ExecuteAsync(Write(
                "INSERT INTO Playlists (Name, ServiceDate, Notes, CreatedAt, UpdatedAt) SELECT @cleanName, @date, Notes, @now, @now FROM Playlists WHERE Id = @id",
                new { cleanName, date, now, id }, tx, cancellationToken)).ConfigureAwait(false);
            if (inserted == 0) throw NotFound();
            var newId = await c.ExecuteScalarAsync<long>(Write("SELECT last_insert_rowid()", new { }, tx, cancellationToken)).ConfigureAwait(false);

            var skipped = (int)await c.ExecuteScalarAsync<long>(Write("SELECT COUNT(*) FROM PlaylistItems WHERE PlaylistId = @id AND IsMissing = 1", new { id }, tx, cancellationToken)).ConfigureAwait(false);
            // Los faltantes no se copian: el trigger no deja crear una canción sin su registro. La instantánea del título de una canción se refresca con el título actual.
            var copied = await c.ExecuteAsync(Write(
                """
                INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title, SongId, PresentationId, BookNumber, Chapter, VerseStart, VerseEnd, BibleAbbreviation, Note, ClearOnActivate)
                SELECT @newId, ROW_NUMBER() OVER (ORDER BY pi.SequenceIndex, pi.Id) - 1, pi.ItemType, COALESCE(s.Title, pi.Title), pi.SongId, pi.PresentationId,
                       pi.BookNumber, pi.Chapter, pi.VerseStart, pi.VerseEnd, pi.BibleAbbreviation, pi.Note, pi.ClearOnActivate
                FROM PlaylistItems pi LEFT JOIN Songs s ON s.Id = pi.SongId
                WHERE pi.PlaylistId = @id AND pi.IsMissing = 0
                ORDER BY pi.SequenceIndex, pi.Id
                """, new { newId, id }, tx, cancellationToken)).ConfigureAwait(false);
            _logger.LogInformation("Lista {PlaylistId} duplicada como {NewPlaylistId}: {Copied} elementos copiados, {Skipped} omitidos (faltantes)", id, newId, copied, skipped);
            return new PlaylistDuplicateResult(newId, 1, copied, skipped);
        }, "duplicar una lista", cancellationToken).ConfigureAwait(false);
    }

    public async Task<PlaylistDeleteResult> DeleteAsync(long id, int expectedRevision, CancellationToken cancellationToken) =>
        await WriteAsync(async (c, tx) =>
        {
            await LockAndCheckAsync(c, tx, id, expectedRevision, cancellationToken).ConfigureAwait(false);   // una vista desactualizada no borra una lista que otra ventana acaba de cambiar
            await c.ExecuteAsync(Write("DELETE FROM Playlists WHERE Id = @id", new { id }, tx, cancellationToken)).ConfigureAwait(false);   // sus elementos se van en cascada; canciones y Biblias no se tocan
            _logger.LogInformation("Lista {PlaylistId} borrada", id);
            return new PlaylistDeleteResult(id);
        }, "borrar una lista", cancellationToken).ConfigureAwait(false);

    // ───────────────────────────── núcleo ─────────────────────────────

    private async Task<T> WriteAsync<T>(Func<SqliteConnection, SqliteTransaction, Task<T>> action, string what, CancellationToken ct)
    {
        try
        {
            await using var connection = await connections.OpenWhenReadyAsync(readiness, ct).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: true);
            var result = await action(connection, transaction).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return result;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)   // SQLITE_BUSY, SQLITE_LOCKED
        {
            _logger.LogWarning("Se abandonó {What}: la base de datos está ocupada", what);
            throw new PlaylistException(PlaylistErrorCodes.Busy, "La base de datos está ocupada (otra operación larga, como una importación). No se cambió nada; inténtalo de nuevo en unos segundos.", ex);
        }
    }

    /// <summary>Toma el bloqueo de escritura (primera sentencia de la transacción), comprueba que la lista existe y, si se pide, que la revisión es la vigente. Devuelve la revisión vigente.</summary>
    private async Task<int> LockAndCheckAsync(SqliteConnection c, SqliteTransaction tx, long id, int? expectedRevision, CancellationToken ct)
    {
        if (await c.ExecuteAsync(Write("UPDATE Playlists SET Revision = Revision WHERE Id = @id", new { id }, tx, ct)).ConfigureAwait(false) == 0) throw NotFound();
        var current = (int)await c.ExecuteScalarAsync<long>(Write("SELECT Revision FROM Playlists WHERE Id = @id", new { id }, tx, ct)).ConfigureAwait(false);
        if (expectedRevision is { } expected && expected != current)
            throw new PlaylistException(PlaylistErrorCodes.Conflict, "La lista cambió desde que la abriste (otra ventana o una edición anterior). No se cambió nada: vuelve a leerla y repite la acción.");
        return current;
    }

    private async Task<int> BumpAsync(SqliteConnection c, SqliteTransaction tx, long id, CancellationToken ct)
    {
        await c.ExecuteAsync(Write("UPDATE Playlists SET Revision = Revision + 1, UpdatedAt = @now WHERE Id = @id", new { now = Now(), id }, tx, ct)).ConfigureAwait(false);
        return (int)await c.ExecuteScalarAsync<long>(Write("SELECT Revision FROM Playlists WHERE Id = @id", new { id }, tx, ct)).ConfigureAwait(false);
    }

    private async Task<List<long>> ItemIdsAsync(SqliteConnection c, SqliteTransaction tx, long playlistId, CancellationToken ct) =>
        [.. await c.QueryAsync<long>(Write("SELECT Id FROM PlaylistItems WHERE PlaylistId = @playlistId ORDER BY SequenceIndex, Id", new { playlistId }, tx, ct)).ConfigureAwait(false)];

    /// <summary>Reescribe SequenceIndex = 0..n−1 según el orden dado (como mucho 100 elementos). El índice no es único, así que no hay estados intermedios que choquen.</summary>
    private async Task ReindexAsync(SqliteConnection c, SqliteTransaction tx, IReadOnlyList<long> orderedIds, CancellationToken ct)
    {
        for (var i = 0; i < orderedIds.Count; i++)
        {
            var id = orderedIds[i];
            await c.ExecuteAsync(Write("UPDATE PlaylistItems SET SequenceIndex = @i WHERE Id = @id AND SequenceIndex <> @i", new { i, id }, tx, ct)).ConfigureAwait(false);
        }
    }

    private async Task<List<ItemRow>> ReadItemsAsync(SqliteConnection c, SqliteTransaction? tx, long playlistId, CancellationToken ct)
    {
        var rows = new List<ItemRow>();
        await using var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText = $"SELECT {ItemColumns} FROM PlaylistItems pi LEFT JOIN Songs s ON s.Id = pi.SongId WHERE pi.PlaylistId = @playlistId ORDER BY pi.SequenceIndex, pi.Id";
        command.Parameters.AddWithValue("@playlistId", playlistId);
        await using var r = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            rows.Add(new ItemRow(
                r.GetInt64(0), r.GetInt64(14), (int)r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetString(15), r.GetInt64(4) != 0,
                r.IsDBNull(5) ? null : r.GetInt64(5), r.IsDBNull(6) ? null : r.GetInt64(6),
                r.IsDBNull(7) ? null : (int)r.GetInt64(7), r.IsDBNull(8) ? null : (int)r.GetInt64(8), r.IsDBNull(9) ? null : (int)r.GetInt64(9), r.IsDBNull(10) ? null : (int)r.GetInt64(10),
                r.IsDBNull(11) ? null : r.GetString(11), r.IsDBNull(12) ? null : r.GetString(12), r.GetInt64(13) != 0));
        return rows;
    }

    // ───────────────────────────── validación de un elemento ─────────────────────────────

    private sealed record Prepared(string ItemType, long? SongId, int? BookNumber, int? Chapter, int? VerseStart, int? VerseEnd, string? BibleAbbreviation, string Title, string? Note, bool ClearOnActivate);

    private static Prepared PrepareItem(PlaylistItemInput item)
    {
        switch (item.ItemType)
        {
            case PlaylistItemTypes.Song:
                if (item.SongId is not { } songId || songId < 1) throw Invalid("Falta la canción que se quiere añadir.");
                if (item.BookNumber is not null || item.Chapter is not null || item.VerseStart is not null || item.VerseEnd is not null || item.BibleAbbreviation is not null || item.Note is not null || item.ClearOnActivate)
                    throw Invalid("Una canción no lleva referencia bíblica ni nota.");
                return new Prepared(PlaylistItemTypes.Song, songId, null, null, null, null, null, "", null, false);

            case PlaylistItemTypes.Passage:
                if (item.SongId is not null || item.Note is not null || item.ClearOnActivate) throw Invalid("Un pasaje no lleva canción ni nota.");
                if (item.BookNumber is not { } book || book is < PassageReference.FirstCanonicalBook or > PassageReference.LastCanonicalBook) throw Invalid("El libro debe ser un número canónico de 1 a 66.");
                if (item.Chapter is not { } chapter || chapter < 1) throw Invalid("El capítulo debe ser 1 o mayor.");
                if (item.VerseStart is not { } start || start < 1) throw Invalid("El versículo inicial debe ser 1 o mayor.");
                if (item.VerseEnd is { } end && end < start) throw Invalid("El versículo final no puede ser menor que el inicial.");
                var bible = NormalizeBible(item.BibleAbbreviation);
                var title = string.IsNullOrWhiteSpace(item.Title) ? PlaylistRules.PassageTitle(book, chapter, start, item.VerseEnd, bible) : PlaylistRules.NormalizeItemTitle(item.Title);
                return new Prepared(PlaylistItemTypes.Passage, null, book, chapter, start, item.VerseEnd, bible, title, null, false);

            case PlaylistItemTypes.Note:
                if (item.SongId is not null || item.BookNumber is not null || item.Chapter is not null || item.VerseStart is not null || item.VerseEnd is not null || item.BibleAbbreviation is not null)
                    throw Invalid("Una nota no lleva canción ni referencia bíblica.");
                return new Prepared(PlaylistItemTypes.Note, null, null, null, null, null, null, PlaylistRules.NormalizeItemTitle(item.Title), PlaylistRules.NormalizeItemNote(item.Note), item.ClearOnActivate);

            default:
                throw Invalid("El tipo de elemento debe ser canción, pasaje o nota.");
        }
    }

    private static string NormalizeBible(string? abbreviation)
    {
        var value = (abbreviation ?? "").Trim();
        if (value.Length == 0) throw Invalid("Falta la abreviatura de la Biblia.");
        if (value.Length > 40 || value.Any(char.IsControl)) throw Invalid("La abreviatura de la Biblia no es válida.");
        return value;
    }

    // ───────────────────────────── utilidades ─────────────────────────────

    private static PlaylistException Invalid(string message) => new(PlaylistErrorCodes.InvalidRequest, message);
    private static PlaylistException NotFound() => new(PlaylistErrorCodes.NotFound, "La lista no existe (puede que se haya borrado).");
    private static PlaylistException ItemNotFound() => new(PlaylistErrorCodes.ItemNotFound, "El elemento ya no está en la lista.");

    private CommandDefinition Write(string sql, object parameters, SqliteTransaction tx, CancellationToken ct) =>
        new(sql, parameters, tx, commandTimeout: Math.Max(1, _options.WriteTimeoutSeconds), cancellationToken: ct);

    private string Now() => _clock.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseDate(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date) ? date : DateTimeOffset.UnixEpoch;

    /// <summary>El nombre del tipo en el protocolo (minúsculas): <c>song</c>, <c>passage</c>, <c>note</c> (<c>presentation</c> no se ofrece todavía).</summary>
    internal static string TypeName(string dbType) => dbType.ToLowerInvariant();

    private sealed record SimpleRow(long Id, string Name, string? ServiceDate);

    private sealed record EditableRow(string ItemType, string Title, string? Note, long ClearOnActivate, string? BibleAbbreviation);

    private sealed record HeadRow(long Id, string Name, string? ServiceDate, string Notes, long Revision, string? CreatedAt, string? UpdatedAt);

    private sealed record ItemRow(
        long Id, long PlaylistId, int SequenceIndex, string ItemType, string Title, string StoredTitle, bool IsMissing, long? SongId, long? PresentationId,
        int? BookNumber, int? Chapter, int? VerseStart, int? VerseEnd, string? BibleAbbreviation, string? Note, bool ClearOnActivate);
}
