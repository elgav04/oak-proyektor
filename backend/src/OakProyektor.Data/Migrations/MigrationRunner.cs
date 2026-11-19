using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace OakProyektor.Data.Migrations;

/// <param name="BackupPath">Copia hecha antes de migrar; null si no hacía falta (BD nueva o ya al día).</param>
public sealed record MigrationRunResult(int FromVersion, int ToVersion, string? BackupPath, IReadOnlyList<int> Applied);

/// <summary>Una migración falló y se revirtió por completo; la BD queda como estaba (y con la copia previa, si se hizo).</summary>
public sealed class MigrationException(int version, string message, string? backupPath, Exception? inner = null) : Exception(message, inner)
{
    public int Version { get; } = version;
    public string? BackupPath { get; } = backupPath;
}

/// <summary>
/// Aplica migraciones numeradas con estas garantías (hallazgo I1):
/// <list type="number">
/// <item>Antes de aplicar la primera migración pendiente sobre una BD con datos, copia la BD con <c>VACUUM INTO</c> a
/// <c>data/backups</c>; si la copia falla, NO se migra.</item>
/// <item>Cada migración y su <c>user_version</c> van en UNA transacción: o se aplica completa o nada.</item>
/// <item><c>PRAGMA foreign_keys = OFF</c> ANTES del <c>BEGIN</c> (dentro de una transacción no tiene efecto): así reconstruir una
/// tabla padre (crear nueva, copiar, <c>DROP</c> de la vieja, renombrar) no borra en cascada a las hijas.</item>
/// <item><c>PRAGMA foreign_key_check</c> ANTES del <c>COMMIT</c>: si la migración dejó referencias rotas, se revierte.</item>
/// <item><c>foreign_keys</c> se reactiva SIEMPRE al terminar, tenga éxito o falle.</item>
/// </list>
/// </summary>
public sealed class MigrationRunner
{
    private readonly IReadOnlyList<Migration> _migrations;
    private readonly DatabaseOptions _options;
    private readonly ILogger<MigrationRunner> _logger;
    private readonly Func<DateTimeOffset> _clock;

    public MigrationRunner(IReadOnlyList<Migration> migrations, DatabaseOptions options, ILogger<MigrationRunner> logger, Func<DateTimeOffset>? clock = null)
    {
        if (migrations.Count == 0) throw new ArgumentException("Debe haber al menos una migración.", nameof(migrations));
        for (var i = 0; i < migrations.Count; i++)
            if (migrations[i].Version != i + 1) throw new ArgumentException($"Las migraciones deben ser consecutivas desde 1; falta la {i + 1}.", nameof(migrations));
        _migrations = migrations; _options = options; _logger = logger; _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public int LatestVersion => _migrations[^1].Version;

    /// <summary>Usa una conexión ya abierta (con <c>foreign_keys = ON</c>), sin transacción activa.</summary>
    public async Task<MigrationRunResult> RunAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var current = (int)await connection.ExecuteScalarAsync<long>(new CommandDefinition("PRAGMA user_version;", cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (current > LatestVersion)
            throw new InvalidOperationException(
                $"La base de datos tiene esquema v{current}, más nuevo que esta versión de la aplicación (v{LatestVersion}). Actualiza la aplicación.");

        var pending = _migrations.Where(m => m.Version > current).ToList();
        if (pending.Count == 0) return new MigrationRunResult(current, current, null, []);

        // Una BD recién creada (v0) no tiene nada que proteger.
        var backupPath = current > 0 ? await BackupAsync(connection, current, cancellationToken).ConfigureAwait(false) : null;

        var applied = new List<int>();
        foreach (var migration in pending)
        {
            await ApplyAsync(connection, migration, backupPath, cancellationToken).ConfigureAwait(false);
            applied.Add(migration.Version);
            _logger.LogInformation("Migración {Version} ({Name}) aplicada", migration.Version, migration.Name);
        }
        return new MigrationRunResult(current, LatestVersion, backupPath, applied);
    }

    private async Task<string> BackupAsync(SqliteConnection connection, int fromVersion, CancellationToken ct)
    {
        var stamp = _clock().UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        var path = Path.Combine(_options.BackupDirectory, $"{Path.GetFileNameWithoutExtension(_options.DatabasePath)}.v{fromVersion}.{stamp}.db");
        try
        {
            Directory.CreateDirectory(_options.BackupDirectory);   // dentro del try: si falla, también debe abortar la migración con un mensaje claro
            // VACUUM INTO produce una copia consistente y compacta (incluye lo que aún esté en el WAL). No puede ir dentro de una transacción.
            await connection.ExecuteAsync(new CommandDefinition("VACUUM INTO @path;", new { path }, cancellationToken: ct)).ConfigureAwait(false);
            if (!File.Exists(path) || new FileInfo(path).Length == 0) throw new IOException("La copia de seguridad quedó vacía.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Sin copia no se migra: es la red de seguridad frente a una migración mal escrita.
            throw new MigrationException(fromVersion, $"No se pudo hacer la copia de seguridad previa a la migración ({ex.Message}); no se migró nada.", null, ex);
        }
        _logger.LogInformation("Copia de seguridad previa a la migración: {Path}", path);
        return path;
    }

    private async Task ApplyAsync(SqliteConnection connection, Migration migration, string? backupPath, CancellationToken ct)
    {
        // Fuera de la transacción: dentro de ella este PRAGMA no hace nada.
        await ExecuteAsync(connection, "PRAGMA foreign_keys = OFF;", null, ct).ConfigureAwait(false);
        try
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                await ExecuteAsync(connection, migration.Sql, transaction, ct).ConfigureAwait(false);

                var violations = await ForeignKeyViolationsAsync(connection, transaction, ct).ConfigureAwait(false);
                if (violations.Count > 0)
                    throw new InvalidOperationException($"la migración dejó referencias rotas (PRAGMA foreign_key_check): {string.Join("; ", violations)}");

                await ExecuteAsync(connection, $"PRAGMA user_version = {migration.Version};", transaction, ct).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw new MigrationException(migration.Version,
                    $"Falló la migración {migration.Version} ({migration.Name}) y se revirtió: {ex.Message}", backupPath, ex);
            }
        }
        finally
        {
            // SIEMPRE se reactiva, también si la migración falló.
            await ExecuteAsync(connection, "PRAGMA foreign_keys = ON;", null, CancellationToken.None).ConfigureAwait(false);
            var on = await connection.ExecuteScalarAsync<long>("PRAGMA foreign_keys;").ConfigureAwait(false);
            if (on != 1) _logger.LogCritical("No se pudo reactivar foreign_keys tras la migración {Version}", migration.Version);
        }
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, SqliteTransaction? transaction, CancellationToken ct) =>
        await connection.ExecuteAsync(new CommandDefinition(sql, transaction: transaction, cancellationToken: ct)).ConfigureAwait(false);

    private static async Task<List<string>> ForeignKeyViolationsAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        const int Max = 20;
        var found = new List<string>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA foreign_key_check;";
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (found.Count == Max) { found.Add("…"); break; }
            found.Add($"{reader.GetString(0)} fila {(reader.IsDBNull(1) ? "?" : reader.GetInt64(1).ToString(CultureInfo.InvariantCulture))} → {reader.GetString(2)}");
        }
        return found;
    }
}