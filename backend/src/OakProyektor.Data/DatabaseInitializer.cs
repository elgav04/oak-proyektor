using Dapper;
using Microsoft.Extensions.Logging;
using OakProyektor.Data.Migrations;

namespace OakProyektor.Data;

public sealed class DatabaseInitializer(
    DatabaseOptions options,
    ISqliteConnectionFactory connections,
    ILogger<DatabaseInitializer> logger) : IDatabaseInitializer
{
    private static readonly IReadOnlyList<Migration> AllMigrations = Migration.LoadEmbedded(typeof(DatabaseInitializer).Assembly);

    /// <summary>Versión de esquema más reciente que conoce este ensamblado.</summary>
    public static int LatestSchemaVersion => AllMigrations[^1].Version;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(options.DatabasePath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        var mode = await connection.ExecuteScalarAsync<string>(
            new CommandDefinition("PRAGMA journal_mode = WAL;", cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
            logger.LogWarning("No se pudo activar el modo WAL; modo actual: {Mode}", mode);

        var current = (int)await connection.ExecuteScalarAsync<long>(
            new CommandDefinition("PRAGMA user_version;", cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (current > LatestSchemaVersion)
            throw new InvalidOperationException(
                $"La base de datos tiene esquema v{current}, más nuevo que esta versión de la aplicación (v{LatestSchemaVersion}). Actualiza la aplicación.");

        foreach (var migration in AllMigrations.Where(m => m.Version > current))
        {
            // Cada migración y su cambio de user_version van en una sola transacción: o se aplica completa o nada.
            await using var transaction = (Microsoft.Data.Sqlite.SqliteTransaction)
                await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition(migration.Sql, transaction: transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition($"PRAGMA user_version = {migration.Version};", transaction: transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Migración {Version} ({Name}) aplicada", migration.Version, migration.Name);
        }
    }
}
