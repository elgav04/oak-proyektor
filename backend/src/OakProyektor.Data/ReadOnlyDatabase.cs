using Dapper;
using Microsoft.Data.Sqlite;
using OakProyektor.Core.Database;

namespace OakProyektor.Data;

/// <summary>La base ya está lista y no se toca: para las órdenes de solo lectura.</summary>
public sealed class AlwaysReady : IDatabaseReadiness
{
    public DatabaseStatus Status { get; } = new(DatabaseState.Ready, null, null);
    public event Action<DatabaseStatus>? Changed { add { } remove { } }
    public Task WhenReadyAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Comprobación previa de las órdenes de solo lectura: la base debe EXISTIR y estar en la versión de esquema de esta aplicación. No crea nada ni aplica migraciones
/// (hacerlo con la aplicación gráfica abierta, o en una carpeta de datos equivocada, es justo lo que se evita).
/// </summary>
public static class ReadOnlyDatabase
{
    /// <returns>null si se puede leer; si no, un mensaje claro para el operador.</returns>
    public static async Task<string?> CheckAsync(DatabaseOptions options, string dataDirectory, CancellationToken cancellationToken)
    {
        var path = options.DatabasePath;
        if (!File.Exists(path))
            return $"No existe la base de datos en {path}. ¿Es la carpeta de datos correcta? Esta orden no crea ninguna (es de solo lectura). " +
                   $"Carpeta usada: {dataDirectory}; la variable OAK_DATA_DIR la cambia. Abre la aplicación una vez para crearla.";
        int version;
        try
        {
            await using var connection = await SqliteConnectionFactory.OpenFileReadOnlyAsync(path, cancellationToken).ConfigureAwait(false);
            version = (int)await connection.ExecuteScalarAsync<long>(new CommandDefinition("PRAGMA user_version;", cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            return $"No se puede leer la base de datos {path} ({ex.Message}). Esta orden es de solo lectura y no la repara.";
        }
        var latest = DatabaseInitializer.LatestSchemaVersion;
        if (version < latest)
            return $"La base de datos está en la versión {version} del esquema y esta aplicación espera la {latest}. Esta orden es de solo lectura y no la actualiza: " +
                   "abre la aplicación gráfica una vez (hace una copia previa y migra) y vuelve a ejecutarla.";
        if (version > latest)
            return $"La base de datos es de una versión más nueva del esquema ({version}) que la de esta aplicación ({latest}). Usa la aplicación más reciente.";
        return null;
    }
}
