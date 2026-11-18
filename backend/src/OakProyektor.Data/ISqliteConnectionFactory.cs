using Microsoft.Data.Sqlite;

namespace OakProyektor.Data;

public interface ISqliteConnectionFactory
{
    /// <summary>Abre una conexión nueva con <c>foreign_keys = ON</c>. El llamador la libera.</summary>
    Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken);
}
