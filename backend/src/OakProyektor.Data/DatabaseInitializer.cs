using Dapper;
using Microsoft.Extensions.Logging;
using OakProyektor.Data.Migrations;

namespace OakProyektor.Data;

/// <summary>La inicialización de la BD falló. <c>Message</c> es apto para mostrarlo al operador.</summary>
public sealed class DatabaseInitializationException(string message, string? backupPath, Exception? inner = null) : InvalidOperationException(message, inner)
{
    /// <summary>Copia previa a la migración, si llegó a hacerse (los datos originales están ahí).</summary>
    public string? BackupPath { get; } = backupPath;
}

public sealed class DatabaseInitializer : IDatabaseInitializer
{
    private static readonly IReadOnlyList<Migration> AllMigrations = Migration.LoadEmbedded(typeof(DatabaseInitializer).Assembly);

    private readonly DatabaseOptions _options;
    private readonly ISqliteConnectionFactory _connections;
    private readonly ILogger<DatabaseInitializer> _logger;
    private readonly MigrationRunner _runner;

    public DatabaseInitializer(DatabaseOptions options, ISqliteConnectionFactory connections, ILogger<DatabaseInitializer> logger, ILoggerFactory? loggerFactory = null)
    {
        _options = options; _connections = connections; _logger = logger;
        _runner = new MigrationRunner(AllMigrations, options, (loggerFactory ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance).CreateLogger<MigrationRunner>());
    }

    /// <summary>Versión de esquema más reciente que conoce este ensamblado.</summary>
    public static int LatestSchemaVersion => AllMigrations[^1].Version;

    public async Task<MigrationRunResult> InitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(_options.DatabasePath));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

            var mode = await connection.ExecuteScalarAsync<string>(
                new CommandDefinition("PRAGMA journal_mode = WAL;", cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
                _logger.LogWarning("No se pudo activar el modo WAL; modo actual: {Mode}", mode);

            return await _runner.RunAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new DatabaseInitializationException(ex.Message, (ex as MigrationException)?.BackupPath, ex);
        }
    }
}