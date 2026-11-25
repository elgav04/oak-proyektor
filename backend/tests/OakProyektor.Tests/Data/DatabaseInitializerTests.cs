using Dapper;
using Microsoft.Data.Sqlite;

namespace OakProyektor.Tests.Data;

public sealed class DatabaseInitializerTests : IDisposable
{
    private readonly TestDatabase _db = new();
    public void Dispose() => _db.Dispose();

    private static readonly string[] ExpectedTables =
    [
        "Bibles", "Books", "Verses", "Songs", "SongSlides", "Playlists", "PlaylistItems", "Presentations", "PresentationSlides", "VersesFts", "SongsFts", "SongSections", "SongArrangements", "SongArrangementItems", "SongRevisions",
    ];

    [Fact]
    public async Task Creates_database_from_scratch_with_full_schema()
    {
        Assert.False(File.Exists(_db.Options.DatabasePath));

        await _db.InitializeAsync();

        Assert.True(File.Exists(_db.Options.DatabasePath));
        Assert.EndsWith("oak-proyektor.db", _db.Options.DatabasePath);
        await using var c = await _db.OpenAsync();
        var tables = (await c.QueryAsync<string>("SELECT name FROM sqlite_master WHERE type = 'table'")).ToHashSet();
        foreach (var table in ExpectedTables) Assert.Contains(table, tables);
        Assert.Equal((long)OakProyektor.Data.DatabaseInitializer.LatestSchemaVersion, await c.ExecuteScalarAsync<long>("PRAGMA user_version"));
        Assert.Equal(11, OakProyektor.Data.DatabaseInitializer.LatestSchemaVersion);
    }

    [Fact]
    public async Task Every_connection_has_recursive_triggers_on()
    {
        await _db.InitializeAsync();
        await using var c = await _db.OpenAsync();
        Assert.Equal(1L, await c.ExecuteScalarAsync<long>("PRAGMA recursive_triggers"));   // sin esto, INSERT OR REPLACE desincroniza el FTS (I2)
    }

    [Fact]
    public async Task Uses_wal_and_foreign_keys_on_every_connection()
    {
        await _db.InitializeAsync();

        await using var c = await _db.OpenAsync();
        Assert.Equal("wal", await c.ExecuteScalarAsync<string>("PRAGMA journal_mode"), ignoreCase: true);
        Assert.Equal(1L, await c.ExecuteScalarAsync<long>("PRAGMA foreign_keys"));
    }

    [Fact]
    public async Task Migration_is_idempotent_and_keeps_data_when_reopened()
    {
        await _db.InitializeAsync();
        await using (var c = await _db.OpenAsync())
            await TestDatabase.InsertVerseAsync(c, "Texto sintetico de prueba");

        await _db.InitializeAsync();
        await _db.InitializeAsync();

        await using var c2 = await _db.OpenAsync();
        Assert.Equal((long)OakProyektor.Data.DatabaseInitializer.LatestSchemaVersion, await c2.ExecuteScalarAsync<long>("PRAGMA user_version"));
        Assert.Equal(1L, await c2.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Verses"));
        Assert.Equal(1L, await c2.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM VersesFts WHERE VersesFts MATCH 'sintetico'"));
    }

    [Fact]
    public async Task Refuses_database_newer_than_the_application()
    {
        await _db.InitializeAsync();
        await using (var c = await _db.OpenAsync())
            await c.ExecuteAsync("PRAGMA user_version = 999");

        var ex = await Assert.ThrowsAsync<OakProyektor.Data.DatabaseInitializationException>(_db.InitializeAsync);
        Assert.Contains("v999", ex.Message);
        Assert.Null(ex.BackupPath);   // no se llegó a migrar nada: no hay copia
    }

    [Fact]
    public async Task Rejects_invalid_presentation_source_type()
    {
        await _db.InitializeAsync();
        await using var c = await _db.OpenAsync();

        await c.ExecuteAsync("INSERT INTO Presentations (Title, SourceType) VALUES ('ok1', 'CUSTOM'), ('ok2', 'PPTX')");
        await Assert.ThrowsAsync<SqliteException>(() =>
            c.ExecuteAsync("INSERT INTO Presentations (Title, SourceType) VALUES ('mal', 'PDF')"));
    }

    [Fact]
    public async Task Enforces_foreign_keys()
    {
        await _db.InitializeAsync();
        await using var c = await _db.OpenAsync();

        await Assert.ThrowsAsync<SqliteException>(() =>
            c.ExecuteAsync("INSERT INTO Books (BibleId, BookNumber, Name, Testament) VALUES (12345, 1, 'huerfano', 'OT')"));
    }
}

