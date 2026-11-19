using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using OakProyektor.Data;
using OakProyektor.Data.Migrations;

namespace OakProyektor.Tests.Data;

public sealed class MigrationRunnerTests : IDisposable
{
    private readonly TestDatabase _db = new();
    public void Dispose() => _db.Dispose();

    private static readonly IReadOnlyList<Migration> Embedded = Migration.LoadEmbedded(typeof(DatabaseInitializer).Assembly);
    private static Migration First => Embedded[0];

    private MigrationRunner Runner(params Migration[] migrations) =>
        new(migrations, _db.Options, NullLogger<MigrationRunner>.Instance);

    /// <summary>Reconstruye la tabla PADRE Songs con el patrón recomendado: crear nueva, copiar, DROP de la vieja, renombrar la nueva.</summary>
    private const string RebuildSongsSql = """
        CREATE TABLE Songs_new (
            Id INTEGER PRIMARY KEY, Title TEXT NOT NULL, Author TEXT, Copyright TEXT, CcliNumber TEXT,
            CreatedAt TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')), Extra TEXT);
        INSERT INTO Songs_new (Id, Title, Author, Copyright, CcliNumber, CreatedAt) SELECT Id, Title, Author, Copyright, CcliNumber, CreatedAt FROM Songs;
        DROP TABLE Songs;
        ALTER TABLE Songs_new RENAME TO Songs;
        """;

    /// <summary>Crea una BD en v1 con una canción, dos diapositivas y una Biblia con un verso.</summary>
    private async Task<SqliteConnection> SeedVersion1Async()
    {
        var c = await _db.OpenAsync();
        await Runner(First).RunAsync(c, CancellationToken.None);
        await c.ExecuteAsync("INSERT INTO Songs (Id, Title) VALUES (1, 'Canción de prueba')");
        await c.ExecuteAsync("INSERT INTO SongSlides (SongId, SequenceIndex, Content) VALUES (1, 0, 'estrofa uno'), (1, 1, 'estrofa dos')");
        await TestDatabase.InsertVerseAsync(c, "Texto sintético");
        return c;
    }

    private static async Task<long> CountAsync(SqliteConnection c, string table) => await c.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {table}");

    [Fact]
    public async Task Rebuilding_a_parent_table_does_not_delete_its_children()
    {
        await using var c = await SeedVersion1Async();

        var result = await Runner(First, new Migration(2, "RebuildSongs", RebuildSongsSql)).RunAsync(c, CancellationToken.None);

        Assert.Equal(2, result.ToVersion);
        Assert.Equal(1L, await CountAsync(c, "Songs"));
        Assert.Equal(2L, await CountAsync(c, "SongSlides"));                                       // las hijas siguen ahí
        Assert.Equal(2L, await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM SongSlides WHERE SongId = 1"));
        Assert.Empty(await c.QueryAsync("PRAGMA foreign_key_check"));                              // sin referencias rotas
        Assert.Equal(1L, await c.ExecuteScalarAsync<long>("PRAGMA foreign_keys"));                 // reactivadas al terminar
        Assert.Equal(2L, await c.ExecuteScalarAsync<long>("PRAGMA user_version"));
    }

    [Fact]
    public async Task Control_the_same_rebuild_with_foreign_keys_on_silently_deletes_the_children()
    {
        // Demuestra POR QUÉ el runner apaga las claves foráneas: este es el comportamiento de SQLite si no lo hiciera.
        await using var c = await SeedVersion1Async();

        await using (var tx = await c.BeginTransactionAsync())
        {
            await c.ExecuteAsync(RebuildSongsSql, transaction: tx);
            await tx.CommitAsync();
        }

        Assert.Equal(1L, await CountAsync(c, "Songs"));
        Assert.Equal(0L, await CountAsync(c, "SongSlides"));   // DROP TABLE Songs borró en cascada las diapositivas
    }

    [Fact]
    public async Task A_migration_that_leaves_dangling_references_is_rejected_and_rolled_back()
    {
        await using var c = await SeedVersion1Async();
        var bad = new Migration(2, "Huerfanas", "INSERT INTO SongSlides (SongId, SequenceIndex, Content) VALUES (9999, 0, 'huérfana');");

        var ex = await Assert.ThrowsAsync<MigrationException>(() => Runner(First, bad).RunAsync(c, CancellationToken.None));

        Assert.Equal(2, ex.Version);
        Assert.Contains("foreign_key_check", ex.Message);
        Assert.Equal(1L, await c.ExecuteScalarAsync<long>("PRAGMA user_version"));            // sigue en v1
        Assert.Equal(2L, await CountAsync(c, "SongSlides"));                                  // la fila huérfana se revirtió
        Assert.Equal(1L, await c.ExecuteScalarAsync<long>("PRAGMA foreign_keys"));            // y las claves foráneas vuelven a estar activas
        Assert.NotNull(ex.BackupPath);
        Assert.True(File.Exists(ex.BackupPath));
    }

    [Fact]
    public async Task A_failing_migration_is_rolled_back_completely_and_foreign_keys_come_back_on()
    {
        await using var c = await SeedVersion1Async();
        var bad = new Migration(2, "Rota", "CREATE TABLE Temporal (X INTEGER); SELECT * FROM tabla_que_no_existe;");

        var ex = await Assert.ThrowsAsync<MigrationException>(() => Runner(First, bad).RunAsync(c, CancellationToken.None));

        Assert.Contains("Falló la migración 2", ex.Message);
        Assert.Equal(0L, await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM sqlite_master WHERE name = 'Temporal'"));   // nada a medias
        Assert.Equal(1L, await c.ExecuteScalarAsync<long>("PRAGMA user_version"));
        Assert.Equal(1L, await c.ExecuteScalarAsync<long>("PRAGMA foreign_keys"));
    }

    [Fact]
    public async Task A_backup_is_made_before_migrating_and_it_holds_the_pre_migration_data()
    {
        await using var c = await SeedVersion1Async();

        var result = await Runner(First, new Migration(2, "Agrega", "CREATE TABLE Agregada (X INTEGER);")).RunAsync(c, CancellationToken.None);

        Assert.NotNull(result.BackupPath);
        Assert.StartsWith(_db.Options.BackupDirectory, result.BackupPath);
        Assert.Matches(@"oak-proyektor\.v1\.\d{8}T\d{9}Z\.db$", result.BackupPath);
        Assert.Single(Directory.GetFiles(_db.Options.BackupDirectory));

        // La copia es una BD válida en el estado ANTERIOR: v1, con los datos y sin la tabla que añadió la migración.
        await using var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = result.BackupPath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        await backup.OpenAsync();
        Assert.Equal(1L, await backup.ExecuteScalarAsync<long>("PRAGMA user_version"));
        Assert.Equal(2L, await backup.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM SongSlides"));
        Assert.Equal(0L, await backup.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM sqlite_master WHERE name = 'Agregada'"));
        Assert.Equal("ok", await backup.ExecuteScalarAsync<string>("PRAGMA integrity_check"));
    }

    [Fact]
    public async Task No_backup_for_a_brand_new_database_nor_when_already_up_to_date()
    {
        await using var c = await _db.OpenAsync();

        var first = await Runner(First).RunAsync(c, CancellationToken.None);   // v0 → v1: no hay nada que proteger
        var second = await Runner(First).RunAsync(c, CancellationToken.None);  // ya al día

        Assert.Null(first.BackupPath);
        Assert.Null(second.BackupPath);
        Assert.Empty(second.Applied);
        Assert.False(Directory.Exists(_db.Options.BackupDirectory));
    }

    [Fact]
    public async Task If_the_backup_cannot_be_made_nothing_is_migrated()
    {
        await using var c = await SeedVersion1Async();
        await File.WriteAllTextAsync(_db.Options.BackupDirectory, "un archivo donde debería haber una carpeta");   // la copia no podrá crearse

        var ex = await Assert.ThrowsAsync<MigrationException>(() =>
            Runner(First, new Migration(2, "Agrega", "CREATE TABLE Agregada (X INTEGER);")).RunAsync(c, CancellationToken.None));

        Assert.Contains("copia de seguridad", ex.Message);
        Assert.Equal(1L, await c.ExecuteScalarAsync<long>("PRAGMA user_version"));
        Assert.Equal(0L, await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM sqlite_master WHERE name = 'Agregada'"));
    }

    [Fact]
    public async Task Applied_migrations_report_the_versions_and_each_one_is_atomic_on_its_own()
    {
        await using var c = await SeedVersion1Async();
        var ok = new Migration(2, "Buena", "CREATE TABLE A2 (X INTEGER);");
        var bad = new Migration(3, "Mala", "CREATE TABLE A3 (X INTEGER); SELECT * FROM no_existe;");

        var ex = await Assert.ThrowsAsync<MigrationException>(() => Runner(First, ok, bad).RunAsync(c, CancellationToken.None));

        Assert.Equal(3, ex.Version);
        Assert.Equal(2L, await c.ExecuteScalarAsync<long>("PRAGMA user_version"));    // la 2 ya estaba confirmada; la 3 se revirtió
        Assert.Equal(1L, await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM sqlite_master WHERE name = 'A2'"));
        Assert.Equal(0L, await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM sqlite_master WHERE name = 'A3'"));
    }

    [Fact]
    public void The_migration_list_must_be_consecutive_from_one()
    {
        Assert.Throws<ArgumentException>(() => Runner(new Migration(2, "X", "SELECT 1;")));
        Assert.Throws<ArgumentException>(() => Runner(First, new Migration(3, "X", "SELECT 1;")));
        Assert.Throws<ArgumentException>(() => new MigrationRunner([], _db.Options, NullLogger<MigrationRunner>.Instance));
    }

    // ───────── el camino real: una BD v1 que se actualiza a la última versión con todas las migraciones embebidas ─────────

    [Fact]
    public async Task A_version_1_database_upgrades_to_the_latest_keeping_every_row_and_a_consistent_index()
    {
        await using (var c = await SeedVersion1Async())
        {
            await c.ExecuteAsync("INSERT INTO Playlists (Name, ServiceDate) VALUES ('Domingo', '2026-11-22')");   // ItemsJson = '[]' por defecto
        }

        var result = await _db.Initializer.InitializeAsync(CancellationToken.None);

        Assert.Equal((1, DatabaseInitializer.LatestSchemaVersion), (result.FromVersion, result.ToVersion));
        Assert.Equal([2, 3], result.Applied);
        Assert.NotNull(result.BackupPath);
        await using var d = await _db.OpenAsync();
        Assert.Equal(1L, await CountAsync(d, "Songs"));
        Assert.Equal(2L, await CountAsync(d, "SongSlides"));
        Assert.Equal(1L, await CountAsync(d, "Verses"));
        Assert.Equal(1L, await CountAsync(d, "Playlists"));
        Assert.Equal(0L, await CountAsync(d, "PlaylistItems"));
        Assert.DoesNotContain("ItemsJson", (await d.QueryAsync<string>("SELECT name FROM pragma_table_info('Playlists')")));
        Assert.Empty(await d.QueryAsync("PRAGMA foreign_key_check"));
        await d.ExecuteAsync("INSERT INTO VersesFts (VersesFts, rank) VALUES ('integrity-check', 1)");               // el índice FTS sigue consistente
        Assert.Equal(1L, await d.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM SongsFts WHERE SongsFts MATCH 'estrofa'"));
    }

    [Fact]
    public async Task The_playlist_migration_aborts_instead_of_losing_unconvertible_ItemsJson_data()
    {
        await using (var c = await SeedVersion1Async())
        {
            await c.ExecuteAsync("INSERT INTO Playlists (Name, ItemsJson) VALUES ('Con datos', '[{\"type\":\"song\",\"id\":1}]')");
        }

        var ex = await Assert.ThrowsAsync<DatabaseInitializationException>(() => _db.Initializer.InitializeAsync(CancellationToken.None));

        Assert.Contains("migración 3", ex.Message);
        Assert.Contains("ItemsJson", ex.Message);
        Assert.NotNull(ex.BackupPath);
        await using var d = await _db.OpenAsync();
        Assert.Equal(2L, await d.ExecuteScalarAsync<long>("PRAGMA user_version"));                       // la 2 se aplicó; la 3 se revirtió entera
        Assert.Equal("[{\"type\":\"song\",\"id\":1}]", await d.ExecuteScalarAsync<string>("SELECT ItemsJson FROM Playlists"));   // el dato sigue ahí
        Assert.Equal(0L, await d.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM sqlite_master WHERE name = 'PlaylistItems'"));
    }
}