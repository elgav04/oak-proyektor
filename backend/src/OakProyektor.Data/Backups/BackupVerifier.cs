using Dapper;
using Microsoft.Data.Sqlite;

namespace OakProyektor.Data.Backups;

/// <param name="Ok"><c>PRAGMA integrity_check</c> devolvió «ok».</param>
/// <param name="Detail">«ok», o las primeras líneas de lo que encontró (posiciones de páginas e índices, nunca texto bíblico: son mensajes de SQLite sobre la estructura).</param>
/// <param name="SchemaVersion"><c>PRAGMA user_version</c> de la copia (0 si no se pudo leer).</param>
public sealed record BackupVerification(bool Ok, string Detail, int SchemaVersion);

public interface IBackupVerifier
{
    Task<BackupVerification> VerifyAsync(string path, CancellationToken cancellationToken);
}

/// <summary>Verifica una copia abriéndola SOLO LECTURA y ejecutando <c>PRAGMA integrity_check</c>. Nunca lanza por un archivo dañado: lo informa.</summary>
public sealed class SqliteBackupVerifier : IBackupVerifier
{
    public const int MaxDetailLines = 5;

    public async Task<BackupVerification> VerifyAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return new BackupVerification(false, "el archivo no existe", 0);
        if (new FileInfo(path).Length == 0) return new BackupVerification(false, "el archivo está vacío", 0);
        try
        {
            await using var connection = await SqliteConnectionFactory.OpenFileReadOnlyAsync(path, cancellationToken).ConfigureAwait(false);
            var rows = (await connection.QueryAsync<string>(new CommandDefinition("PRAGMA integrity_check;", cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();
            var version = (int)await connection.ExecuteScalarAsync<long>(new CommandDefinition("PRAGMA user_version;", cancellationToken: cancellationToken)).ConfigureAwait(false);
            var ok = rows.Count == 1 && string.Equals(rows[0], "ok", StringComparison.OrdinalIgnoreCase);
            return new BackupVerification(ok, ok ? "ok" : string.Join(" | ", rows.Take(MaxDetailLines)) + (rows.Count > MaxDetailLines ? " | …" : ""), version);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Un archivo que no es una base SQLite («file is not a database») o está truncado falla al abrirse o al leer: es exactamente lo que se quiere detectar.
            return new BackupVerification(false, ex.Message, 0);
        }
    }
}
