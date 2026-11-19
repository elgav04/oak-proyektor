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