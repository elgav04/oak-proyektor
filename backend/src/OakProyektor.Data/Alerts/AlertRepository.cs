using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OakProyektor.Core.Alerts;
using OakProyektor.Core.Output;

namespace OakProyektor.Data.Alerts;

public interface IAlertRepository
{
    Task<SavedAlertList> ListAsync(CancellationToken cancellationToken);
    /// <summary>Crea (sin <paramref name="id"/>) o edita una alerta guardada y la fija o desfija. Crear con un texto que ya existe actualiza esa fila.</summary>
    Task<SavedAlert> SaveAsync(long? id, string text, bool pinned, CancellationToken cancellationToken);
    /// <summary>Borra la alerta; idempotente (devuelve si existía).</summary>
    Task<bool> DeleteAsync(long id, CancellationToken cancellationToken);
}

public sealed record AlertRepositoryOptions
{
    /// <summary>Cuánto espera cada comando de escritura a que otro escritor suelte el bloqueo (segundos). Pasado ese tiempo: <c>alert.busy</c> y no se cambia nada.</summary>
    public int WriteTimeoutSeconds { get; init; } = 10;
}

/// <summary>Anota el uso de una alerta (la mostró el operador). Nunca lanza ni espera al disco en las implementaciones de fondo.</summary>
public interface IAlertRecorder
{
    void Record(string normalizedText);
}

/// <summary>Escritura compartida del repositorio y del anotador de fondo. Solo texto normalizado; nada se registra con el texto.</summary>
public static class AlertStore
{
    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    public static string Stamp(DateTimeOffset time) => time.ToUniversalTime().ToString(TimeFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// Anota que se mostró una alerta: la crea como reciente (no fijada) o sube su contador y su fecha de uso (conserva su texto y si está fijada) y poda las recientes sobrantes.
    /// UNA transacción DIFERIDA con la escritura primero (el tiempo del comando acota la espera del bloqueo). <c>INSERT … ON CONFLICT</c>, nunca <c>REPLACE</c>.
    /// </summary>
    public static async Task RecordUseAsync(SqliteConnection connection, string normalizedText, DateTimeOffset now, int? commandTimeoutSeconds, CancellationToken ct)
    {
        var stamp = Stamp(now);
        await using var transaction = connection.BeginTransaction(deferred: true);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO Alerts (Text, TextKey, Pinned, UseCount, LastUsedAt, CreatedAt, UpdatedAt)
            VALUES (@text, @key, 0, 1, @stamp, @stamp, @stamp)
            ON CONFLICT(TextKey) DO UPDATE SET UseCount = UseCount + 1, LastUsedAt = excluded.LastUsedAt, UpdatedAt = excluded.UpdatedAt
            """,
            new { text = normalizedText, key = AlertRules.TextKey(normalizedText), stamp }, transaction, commandTimeout: commandTimeoutSeconds, cancellationToken: ct)).ConfigureAwait(false);
        await PruneAsync(connection, transaction, commandTimeoutSeconds, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Deja como mucho <see cref="AlertLimits.MaxRecentStored"/> alertas NO fijadas: se van las de uso más antiguo. Las fijadas no se tocan nunca.</summary>
    public static Task<int> PruneAsync(SqliteConnection connection, SqliteTransaction transaction, int? commandTimeoutSeconds, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition(
            """
            DELETE FROM Alerts WHERE Pinned = 0 AND Id NOT IN
                (SELECT Id FROM Alerts WHERE Pinned = 0 ORDER BY COALESCE(LastUsedAt, CreatedAt) DESC, Id DESC LIMIT @limit)
            """,
            new { limit = AlertLimits.MaxRecentStored }, transaction, commandTimeout: commandTimeoutSeconds, cancellationToken: ct));
}

/// <summary>
/// Repositorio de alertas guardadas. Mismas reglas que listas y canciones: conexiones de la fábrica con <c>OpenWhenReadyAsync</c>; cada escritura es UNA transacción DIFERIDA cuya PRIMERA
/// sentencia es una escritura (toma el bloqueo ya, con el tiempo del comando acotado); los registros llevan SOLO ids y conteos, nunca el texto.
/// </summary>
public sealed class AlertRepository(
    ISqliteConnectionFactory connections, IDatabaseReadiness readiness, ILogger<AlertRepository>? logger = null, AlertRepositoryOptions? options = null, TimeProvider? clock = null) : IAlertRepository
{
    private readonly ILogger<AlertRepository> _logger = logger ?? NullLogger<AlertRepository>.Instance;
    private readonly AlertRepositoryOptions _options = options ?? new AlertRepositoryOptions();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private const string Columns = "Id, Text, Pinned, UseCount, LastUsedAt";

    public async Task<SavedAlertList> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenWhenReadyAsync(readiness, cancellationToken).ConfigureAwait(false);
        var pinned = await ReadAsync(connection, $"SELECT {Columns} FROM Alerts WHERE Pinned = 1 ORDER BY Text COLLATE NOCASE, Id", null, cancellationToken).ConfigureAwait(false);
        var since = AlertStore.Stamp(_clock.GetUtcNow() - AlertLimits.RecentWindow);
        var recent = await ReadAsync(connection,
            $"SELECT {Columns} FROM Alerts WHERE Pinned = 0 AND LastUsedAt IS NOT NULL AND LastUsedAt >= @since ORDER BY LastUsedAt DESC, Id DESC LIMIT @limit",
            cmd => { cmd.Parameters.AddWithValue("@since", since); cmd.Parameters.AddWithValue("@limit", AlertLimits.RecentShown); }, cancellationToken).ConfigureAwait(false);
        return new SavedAlertList(pinned, recent);
    }

    public async Task<SavedAlert> SaveAsync(long? id, string text, bool pinned, CancellationToken cancellationToken)
    {
        var problem = AlertRules.TryNormalizeText(text, out var clean);
        if (problem is not null) throw new AlertException(AlertErrorCodes.InvalidContent, problem);
        var key = AlertRules.TextKey(clean);
        return await WriteAsync(async (c, tx) =>
        {
            var stamp = AlertStore.Stamp(_clock.GetUtcNow());
            // La primera sentencia es una escritura: toma el bloqueo ya (y acota la espera con el tiempo del comando).
            await c.ExecuteAsync(Write("UPDATE Alerts SET Id = Id WHERE Id = -1", new { }, tx, cancellationToken)).ConfigureAwait(false);
            if (pinned)
            {
                var others = await c.ExecuteScalarAsync<long>(Write("SELECT COUNT(*) FROM Alerts WHERE Pinned = 1 AND Id IS NOT @id AND TextKey <> @key", new { id, key }, tx, cancellationToken)).ConfigureAwait(false);
                if (others >= AlertLimits.MaxPinned) throw new AlertException(AlertErrorCodes.Full, $"Ya hay {AlertLimits.MaxPinned} alertas fijadas: desfija o borra alguna antes.");
            }

            long savedId;
            if (id is { } existing)
            {
                try
                {
                    var rows = await c.ExecuteAsync(Write("UPDATE Alerts SET Text = @clean, TextKey = @key, Pinned = @pinned, UpdatedAt = @stamp WHERE Id = @existing",
                        new { clean, key, pinned = pinned ? 1 : 0, stamp, existing }, tx, cancellationToken)).ConfigureAwait(false);
                    if (rows == 0) throw new AlertException(AlertErrorCodes.NotFound, "La alerta ya no existe (puede que se haya borrado).");
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 19)   // SQLITE_CONSTRAINT: el índice único de TextKey
                {
                    throw new AlertException(AlertErrorCodes.Duplicate, "Ya existe otra alerta con ese texto.", ex);
                }
                savedId = existing;
            }
            else
            {
                savedId = await c.ExecuteScalarAsync<long>(Write(
                    """
                    INSERT INTO Alerts (Text, TextKey, Pinned, UseCount, LastUsedAt, CreatedAt, UpdatedAt) VALUES (@clean, @key, @pinned, 0, NULL, @stamp, @stamp)
                    ON CONFLICT(TextKey) DO UPDATE SET Text = excluded.Text, Pinned = excluded.Pinned, UpdatedAt = excluded.UpdatedAt;
                    SELECT Id FROM Alerts WHERE TextKey = @key
                    """, new { clean, key, pinned = pinned ? 1 : 0, stamp }, tx, cancellationToken)).ConfigureAwait(false);
            }
            if (!pinned) await AlertStore.PruneAsync(c, tx, Math.Max(1, _options.WriteTimeoutSeconds), cancellationToken).ConfigureAwait(false);

            var saved = await ReadOneAsync(c, tx, savedId, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Alerta {AlertId} guardada (fijada: {Pinned})", savedId, pinned);
            return saved ?? throw new AlertException(AlertErrorCodes.NotFound, "La alerta ya no existe (puede que se haya borrado).");
        }, "guardar una alerta", cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> DeleteAsync(long id, CancellationToken cancellationToken) =>
        WriteAsync(async (c, tx) =>
        {
            var rows = await c.ExecuteAsync(Write("DELETE FROM Alerts WHERE Id = @id", new { id }, tx, cancellationToken)).ConfigureAwait(false);
            _logger.LogInformation("Alerta {AlertId} borrada: {Existed}", id, rows > 0);
            return rows > 0;
        }, "borrar una alerta", cancellationToken);

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
            throw new AlertException(AlertErrorCodes.Busy, "La base de datos está ocupada (otra operación larga, como una importación). No se cambió nada; inténtalo de nuevo en unos segundos.", ex);
        }
    }

    private CommandDefinition Write(string sql, object parameters, SqliteTransaction tx, CancellationToken ct) =>
        new(sql, parameters, tx, commandTimeout: Math.Max(1, _options.WriteTimeoutSeconds), cancellationToken: ct);

    /// <summary>Lectura con ADO a mano (la regla del repositorio: una consulta que puede no devolver filas no se lee con Dapper).</summary>
    private static async Task<IReadOnlyList<SavedAlert>> ReadAsync(SqliteConnection connection, string sql, Action<SqliteCommand>? parameters, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        parameters?.Invoke(command);
        var rows = new List<SavedAlert>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) rows.Add(Map(reader));
        return rows;
    }

    private static async Task<SavedAlert?> ReadOneAsync(SqliteConnection connection, SqliteTransaction tx, long id, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = $"SELECT {Columns} FROM Alerts WHERE Id = @id";
        command.Parameters.AddWithValue("@id", id);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    private static SavedAlert Map(SqliteDataReader r) =>
        new(r.GetInt64(0), r.GetString(1), r.GetInt64(2) != 0, (int)r.GetInt64(3),
            r.IsDBNull(4) ? null : DateTimeOffset.TryParse(r.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) ? t : null);
}
