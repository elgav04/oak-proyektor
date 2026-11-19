using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using OakProyektor.Core.Library;
using OakProyektor.Data;
using OakProyektor.Data.Migrations;

namespace OakProyektor.Tests.Data;

/// <summary>
/// Migración 4: los pasajes pasan de una clave foránea al libro (BookId) a una referencia canónica (BookNumber + abreviatura de la Biblia). Con datos
/// existentes se convierten sin perder nada; si hubiera datos que no se pueden convertir sin adivinar, se ABORTA y la BD queda intacta en v3.
/// </summary>
public sealed class PassageMigrationTests : IDisposable
{
    private readonly TestDatabase _db = new();
    public void Dispose() => _db.Dispose();

    private static readonly IReadOnlyList<Migration> Embedded = Migration.LoadEmbedded(typeof(DatabaseInitializer).Assembly);

    /// <summary>Una BD en la versión 3 (el esquema anterior: PlaylistItems con BookId), con una Biblia, una canción y un servicio.</summary>
    private async Task<(SqliteConnection Connection, long PlaylistId, long Book43, long Book1)> SeedVersion3Async()
    {
        var c = await _db.OpenAsync();
        await new MigrationRunner([.. Embedded.Take(3)], _db.Options, NullLogger<MigrationRunner>.Instance).RunAsync(c, CancellationToken.None);
        Assert.Equal(3L, await c.ExecuteScalarAsync<long>("PRAGMA user_version"));

        var bible = await c.ExecuteScalarAsync<long>("INSERT INTO Bibles (Abbreviation, Name, Language) VALUES ('TST', 'Biblia sintética', 'es'); SELECT last_insert_rowid();");
        var book43 = await c.ExecuteScalarAsync<long>("INSERT INTO Books (BibleId, BookNumber, Name, Testament) VALUES (@b, 43, 'Libro 43', 'NT'); SELECT last_insert_rowid();", new { b = bible });
        var book1 = await c.ExecuteScalarAsync<long>("INSERT INTO Books (BibleId, BookNumber, Name, Testament) VALUES (@b, 1, 'Libro 1', 'OT'); SELECT last_insert_rowid();", new { b = bible });
        await c.ExecuteAsync("INSERT INTO Verses (BookId, Chapter, VerseNumber, Text) VALUES (@k, 3, 16, 'uno'), (@k, 3, 17, 'dos')", new { k = book43 });
        var song = await c.ExecuteScalarAsync<long>("INSERT INTO Songs (Title) VALUES ('Canción'); SELECT last_insert_rowid();");
        var pl = await c.ExecuteScalarAsync<long>("INSERT INTO Playlists (Name, ServiceDate) VALUES ('Domingo', '2026-11-22'); SELECT last_insert_rowid();");

        await c.ExecuteAsync("INSERT INTO PlaylistItems (Id, PlaylistId, SequenceIndex, ItemType, Title, SongId) VALUES (10, @p, 0, 'SONG', 'Canción', @s)", new { p = pl, s = song });
        await c.ExecuteAsync("INSERT INTO PlaylistItems (Id, PlaylistId, SequenceIndex, ItemType, Title, BookId, Chapter, VerseStart, VerseEnd) VALUES (11, @p, 1, 'PASSAGE', 'Libro 43 3:16-17', @b, 3, 16, 17)", new { p = pl, b = book43 });
        await c.ExecuteAsync("INSERT INTO PlaylistItems (Id, PlaylistId, SequenceIndex, ItemType, Title, BookId, Chapter, VerseStart) VALUES (12, @p, 2, 'PASSAGE', 'Libro 1 1:1', @b, 1, 1)", new { p = pl, b = book1 });
        return (c, pl, book43, book1);
    }

    [Fact]
    public async Task Existing_passages_are_converted_to_canonical_references_without_losing_anything()
    {
        var (seed, playlistId, _, _) = await SeedVersion3Async();
        await seed.DisposeAsync();

        var result = await _db.Initializer.InitializeAsync(CancellationToken.None);

        Assert.Equal((3, 4), (result.FromVersion, result.ToVersion));
        Assert.Equal([4], result.Applied);
        Assert.NotNull(result.BackupPath);
        await using var c = await _db.OpenAsync();
        var rows = (await c.QueryAsync<(long Id, long SequenceIndex, string ItemType, string Title, long? SongId, long? BookNumber, long? Chapter, long? VerseStart, long? VerseEnd, string? Abbr, long IsMissing)>(
            "SELECT Id, SequenceIndex, ItemType, Title, SongId, BookNumber, Chapter, VerseStart, VerseEnd, BibleAbbreviation, IsMissing FROM PlaylistItems WHERE PlaylistId = @p ORDER BY SequenceIndex", new { p = playlistId })).ToList();

        Assert.Equal(3, rows.Count);
        Assert.Equal((10L, "SONG", "Canción", true, (long?)null, (string?)null, 0L), (rows[0].Id, rows[0].ItemType, rows[0].Title, rows[0].SongId is not null, rows[0].BookNumber, rows[0].Abbr, rows[0].IsMissing));
        Assert.Equal((11L, "PASSAGE", "Libro 43 3:16-17", (long?)43, (long?)3, (long?)16, (long?)17, "TST", 0L),
                     (rows[1].Id, rows[1].ItemType, rows[1].Title, rows[1].BookNumber, rows[1].Chapter, rows[1].VerseStart, rows[1].VerseEnd, rows[1].Abbr, rows[1].IsMissing));
        Assert.Equal((12L, (long?)1, (long?)1, (long?)1, (long?)null, "TST"), (rows[2].Id, rows[2].BookNumber, rows[2].Chapter, rows[2].VerseStart, rows[2].VerseEnd, rows[2].Abbr));

        Assert.DoesNotContain("BookId", await c.QueryAsync<string>("SELECT name FROM pragma_table_info('PlaylistItems')"));
        Assert.Empty(await c.QueryAsync("PRAGMA foreign_key_check"));
        Assert.Equal(1L, await c.ExecuteScalarAsync<long>("PRAGMA foreign_keys"));
        Assert.Equal(4L, await c.ExecuteScalarAsync<long>("PRAGMA user_version"));
        // Índices y triggers que el DROP se llevó: recreados.
        Assert.Equal(["PlaylistItems_bi", "PlaylistItems_bu"], await c.QueryAsync<string>("SELECT name FROM sqlite_master WHERE type = 'trigger' AND tbl_name = 'PlaylistItems' ORDER BY name"));
        Assert.Equal(["IX_PlaylistItems_PlaylistId_SequenceIndex", "IX_PlaylistItems_PresentationId", "IX_PlaylistItems_SongId"],
                     await c.QueryAsync<string>("SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'PlaylistItems' AND name LIKE 'IX_%' ORDER BY name"));
        // Y los pasajes convertidos se resuelven contra la Biblia instalada.
        var resolved = await PassageResolver.ResolvePlaylistAsync(c, playlistId, null, CancellationToken.None);
        Assert.Equal([PassageStatus.Resolved, PassageStatus.PassageNotFound], resolved.Select(r => r.Resolution.Status));   // el libro 1 no tiene versículos cargados
        Assert.Equal(2, resolved[0].Resolution.VersesFound);
    }

    [Fact]
    public async Task The_backup_made_before_migrating_still_has_the_old_schema_and_data()
    {
        var (seed, _, _, _) = await SeedVersion3Async();
        await seed.DisposeAsync();

        var result = await _db.Initializer.InitializeAsync(CancellationToken.None);

        await using var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = result.BackupPath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        await backup.OpenAsync();
        Assert.Equal(3L, await backup.ExecuteScalarAsync<long>("PRAGMA user_version"));
        Assert.Contains("BookId", await backup.QueryAsync<string>("SELECT name FROM pragma_table_info('PlaylistItems')"));
        Assert.Equal(2L, await backup.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM PlaylistItems WHERE ItemType = 'PASSAGE' AND BookId IS NOT NULL"));
    }

    [Fact]
    public async Task Reopening_after_the_migration_changes_nothing()
    {
        var (seed, _, _, _) = await SeedVersion3Async();
        await seed.DisposeAsync();
        await _db.Initializer.InitializeAsync(CancellationToken.None);
        await using var c = await _db.OpenAsync();
        var before = string.Join("\n", (await c.QueryAsync("SELECT * FROM PlaylistItems ORDER BY Id")).Select(r => string.Join("|", ((IDictionary<string, object>)r).Values)));

        var again = await _db.Initializer.InitializeAsync(CancellationToken.None);

        Assert.Empty(again.Applied);
        Assert.Equal(before, string.Join("\n", (await c.QueryAsync("SELECT * FROM PlaylistItems ORDER BY Id")).Select(r => string.Join("|", ((IDictionary<string, object>)r).Values))));
    }

    [Fact]
    public async Task A_service_with_no_passages_migrates_too_and_an_empty_one_as_well()
    {
        await using (var c = await _db.OpenAsync())
        {
            await new MigrationRunner([.. Embedded.Take(3)], _db.Options, NullLogger<MigrationRunner>.Instance).RunAsync(c, CancellationToken.None);
            await c.ExecuteAsync("INSERT INTO Playlists (Name) VALUES ('Vacío')");
        }

        var result = await _db.Initializer.InitializeAsync(CancellationToken.None);

        Assert.Equal(4, result.ToVersion);
    }

    // ───────── datos que NO se pueden convertir sin adivinar: se aborta y no se pierde nada ─────────

    [Fact]
    public async Task A_passage_whose_book_no_longer_exists_aborts_the_migration_instead_of_being_lost()
    {
        var (c, playlistId, book43, _) = await SeedVersion3Async();
        await c.ExecuteAsync("DELETE FROM Books WHERE Id = @b", new { b = book43 });          // en el esquema viejo, el pasaje queda con BookId NULL (huérfano)
        await c.DisposeAsync();

        var ex = await Assert.ThrowsAsync<DatabaseInitializationException>(() => _db.Initializer.InitializeAsync(CancellationToken.None));

        Assert.Contains("migración 4", ex.Message);
        Assert.Contains("Hay_pasajes_cuyo_libro_ya_no_existe", ex.Message);                    // el motivo, en el mensaje que ve el operador
        Assert.NotNull(ex.BackupPath);
        await using var d = await _db.OpenAsync();
        Assert.Equal(3L, await d.ExecuteScalarAsync<long>("PRAGMA user_version"));            // sigue en v3
        Assert.Contains("BookId", await d.QueryAsync<string>("SELECT name FROM pragma_table_info('PlaylistItems')"));
        Assert.Equal(3L, await d.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM PlaylistItems WHERE PlaylistId = @p", new { p = playlistId }));   // los tres elementos siguen
        Assert.Equal(1L, await d.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM PlaylistItems WHERE ItemType = 'PASSAGE' AND BookId IS NULL"));
    }

    [Fact]
    public async Task A_passage_in_a_book_outside_the_canonical_range_aborts_the_migration()
    {
        var (c, _, _, _) = await SeedVersion3Async();
        await c.ExecuteAsync("UPDATE Books SET BookNumber = 70 WHERE BookNumber = 43");        // un libro deuterocanónico mal numerado
        await c.DisposeAsync();

        var ex = await Assert.ThrowsAsync<DatabaseInitializationException>(() => _db.Initializer.InitializeAsync(CancellationToken.None));

        Assert.Contains("Hay_pasajes_de_un_libro_con_numero_fuera_de_1_a_66", ex.Message);
        await using var d = await _db.OpenAsync();
        Assert.Equal(3L, await d.ExecuteScalarAsync<long>("PRAGMA user_version"));
        Assert.Equal(2L, await d.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM PlaylistItems WHERE ItemType = 'PASSAGE' AND BookId IS NOT NULL"));
    }

    [Fact]
    public async Task Duplicate_verses_abort_the_migration_when_making_the_verse_index_unique_and_nothing_is_changed()
    {
        var (c, _, book43, _) = await SeedVersion3Async();
        await c.ExecuteAsync("INSERT INTO Verses (BookId, Chapter, VerseNumber, Text) VALUES (@k, 3, 16, 'duplicado')", new { k = book43 });   // el esquema v3 lo permitía
        await c.DisposeAsync();

        var ex = await Assert.ThrowsAsync<DatabaseInitializationException>(() => _db.Initializer.InitializeAsync(CancellationToken.None));

        Assert.Contains("UNIQUE", ex.Message);
        await using var d = await _db.OpenAsync();
        Assert.Equal(3L, await d.ExecuteScalarAsync<long>("PRAGMA user_version"));
        Assert.Contains("BookId", await d.QueryAsync<string>("SELECT name FROM pragma_table_info('PlaylistItems')"));   // la reconstrucción tampoco quedó a medias
        Assert.Equal(0L, await d.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM sqlite_master WHERE name = 'PlaylistItems_new'"));
        Assert.Equal(3L, await d.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Verses"));
    }
}
