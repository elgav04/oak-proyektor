using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using OakProyektor.Data;
using OakProyektor.Data.Migrations;
using OakProyektor.Tests.Ipc;

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

    private static readonly Migration Agrega = new(2, "Agrega", "CREATE TABLE Agregada (X INTEGER);");
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private MigrationRunner RunnerWith(MigrationRunnerHooks hooks, ListLogger<MigrationRunner>? log = null, params Migration[] migrations) =>
        new(migrations, _db.Options, log ?? new ListLogger<MigrationRunner>(), () => FixedNow, hooks);

    [Fact]
    public async Task A_half_written_backup_is_deleted_when_the_copy_fails_and_nothing_is_migrated()
    {
        await using var c = await SeedVersion1Async();
        // Simula un disco lleno: la copia empieza a escribirse (queda un archivo truncado) y entonces falla.
        var hooks = new MigrationRunnerHooks
        {
            WriteBackup = async (_, path, _) =>
            {
                await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
                throw new IOException("No hay espacio suficiente en el disco.");
            },
        };

        var ex = await Assert.ThrowsAsync<MigrationException>(() => RunnerWith(hooks, null, First, Agrega).RunAsync(c, CancellationToken.None));

        Assert.Contains("copia de seguridad", ex.Message);
        Assert.Contains("No hay espacio", ex.Message);
        Assert.Null(ex.BackupPath);                                                           // no hay copia que ofrecer
        Assert.Empty(Directory.GetFiles(_db.Options.BackupDirectory));                        // el archivo truncado NO se dejó
        Assert.Equal(1L, await c.ExecuteScalarAsync<long>("PRAGMA user_version"));            // y no se migró
    }

    [Fact]
    public async Task A_backup_that_comes_out_empty_is_also_deleted()
    {
        await using var c = await SeedVersion1Async();
        var hooks = new MigrationRunnerHooks { WriteBackup = async (_, path, _) => await File.WriteAllBytesAsync(path, []) };

        await Assert.ThrowsAsync<MigrationException>(() => RunnerWith(hooks, null, First, Agrega).RunAsync(c, CancellationToken.None));

        Assert.Empty(Directory.GetFiles(_db.Options.BackupDirectory));
    }

    [Fact]
    public async Task A_cancelled_copy_leaves_no_partial_file_either_and_stays_a_cancellation()
    {
        await using var c = await SeedVersion1Async();
        var hooks = new MigrationRunnerHooks
        {
            WriteBackup = async (_, path, _) => { await File.WriteAllBytesAsync(path, [9]); throw new OperationCanceledException(); },
        };

        await Assert.ThrowsAsync<OperationCanceledException>(() => RunnerWith(hooks, null, First, Agrega).RunAsync(c, CancellationToken.None));

        Assert.Empty(Directory.GetFiles(_db.Options.BackupDirectory));
    }

    [Fact]
    public async Task A_failed_copy_never_deletes_a_file_it_did_not_create()
    {
        await using var c = await SeedVersion1Async();
        // Mismo reloj fijo → mismo nombre: ya hay allí un archivo anterior (p. ej. una copia buena de otra ejecución). No es nuestro: no se toca.
        Directory.CreateDirectory(_db.Options.BackupDirectory);
        var stamp = FixedNow.UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var existing = Path.Combine(_db.Options.BackupDirectory, $"oak-proyektor.v1.{stamp}.db");
        await File.WriteAllTextAsync(existing, "copia anterior que no es nuestra");
        var hooks = new MigrationRunnerHooks { WriteBackup = (_, _, _) => throw new IOException("falló") };

        await Assert.ThrowsAsync<MigrationException>(() => RunnerWith(hooks, null, First, Agrega).RunAsync(c, CancellationToken.None));

        Assert.Equal("copia anterior que no es nuestra", await File.ReadAllTextAsync(existing));
    }

    [Fact]
    public async Task A_failure_re_enabling_foreign_keys_does_not_mask_the_migrations_own_error()
    {
        await using var c = await SeedVersion1Async();
        var log = new ListLogger<MigrationRunner>();
        var hooks = new MigrationRunnerHooks { EnableForeignKeys = _ => throw new InvalidOperationException("la conexión se rompió al reactivar") };
        var bad = new Migration(2, "Rota", "SELECT * FROM tabla_que_no_existe;");

        var ex = await Assert.ThrowsAsync<MigrationException>(() => RunnerWith(hooks, log, First, bad).RunAsync(c, CancellationToken.None));

        // Quien llama recibe la causa REAL (la migración), no la excepción del PRAGMA.
        Assert.Equal(2, ex.Version);
        Assert.Contains("Falló la migración 2", ex.Message);
        Assert.Contains("tabla_que_no_existe", ex.Message);
        Assert.DoesNotContain("al reactivar", ex.Message);
        Assert.NotNull(ex.BackupPath);
        // ...y el fallo secundario no se pierde: queda registrado como crítico.
        Assert.Contains(log.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Critical && e.Message.Contains("reactivación de foreign_keys"));
    }

    [Fact]
    public async Task A_failure_re_enabling_foreign_keys_after_a_successful_migration_is_logged_without_failing_the_committed_migration()
    {
        await using var c = await SeedVersion1Async();
        var log = new ListLogger<MigrationRunner>();
        var hooks = new MigrationRunnerHooks { EnableForeignKeys = _ => throw new InvalidOperationException("boom") };

        var result = await RunnerWith(hooks, log, First, Agrega).RunAsync(c, CancellationToken.None);

        Assert.Equal(2, result.ToVersion);                                                    // la migración ya estaba confirmada
        Assert.Equal(2L, await c.ExecuteScalarAsync<long>("PRAGMA user_version"));
        Assert.Contains(log.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Critical);
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
        Assert.Equal([2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13], result.Applied);
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