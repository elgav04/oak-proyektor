using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace OakProyektor.Data.Backups;

/// <param name="RestoredFrom">La copia que se restauró.</param>
/// <param name="PreviousSavedAs">Dónde quedó la base que había (null si no había ninguna).</param>
/// <param name="Verification">Comprobación de integridad de la copia ANTES de restaurar.</param>
public sealed record RestoreResult(string RestoredFrom, string? PreviousSavedAs, BackupVerification Verification);

/// <summary>
/// Restauración SEGURA de una copia. Garantías (cada una con test):
/// <list type="number">
/// <item>La copia se comprueba con <c>PRAGMA integrity_check</c> ANTES de tocar nada; si falla, no se hace nada.</item>
/// <item>Una copia de un esquema MÁS NUEVO que esta aplicación se rechaza (no se sabría leer).</item>
/// <item>La base actual NUNCA se sobrescribe sin guardarla antes con otro nombre (<c>oak-proyektor.before-restore.{sello}.db</c>, en la carpeta de respaldos). Si la actual está
/// dañada y no se puede copiar con <c>VACUUM INTO</c>, se copian sus archivos tal cual (con <c>-wal</c> y <c>-shm</c>); si ni eso se puede, NO se restaura.</item>
/// <item>El <c>-wal</c> y el <c>-shm</c> de la base vieja se retiran: aplicados a la restaurada la corromperían.</item>
/// <item>La sustitución es un renombrado desde un temporal en la MISMA carpeta (atómico en el mismo volumen): nunca queda un archivo a medias.</item>
/// </list>
/// NO comprueba que la aplicación esté cerrada: lo hace quien llama (la consola, con la instancia única).
/// </summary>
public sealed class DatabaseRestorer(DatabaseOptions options, IBackupVerifier? verifier = null, TimeProvider? time = null, ILogger? logger = null)
{
    private readonly IBackupVerifier _verifier = verifier ?? new SqliteBackupVerifier();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public async Task<RestoreResult> RestoreAsync(string backupPath, CancellationToken cancellationToken)
    {
        var source = Path.GetFullPath(backupPath);
        var target = Path.GetFullPath(options.DatabasePath);
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase)) throw new BackupException("La copia indicada es la propia base de datos actual.");
        if (!File.Exists(source)) throw new BackupException($"No existe la copia «{source}».");

        var verification = await _verifier.VerifyAsync(source, cancellationToken).ConfigureAwait(false);
        if (!verification.Ok)
            throw new BackupException($"La copia no pasó la comprobación de integridad (PRAGMA integrity_check): {verification.Detail}. No se cambió nada.");
        if (verification.SchemaVersion > DatabaseInitializer.LatestSchemaVersion)
            throw new BackupException($"La copia es de una versión más nueva del esquema ({verification.SchemaVersion}) que la de esta aplicación ({DatabaseInitializer.LatestSchemaVersion}). No se cambió nada.");

        var directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        var temp = target + ".restoring";
        DeleteIfExists(temp);

        string? saved = null;
        try
        {
            // 1. La copia se deja junto a la base (mismo volumen) para poder renombrarla de golpe; si no cabe en disco, falla AQUÍ, antes de tocar nada.
            File.Copy(source, temp, overwrite: true);

            // 2. Guardar la base actual con otro nombre.
            if (File.Exists(target)) saved = await SaveCurrentAsync(target, cancellationToken).ConfigureAwait(false);

            SqliteConnection.ClearAllPools();   // suelta las conexiones agrupadas de ESTE proceso: sostienen el -wal abierto (la consola corre en un proceso nuevo y no tiene ninguna)

            // 3. Retirar el WAL/SHM de la base vieja y colocar la restaurada.
            DeleteIfExists(target + "-wal");
            DeleteIfExists(target + "-shm");
            File.Move(temp, target, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DeleteIfExists(temp);
            throw new BackupException($"No se pudo restaurar ({ex.Message}). " + (saved is null ? "No se cambió nada." : $"La base anterior está a salvo en {saved}."), ex);
        }

        // 4. Comprobar lo que quedó en su sitio.
        var after = await _verifier.VerifyAsync(target, cancellationToken).ConfigureAwait(false);
        if (!after.Ok)
            throw new BackupException($"La base restaurada no pasó la comprobación de integridad: {after.Detail}. La anterior está en {saved ?? "(no había)"}.");
        _logger.LogWarning("Base de datos restaurada desde {Source}; la anterior se guardó en {Saved}", source, saved ?? "(no había)");
        return new RestoreResult(source, saved, verification);
    }

    /// <summary>Guarda la base actual: <c>VACUUM INTO</c> si se puede abrir (incluye lo pendiente del WAL); si está dañada, copia de archivos. Lanza si ninguna funciona.</summary>
    private async Task<string> SaveCurrentAsync(string target, CancellationToken ct)
    {
        var directory = options.BackupDirectory;
        Directory.CreateDirectory(directory);
        var stamp = _time.GetUtcNow().ToString("yyyyMMdd'T'HHmmssfff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var name = $"{BackupCatalog.BeforeRestorePrefix}{stamp}";
        var path = Path.Combine(directory, name + ".db");
        for (var n = 2; File.Exists(path); n++) path = Path.Combine(directory, $"{name}-{n}.db");   // nunca se pisa una copia

        try
        {
            await using var connection = await SqliteConnectionFactory.OpenFileReadOnlyAsync(target, ct).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition("VACUUM INTO @path;", new { path }, cancellationToken: ct)).ConfigureAwait(false);
            return path;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "La base actual no se pudo copiar con VACUUM INTO; se copian sus archivos tal cual");
            DeleteIfExists(path);
        }

        try
        {
            File.Copy(target, path, overwrite: false);
            if (File.Exists(target + "-wal")) File.Copy(target + "-wal", path + "-wal", overwrite: false);
            if (File.Exists(target + "-shm")) File.Copy(target + "-shm", path + "-shm", overwrite: false);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DeleteIfExists(path); DeleteIfExists(path + "-wal"); DeleteIfExists(path + "-shm");
            throw new BackupException($"No se pudo guardar la base actual antes de restaurar ({ex.Message}). No se cambió nada.", ex);
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
