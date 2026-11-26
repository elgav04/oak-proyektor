using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OakProyektor.Core.Media;

namespace OakProyektor.Data.Media;

/// <summary>Filtros de <see cref="IMediaRepository.ListAsync"/>. <c>FolderId</c> nulo = todas las carpetas; <c>OnlyUnfiled</c> = solo los que no están en ninguna carpeta.</summary>
public sealed record MediaQuery(long? FolderId = null, bool OnlyUnfiled = false, MediaItemKind? Kind = null, string? Text = null, int Limit = MediaLimits.MaxListed);

/// <summary>Resultado de borrar un elemento: lo que hay que borrar del disco (el hash y la extensión) y si la fila existía.</summary>
public sealed record MediaDeleteResult(bool Existed, string? Sha256, string? Ext, bool HadThumbnail);

public interface IMediaRepository
{
    Task<MediaListResult> ListAsync(MediaQuery query, CancellationToken ct);
    Task<MediaItem?> GetAsync(long id, CancellationToken ct);
    Task<MediaItem?> FindByShaAsync(string sha256, CancellationToken ct);
    /// <summary>Alta. Si el contenido ya existe lanza <see cref="MediaException"/> <c>media.duplicate</c> (la carrera entre dos importaciones la resuelve el índice único).</summary>
    Task<MediaItem> InsertAsync(NewMediaItem item, CancellationToken ct);
    Task<MediaItem> RenameAsync(long id, string name, CancellationToken ct);
    Task<MediaItem> MoveAsync(long id, long? folderId, CancellationToken ct);
    Task<MediaDeleteResult> DeleteAsync(long id, CancellationToken ct);
    /// <summary>Resultado de la verificación en el navegador: duración y dimensiones reales y estado (<c>Ok</c> o <c>Unsupported</c> con su código).</summary>
    Task<MediaItem> ApplyProbeAsync(long id, bool ok, long? durationMs, int? width, int? height, string? failureCode, CancellationToken ct);
    Task SetThumbnailAsync(long id, bool has, CancellationToken ct);
    Task<IReadOnlyList<MediaItem>> AllAsync(CancellationToken ct);
    Task SetStatusAsync(IReadOnlyCollection<long> ids, MediaStatus status, string? code, CancellationToken ct);
    Task<MediaFolder> CreateFolderAsync(string name, CancellationToken ct);
    Task<MediaFolder> RenameFolderAsync(long id, string name, CancellationToken ct);
    Task DeleteFolderAsync(long id, CancellationToken ct);
}

public sealed record MediaRepositoryOptions
{
    /// <summary>Cuánto espera cada comando de escritura a que otro escritor suelte el bloqueo (segundos). Pasado ese tiempo: <c>media.busy</c> y no se cambia nada.</summary>
    public int WriteTimeoutSeconds { get; init; } = 10;
}

/// <summary>
/// Repositorio de la biblioteca. Mismas reglas que listas, canciones y alertas: conexiones de la fábrica con <c>OpenWhenReadyAsync</c>; cada escritura es UNA transacción DIFERIDA cuya PRIMERA
/// sentencia es una escritura (toma el bloqueo ya, con el tiempo del comando acotado); lecturas con ADO a mano; los registros llevan SOLO ids y conteos (nunca nombres ni rutas).
/// </summary>
public sealed class MediaRepository(
    ISqliteConnectionFactory connections, IDatabaseReadiness readiness, ILogger<MediaRepository>? logger = null, MediaRepositoryOptions? options = null, TimeProvider? clock = null) : IMediaRepository
{
    private readonly ILogger<MediaRepository> _logger = logger ?? NullLogger<MediaRepository>.Instance;
    private readonly MediaRepositoryOptions _options = options ?? new MediaRepositoryOptions();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private const string Columns =
        "Id, Kind, Name, FolderId, OriginalFileName, Ext, Bytes, Sha256, DurationMs, Width, Height, Container, VideoCodec, AudioCodec, HasAudio, Status, StatusCode, HasThumbnail, Revision, CreatedAt, UpdatedAt";

    public async Task<MediaListResult> ListAsync(MediaQuery query, CancellationToken ct)
    {
        await using var c = await connections.OpenWhenReadyAsync(readiness, ct).ConfigureAwait(false);
        var limit = Math.Clamp(query.Limit, 1, MediaLimits.MaxListed);
        var where = new List<string>();
        await using var cmd = c.CreateCommand();
        if (query.OnlyUnfiled) where.Add("FolderId IS NULL");
        else if (query.FolderId is { } f) { where.Add("FolderId = @folder"); cmd.Parameters.AddWithValue("@folder", f); }
        if (query.Kind is { } k) { where.Add("Kind = @kind"); cmd.Parameters.AddWithValue("@kind", KindName(k)); }
        if (!string.IsNullOrWhiteSpace(query.Text)) { where.Add("Name LIKE @text ESCAPE '\\' COLLATE NOCASE"); cmd.Parameters.AddWithValue("@text", "%" + EscapeLike(query.Text.Trim()) + "%"); }
        cmd.CommandText = $"SELECT {Columns} FROM MediaItems {(where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "")} ORDER BY Name COLLATE NOCASE, Id LIMIT @limit";
        cmd.Parameters.AddWithValue("@limit", limit + 1);
        var items = new List<MediaItem>();
        await using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            while (await r.ReadAsync(ct).ConfigureAwait(false)) items.Add(Map(r));
        var truncated = items.Count > limit;

        var folders = new List<MediaFolder>();
        await using (var fc = c.CreateCommand())
        {
            fc.CommandText = "SELECT f.Id, f.Name, (SELECT COUNT(*) FROM MediaItems i WHERE i.FolderId = f.Id) FROM MediaFolders f ORDER BY f.Name COLLATE NOCASE, f.Id";
            await using var r = await fc.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false)) folders.Add(new MediaFolder(r.GetInt64(0), r.GetString(1), (int)r.GetInt64(2)));
        }
        await using var tc = c.CreateCommand();
        tc.CommandText = "SELECT COUNT(*), COALESCE(SUM(Bytes), 0) FROM MediaItems";
        await using var tr = await tc.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await tr.ReadAsync(ct).ConfigureAwait(false);
        return new MediaListResult(folders, [.. items.Take(limit)], truncated, (int)tr.GetInt64(0), tr.GetInt64(1));
    }

    public async Task<MediaItem?> GetAsync(long id, CancellationToken ct)
    {
        await using var c = await connections.OpenWhenReadyAsync(readiness, ct).ConfigureAwait(false);
        return await ReadOneAsync(c, null, "Id = @v", id, ct).ConfigureAwait(false);
    }

    public async Task<MediaItem?> FindByShaAsync(string sha256, CancellationToken ct)
    {
        await using var c = await connections.OpenWhenReadyAsync(readiness, ct).ConfigureAwait(false);
        return await ReadOneAsync(c, null, "Sha256 = @v", sha256.ToLowerInvariant(), ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MediaItem>> AllAsync(CancellationToken ct)
    {
        await using var c = await connections.OpenWhenReadyAsync(readiness, ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM MediaItems ORDER BY Id";
        var items = new List<MediaItem>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false)) items.Add(Map(r));
        return items;
    }

    public async Task<MediaItem> InsertAsync(NewMediaItem n, CancellationToken ct) =>
        await WriteAsync(async (c, tx) =>
        {
            var now = Stamp(_clock.GetUtcNow());
            await LockAsync(c, tx, ct).ConfigureAwait(false);
            if (n.FolderId is { } folder && await c.ExecuteScalarAsync<long>(Write("SELECT COUNT(*) FROM MediaFolders WHERE Id = @folder", new { folder }, tx, ct)).ConfigureAwait(false) == 0)
                throw new MediaException(MediaErrorCodes.FolderNotFound, "La carpeta no existe.");
            try
            {
                var id = await c.ExecuteScalarAsync<long>(Write(
                    """
                    INSERT INTO MediaItems (Kind, Name, FolderId, OriginalFileName, Ext, Bytes, Sha256, DurationMs, Width, Height, Container, VideoCodec, AudioCodec, HasAudio, Status, StatusCode, CreatedAt, UpdatedAt)
                    VALUES (@kind, @name, @folder, @orig, @ext, @bytes, @sha, @dur, @w, @h, @container, @vc, @ac, @hasAudio, 'PENDING', @code, @now, @now);
                    SELECT last_insert_rowid();
                    """,
                    new { kind = KindName(n.Kind), name = n.Name, folder = n.FolderId, orig = n.OriginalFileName, ext = n.Ext, bytes = n.Bytes, sha = n.Sha256.ToLowerInvariant(), dur = n.DurationMs, w = n.Width, h = n.Height,
                          container = n.Container, vc = n.VideoCodec, ac = n.AudioCodec, hasAudio = n.HasAudio ? 1 : 0, code = n.StatusCode, now }, tx, ct)).ConfigureAwait(false);
                _logger.LogInformation("Medio {MediaId} añadido ({Kind}, {Bytes} bytes)", id, n.Kind, n.Bytes);
                return (await ReadOneAsync(c, tx, "Id = @v", id, ct).ConfigureAwait(false))!;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19 && ex.Message.Contains("Sha256", StringComparison.OrdinalIgnoreCase))
            {
                throw new MediaException(MediaErrorCodes.Duplicate, MediaRules.MessageFor(MediaErrorCodes.Duplicate), ex);
            }
        }, "añadir un medio", ct).ConfigureAwait(false);

    public Task<MediaItem> RenameAsync(long id, string name, CancellationToken ct) =>
        WriteAsync(async (c, tx) =>
        {
            var clean = MediaRules.NormalizeName(name);
            var rows = await c.ExecuteAsync(Write("UPDATE MediaItems SET Name = @clean, Revision = Revision + 1, UpdatedAt = @now WHERE Id = @id", new { clean, now = Stamp(_clock.GetUtcNow()), id }, tx, ct)).ConfigureAwait(false);
            if (rows == 0) throw NotFound();
            return (await ReadOneAsync(c, tx, "Id = @v", id, ct).ConfigureAwait(false))!;
        }, "renombrar un medio", ct);

    public Task<MediaItem> MoveAsync(long id, long? folderId, CancellationToken ct) =>
        WriteAsync(async (c, tx) =>
        {
            await LockAsync(c, tx, ct).ConfigureAwait(false);
            if (folderId is { } f && await c.ExecuteScalarAsync<long>(Write("SELECT COUNT(*) FROM MediaFolders WHERE Id = @f", new { f }, tx, ct)).ConfigureAwait(false) == 0)
                throw new MediaException(MediaErrorCodes.FolderNotFound, "La carpeta no existe.");
            var rows = await c.ExecuteAsync(Write("UPDATE MediaItems SET FolderId = @folderId, Revision = Revision + 1, UpdatedAt = @now WHERE Id = @id", new { folderId, now = Stamp(_clock.GetUtcNow()), id }, tx, ct)).ConfigureAwait(false);
            if (rows == 0) throw NotFound();
            return (await ReadOneAsync(c, tx, "Id = @v", id, ct).ConfigureAwait(false))!;
        }, "mover un medio", ct);

    public Task<MediaDeleteResult> DeleteAsync(long id, CancellationToken ct) =>
        WriteAsync(async (c, tx) =>
        {
            await LockAsync(c, tx, ct).ConfigureAwait(false);
            var item = await ReadOneAsync(c, tx, "Id = @v", id, ct).ConfigureAwait(false);
            if (item is null) return new MediaDeleteResult(false, null, null, false);
            await c.ExecuteAsync(Write("DELETE FROM MediaItems WHERE Id = @id", new { id }, tx, ct)).ConfigureAwait(false);
            _logger.LogInformation("Medio {MediaId} borrado", id);
            return new MediaDeleteResult(true, item.Sha256, item.Ext, item.HasThumbnail);
        }, "borrar un medio", ct);

    public Task<MediaItem> ApplyProbeAsync(long id, bool ok, long? durationMs, int? width, int? height, string? failureCode, CancellationToken ct) =>
        WriteAsync(async (c, tx) =>
        {
            await LockAsync(c, tx, ct).ConfigureAwait(false);
            var current = await ReadOneAsync(c, tx, "Id = @v", id, ct).ConfigureAwait(false) ?? throw NotFound();
            // Lo que ya sabía la sonda de cabeceras se conserva si el navegador no lo dice; un aviso previo (HEVC…) se conserva si todo fue bien.
            var status = ok ? "OK" : "UNSUPPORTED";
            var code = ok ? current.StatusCode : failureCode ?? MediaErrorCodes.DecodeFailed;
            await c.ExecuteAsync(Write(
                "UPDATE MediaItems SET Status = @status, StatusCode = @code, DurationMs = COALESCE(@dur, DurationMs), Width = COALESCE(@w, Width), Height = COALESCE(@h, Height), Revision = Revision + 1, UpdatedAt = @now WHERE Id = @id",
                new { status, code, dur = durationMs, w = width, h = height, now = Stamp(_clock.GetUtcNow()), id }, tx, ct)).ConfigureAwait(false);
            return (await ReadOneAsync(c, tx, "Id = @v", id, ct).ConfigureAwait(false))!;
        }, "guardar la verificación de un medio", ct);

    public Task SetThumbnailAsync(long id, bool has, CancellationToken ct) =>
        WriteAsync(async (c, tx) =>
        {
            var rows = await c.ExecuteAsync(Write("UPDATE MediaItems SET HasThumbnail = @has, Revision = Revision + 1, UpdatedAt = @now WHERE Id = @id", new { has = has ? 1 : 0, now = Stamp(_clock.GetUtcNow()), id }, tx, ct)).ConfigureAwait(false);
            if (rows == 0) throw NotFound();
            return 0;
        }, "guardar la miniatura de un medio", ct);

    public Task SetStatusAsync(IReadOnlyCollection<long> ids, MediaStatus status, string? code, CancellationToken ct) =>
        WriteAsync(async (c, tx) =>
        {
            await LockAsync(c, tx, ct).ConfigureAwait(false);
            foreach (var id in ids)
                await c.ExecuteAsync(Write("UPDATE MediaItems SET Status = @status, StatusCode = @code, Revision = Revision + 1, UpdatedAt = @now WHERE Id = @id", new { status = StatusName(status), code, now = Stamp(_clock.GetUtcNow()), id }, tx, ct)).ConfigureAwait(false);
            return 0;
        }, "actualizar el estado de los medios", ct);

    public Task<MediaFolder> CreateFolderAsync(string name, CancellationToken ct) =>
        WriteAsync(async (c, tx) =>
        {
            var clean = MediaRules.NormalizeName(name, MediaLimits.MaxFolderNameChars);
            await LockAsync(c, tx, ct).ConfigureAwait(false);
            if (await c.ExecuteScalarAsync<long>(Write("SELECT COUNT(*) FROM MediaFolders", new { }, tx, ct)).ConfigureAwait(false) >= MediaLimits.MaxFolders)
                throw new MediaException(MediaErrorCodes.InvalidRequest, $"Ya hay {MediaLimits.MaxFolders} carpetas.");
            try
            {
                var id = await c.ExecuteScalarAsync<long>(Write("INSERT INTO MediaFolders (Name, NameKey, CreatedAt) VALUES (@clean, @key, @now); SELECT last_insert_rowid();",
                    new { clean, key = MediaRules.FolderKey(clean), now = Stamp(_clock.GetUtcNow()) }, tx, ct)).ConfigureAwait(false);
                return new MediaFolder(id, clean, 0);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { throw new MediaException(MediaErrorCodes.FolderDuplicate, "Ya existe una carpeta con ese nombre.", ex); }
        }, "crear una carpeta", ct);

    public Task<MediaFolder> RenameFolderAsync(long id, string name, CancellationToken ct) =>
        WriteAsync(async (c, tx) =>
        {
            var clean = MediaRules.NormalizeName(name, MediaLimits.MaxFolderNameChars);
            try
            {
                var rows = await c.ExecuteAsync(Write("UPDATE MediaFolders SET Name = @clean, NameKey = @key WHERE Id = @id", new { clean, key = MediaRules.FolderKey(clean), id }, tx, ct)).ConfigureAwait(false);
                if (rows == 0) throw new MediaException(MediaErrorCodes.FolderNotFound, "La carpeta no existe.");
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { throw new MediaException(MediaErrorCodes.FolderDuplicate, "Ya existe una carpeta con ese nombre.", ex); }
            var count = await c.ExecuteScalarAsync<long>(Write("SELECT COUNT(*) FROM MediaItems WHERE FolderId = @id", new { id }, tx, ct)).ConfigureAwait(false);
            return new MediaFolder(id, clean, (int)count);
        }, "renombrar una carpeta", ct);

    public Task DeleteFolderAsync(long id, CancellationToken ct) =>
        WriteAsync(async (c, tx) =>
        {
            // Los elementos NO se borran: su carpeta pasa a nula (ON DELETE SET NULL).
            await c.ExecuteAsync(Write("DELETE FROM MediaFolders WHERE Id = @id", new { id }, tx, ct)).ConfigureAwait(false);
            return 0;
        }, "borrar una carpeta", ct);

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
            throw new MediaException(MediaErrorCodes.Busy, "La base de datos está ocupada (otra operación larga). No se cambió nada; inténtalo de nuevo en unos segundos.", ex);
        }
    }

    /// <summary>La primera sentencia de la transacción es una escritura: toma el bloqueo ya, con el tiempo del comando acotado.</summary>
    private Task LockAsync(SqliteConnection c, SqliteTransaction tx, CancellationToken ct) =>
        c.ExecuteAsync(Write("UPDATE MediaItems SET Id = Id WHERE Id = -1", new { }, tx, ct));

    private CommandDefinition Write(string sql, object parameters, SqliteTransaction tx, CancellationToken ct) =>
        new(sql, parameters, tx, commandTimeout: Math.Max(1, _options.WriteTimeoutSeconds), cancellationToken: ct);

    private static async Task<MediaItem?> ReadOneAsync(SqliteConnection c, SqliteTransaction? tx, string where, object value, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT {Columns} FROM MediaItems WHERE {where}";
        cmd.Parameters.AddWithValue("@v", value);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false) ? Map(r) : null;
    }

    private static MediaException NotFound() => new(MediaErrorCodes.NotFound, MediaRules.MessageFor(MediaErrorCodes.NotFound));

    private static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    public static string KindName(MediaItemKind k) => k switch { MediaItemKind.Video => "VIDEO", MediaItemKind.Audio => "AUDIO", _ => "IMAGE" };
    public static string StatusName(MediaStatus s) => s switch { MediaStatus.Pending => "PENDING", MediaStatus.Ok => "OK", MediaStatus.Unsupported => "UNSUPPORTED", _ => "MISSING" };
    private static MediaItemKind KindOf(string s) => s switch { "VIDEO" => MediaItemKind.Video, "AUDIO" => MediaItemKind.Audio, _ => MediaItemKind.Image };
    private static MediaStatus StatusOf(string s) => s switch { "OK" => MediaStatus.Ok, "UNSUPPORTED" => MediaStatus.Unsupported, "MISSING" => MediaStatus.Missing, _ => MediaStatus.Pending };

    public static string Stamp(DateTimeOffset t) => t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseTime(string s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) ? t : DateTimeOffset.UnixEpoch;

    private static MediaItem Map(SqliteDataReader r)
    {
        string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        long? L(int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
        return new MediaItem(r.GetInt64(0), KindOf(r.GetString(1)), r.GetString(2), L(3), r.GetString(4), r.GetString(5), r.GetInt64(6), r.GetString(7),
            L(8), L(9) is { } w ? (int)w : null, L(10) is { } h ? (int)h : null, S(11), S(12), S(13), r.GetInt64(14) != 0, StatusOf(r.GetString(15)), S(16), r.GetInt64(17) != 0,
            (int)r.GetInt64(18), ParseTime(r.GetString(19)), ParseTime(r.GetString(20)));
    }
}
