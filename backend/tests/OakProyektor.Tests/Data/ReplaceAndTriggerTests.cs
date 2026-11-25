using Dapper;
using Microsoft.Data.Sqlite;

namespace OakProyektor.Tests.Data;

/// <summary>I2 (REPLACE y FTS) y M5 (triggers de actualización con UPDATE OF).</summary>
public sealed class ReplaceAndTriggerTests : IDisposable
{
    private readonly TestDatabase _db = new();
    public void Dispose() => _db.Dispose();

    private static Task IntegrityCheck(SqliteConnection c) =>
        c.ExecuteAsync("INSERT INTO VersesFts (VersesFts, rank) VALUES ('integrity-check', 1)");   // lanza SqliteException si el índice no coincide con la tabla

    private static Task<long> Matches(SqliteConnection c, string term) =>
        c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM VersesFts WHERE VersesFts MATCH @t", new { t = term });

    // ───────────────────────────── I2 ─────────────────────────────

    [Fact]
    public async Task Connections_from_the_factory_have_recursive_triggers_on()
    {
        await _db.InitializeAsync();
        await using var c = await _db.OpenAsync();
        Assert.Equal(1L, await c.ExecuteScalarAsync<long>("PRAGMA recursive_triggers"));
    }

    [Fact]
    public async Task Replacing_a_verse_keeps_VersesFts_consistent_and_the_integrity_check_passes()
    {
        await _db.InitializeAsync();
        await using var c = await _db.OpenAsync();
        var (_, bookId, verseId) = await TestDatabase.InsertVerseAsync(c, "texto viejo");

        // Lo que haría un importador descuidado: el INSERT OR REPLACE borra la fila vieja internamente.
        await c.ExecuteAsync("INSERT OR REPLACE INTO Verses (Id, BookId, Chapter, VerseNumber, Text) VALUES (@id, @b, 1, 1, 'texto nuevo')", new { id = verseId, b = bookId });

        Assert.Equal(0L, await Matches(c, "viejo"));      // sin entradas fantasma
        Assert.Equal(1L, await Matches(c, "nuevo"));
        await IntegrityCheck(c);
    }

    [Fact]
    public async Task Control_without_recursive_triggers_replace_leaves_a_ghost_entry_which_is_why_the_pragma_is_set()
    {
        // Demuestra el peligro: una conexión SIN el PRAGMA (no creada por la fábrica) desincroniza el índice.
        await _db.InitializeAsync();
        long bookId, verseId;
        await using (var setup = await _db.OpenAsync()) (_, bookId, verseId) = await TestDatabase.InsertVerseAsync(setup, "texto viejo");

        await using var raw = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _db.Options.DatabasePath, ForeignKeys = true }.ToString());
        await raw.OpenAsync();
        Assert.Equal(0L, await raw.ExecuteScalarAsync<long>("PRAGMA recursive_triggers"));
        await raw.ExecuteAsync("INSERT OR REPLACE INTO Verses (Id, BookId, Chapter, VerseNumber, Text) VALUES (@id, @b, 1, 1, 'texto nuevo')", new { id = verseId, b = bookId });

        Assert.Equal(1L, await Matches(raw, "viejo"));    // el texto viejo SIGUE en el índice aunque ya no está en la tabla
        await Assert.ThrowsAsync<SqliteException>(() => IntegrityCheck(raw));
    }

    [Fact]
    public async Task Upsert_with_on_conflict_do_update_is_the_supported_way_and_keeps_the_index_consistent()
    {
        await _db.InitializeAsync();
        await using var c = await _db.OpenAsync();
        var (_, bookId, verseId) = await TestDatabase.InsertVerseAsync(c, "texto viejo");

        await c.ExecuteAsync("""
            INSERT INTO Verses (Id, BookId, Chapter, VerseNumber, Text) VALUES (@id, @b, 1, 1, 'texto nuevo')
            ON CONFLICT(Id) DO UPDATE SET Text = excluded.Text
            """, new { id = verseId, b = bookId });

        Assert.Equal(0L, await Matches(c, "viejo"));
        Assert.Equal(1L, await Matches(c, "nuevo"));
        Assert.Equal(1L, await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Verses"));
        await IntegrityCheck(c);
    }

    [Fact]
    public async Task Replacing_a_parent_row_silently_deletes_its_children_which_is_another_reason_replace_is_banned()
    {
        await _db.InitializeAsync();
        await using var c = await _db.OpenAsync();
        var (bibleId, _, _) = await TestDatabase.InsertVerseAsync(c, "texto sintético");

        // Libro con el mismo (BibleId, BookNumber): REPLACE borra el viejo y, por la cascada, TODOS sus versículos.
        await c.ExecuteAsync("INSERT OR REPLACE INTO Books (BibleId, BookNumber, Name, Testament) VALUES (@b, 1, 'Libro uno bis', 'OT')", new { b = bibleId });

        Assert.Equal(0L, await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Verses"));
        await IntegrityCheck(c);   // eso sí: el índice quedó limpio, porque los triggers DELETE se disparan también en cascada
    }

    // ───────────────────────────── M5 ─────────────────────────────

    [Fact]
    public async Task Update_triggers_are_declared_with_UPDATE_OF_on_the_columns_that_matter()
    {
        await _db.InitializeAsync();
        await using var c = await _db.OpenAsync();
        string Sql(string trigger) => c.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type = 'trigger' AND name = @n", new { n = trigger })!;

        Assert.Contains("UPDATE OF Text, Id ON Verses", Sql("Verses_au"));
        // Migración 9: el índice de canciones indexa también el autor y se alimenta de SongSections (SongSlides quedó congelada y sin triggers).
        Assert.Contains("UPDATE OF Title, Author, Id ON Songs", Sql("Songs_au"));
        Assert.Contains("UPDATE OF SongId, Position, Text, Id ON SongSections", Sql("SongSections_au"));
        Assert.Equal(0L, c.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger' AND tbl_name = 'SongSlides'"));
    }

    [Fact]
    public async Task Changing_only_columns_the_index_ignores_leaves_it_alone_and_consistent()
    {
        await _db.InitializeAsync();
        await using var c = await _db.OpenAsync();
        var (_, _, verseId) = await TestDatabase.InsertVerseAsync(c, "texto estable");
        var songId = await c.ExecuteScalarAsync<long>("INSERT INTO Songs (Title) VALUES ('Tema'); SELECT last_insert_rowid();");
        var sectionId = await c.ExecuteScalarAsync<long>("INSERT INTO SongSections (SongId, Position, Kind, Label, Text) VALUES (@s, 0, 'verse', 'Verso 1', 'letra uno'); SELECT last_insert_rowid();", new { s = songId });

        await c.ExecuteAsync("UPDATE Verses SET Chapter = 5 WHERE Id = @id", new { id = verseId });                          // columna que el FTS no indexa
        await c.ExecuteAsync("UPDATE SongSections SET Label = 'Coro', Kind = 'chorus' WHERE Id = @id", new { id = sectionId });   // idem
        await c.ExecuteAsync("UPDATE Songs SET Notes = 'una nota', Revision = 2 WHERE Id = @id", new { id = songId });           // idem

        Assert.Equal(1L, await Matches(c, "estable"));
        await IntegrityCheck(c);
        Assert.Equal(1L, await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM SongsFts WHERE SongsFts MATCH 'letra'"));
    }

    [Fact]
    public async Task Changing_a_verse_Id_moves_its_index_entry_instead_of_leaving_a_stale_rowid()
    {
        await _db.InitializeAsync();
        await using var c = await _db.OpenAsync();
        var (_, _, verseId) = await TestDatabase.InsertVerseAsync(c, "texto con id cambiante");

        await c.ExecuteAsync("UPDATE Verses SET Id = 9999 WHERE Id = @id", new { id = verseId });

        Assert.Equal(1L, await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM VersesFts WHERE VersesFts MATCH 'cambiante' AND rowid = 9999"));
        await IntegrityCheck(c);
    }

    [Fact]
    public async Task Changing_a_song_Id_moves_its_search_row_too()
    {
        await _db.InitializeAsync();
        await using var c = await _db.OpenAsync();
        var songId = await c.ExecuteScalarAsync<long>("INSERT INTO Songs (Title) VALUES ('Himno movible'); SELECT last_insert_rowid();");

        await c.ExecuteAsync("UPDATE Songs SET Id = 777 WHERE Id = @id", new { id = songId });

        var rowids = (await c.QueryAsync<long>("SELECT rowid FROM SongsFts WHERE SongsFts MATCH 'movible'")).ToArray();
        Assert.Equal(new[] { 777L }, rowids);
        Assert.Equal(1L, await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM SongsFts"));   // y no quedó la fila vieja
    }

    [Fact]
    public async Task Moving_a_section_to_another_song_updates_the_lyrics_of_both()
    {
        await _db.InitializeAsync();
        await using var c = await _db.OpenAsync();
        var a = await c.ExecuteScalarAsync<long>("INSERT INTO Songs (Title) VALUES ('A'); SELECT last_insert_rowid();");
        var b = await c.ExecuteScalarAsync<long>("INSERT INTO Songs (Title) VALUES ('B'); SELECT last_insert_rowid();");
        var slide = await c.ExecuteScalarAsync<long>("INSERT INTO SongSections (SongId, Position, Kind, Label, Text) VALUES (@a, 0, 'other', 'Uno', 'palabra migratoria'); SELECT last_insert_rowid();", new { a });

        await c.ExecuteAsync("UPDATE SongSections SET SongId = @b WHERE Id = @id", new { b, id = slide });

        var rowids = (await c.QueryAsync<long>("SELECT rowid FROM SongsFts WHERE SongsFts MATCH 'migratoria'")).ToArray();
        Assert.Equal(new[] { b }, rowids);
    }
}