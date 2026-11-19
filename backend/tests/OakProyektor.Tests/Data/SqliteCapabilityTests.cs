using Dapper;
using Microsoft.Data.Sqlite;

namespace OakProyektor.Tests.Data;

/// <summary>Las migraciones y el runner dependen de estas funciones de SQLite; si una actualización del motor las perdiera, estos tests lo dirían.</summary>
public sealed class SqliteCapabilityTests : IDisposable
{
    private readonly TestDatabase _db = new();
    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task The_embedded_engine_supports_vacuum_into_generated_columns_drop_column_and_upsert()
    {
        await using var c = await _db.OpenAsync();
        var version = Version.Parse((await c.ExecuteScalarAsync<string>("SELECT sqlite_version()"))!);
        Assert.True(version >= new Version(3, 35), $"SQLite {version}: se necesita >= 3.35 (DROP COLUMN, upsert, columnas generadas)");

        await c.ExecuteAsync("CREATE TABLE T (Id INTEGER PRIMARY KEY, A TEXT, B TEXT GENERATED ALWAYS AS (A || '!') VIRTUAL, Old TEXT)");
        await c.ExecuteAsync("INSERT INTO T (Id, A, Old) VALUES (1, 'x', 'o') ON CONFLICT(Id) DO UPDATE SET A = excluded.A");
        Assert.Equal("x!", await c.ExecuteScalarAsync<string>("SELECT B FROM T"));
        await c.ExecuteAsync("ALTER TABLE T DROP COLUMN Old");

        var backup = Path.Combine(Path.GetTempPath(), $"oak-capability-{Guid.NewGuid():N}.db");
        try
        {
            await c.ExecuteAsync("VACUUM INTO @path", new { path = backup });   // el nombre de archivo se puede pasar como parámetro
            Assert.True(new FileInfo(backup).Length > 0);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(backup);
        }
    }
}