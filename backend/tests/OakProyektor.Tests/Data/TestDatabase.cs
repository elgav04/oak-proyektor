using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using OakProyektor.Data;

namespace OakProyektor.Tests.Data;

/// <summary>BD SQLite en un archivo temporal, aislada por prueba.</summary>
public sealed class TestDatabase : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "oak-proyektor-tests", Guid.NewGuid().ToString("N"));

    public TestDatabase()
    {
        Options = DatabaseOptions.ForDataDirectory(Path.Combine(_directory, "data"));
        Connections = new SqliteConnectionFactory(Options);
        Initializer = new DatabaseInitializer(Options, Connections, NullLogger<DatabaseInitializer>.Instance);
    }

    public DatabaseOptions Options { get; }
    public ISqliteConnectionFactory Connections { get; }
    public DatabaseInitializer Initializer { get; }

    public Task InitializeAsync() => Initializer.InitializeAsync(CancellationToken.None);

    public async Task<SqliteConnection> OpenAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Options.DatabasePath)!);   // el inicializador real lo hace; los tests que abren antes, no
        return await Connections.OpenAsync(CancellationToken.None);
    }

    /// <summary>Crea Bible → Book → Verse sintéticos y devuelve (bibleId, bookId, verseId).</summary>
    public static async Task<(long BibleId, long BookId, long VerseId)> InsertVerseAsync(SqliteConnection c, string text, string abbreviation = "TST")
    {
        var bibleId = await c.ExecuteScalarAsync<long>("INSERT INTO Bibles (Abbreviation, Name, Language) VALUES (@a, 'Biblia de prueba', 'es'); SELECT last_insert_rowid();", new { a = abbreviation });
        var bookId = await c.ExecuteScalarAsync<long>("INSERT INTO Books (BibleId, BookNumber, Name, Testament) VALUES (@b, 1, 'Libro uno', 'OT'); SELECT last_insert_rowid();", new { b = bibleId });
        var verseId = await c.ExecuteScalarAsync<long>("INSERT INTO Verses (BookId, Chapter, VerseNumber, Text) VALUES (@k, 1, 1, @t); SELECT last_insert_rowid();", new { k = bookId, t = text });
        return (bibleId, bookId, verseId);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();   // libera el archivo antes de borrar (incluye -wal y -shm)
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { /* limpieza best-effort */ }
    }
}
