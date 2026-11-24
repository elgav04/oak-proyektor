using Microsoft.Data.Sqlite;

namespace OakProyektor.Data;

public sealed class SqliteConnectionFactory(DatabaseOptions options) : ISqliteConnectionFactory
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = options.DatabasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        // Microsoft.Data.Sqlite ejecuta PRAGMA foreign_keys = ON al abrir cada conexión.
        ForeignKeys = true,
    }.ToString();

    /// <summary>
    /// Abre un archivo SQLite CUALQUIERA (un respaldo, la base actual para copiarla) SOLO LECTURA y sin agrupar conexiones (<c>Pooling=False</c>: al cerrarla el archivo queda libre y se
    /// puede borrar o mover en Windows). Nunca crea el archivo. Está aquí porque solo esta clase crea <c>SqliteConnection</c> (ver <c>SourceRulesTests</c>); no activa los PRAGMA de la
    /// fábrica porque no escribe nada.
    /// </summary>
    public static async Task<SqliteConnection> OpenFileReadOnlyAsync(string path, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            // recursive_triggers es un ajuste POR CONEXIÓN (no se guarda en el archivo). Con él activo, los borrados que hace
            // internamente INSERT OR REPLACE disparan los triggers DELETE; sin él, el índice FTS queda desincronizado (hallazgo I2).
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA recursive_triggers = ON;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>
/// Fábrica de conexiones SOLO LECTURA para las órdenes de consola que no escriben (<c>--list-bibles</c>, <c>--show-verse</c>, <c>--verify-bible</c>): nunca crea el archivo
/// ni aplica migraciones, y cualquier intento de escribir falla. Está en este archivo porque solo aquí se crean <c>SqliteConnection</c> (ver <c>SourceRulesTests</c>).
/// </summary>
public sealed class ReadOnlySqliteConnectionFactory(DatabaseOptions options) : ISqliteConnectionFactory
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder { DataSource = options.DatabasePath, Mode = SqliteOpenMode.ReadOnly }.ToString();

    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
