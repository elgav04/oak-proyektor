using Dapper;

namespace OakProyektor.Tests.Data;

public sealed class FtsTests : IDisposable
{
    private readonly TestDatabase _db = new();
    public void Dispose() => _db.Dispose();

    private static string[] SearchVerses(Microsoft.Data.Sqlite.SqliteConnection c, string match) =>
        c.Query<string>("SELECT v.Text FROM VersesFts f JOIN Verses v ON v.Id = f.rowid WHERE VersesFts MATCH @m", new { m = match }).ToArray();

    [Fact]
    public async Task Search_without_accent_finds_text_with_accent()
    {
        await _db.InitializeAsync();
        await using var c = await _db.OpenAsync();
        await TestDatabase.InsertVerseAsync(c, "Jesús caminó junto al lago");

        Assert.Single(SearchVerses(c, "jesus"));
        Assert.Single(SearchVerses(c, "Jesús"));      // con acento también
        Assert.Single(SearchVerses(c, "CAMINO"));     // mayúsculas y sin acento
    }

    [Fact]
    public async Task Verse_triggers_keep_fts_in_sync_on_insert_update_and_delete()
    {
        await _db.InitializeAsync();
        await using var c = await _db.OpenAsync();

        var (_, _, verseId) = await TestDatabase.InsertVerseAsync(c, "El pastor guardaba las ovejas");
        Assert.Single(SearchVerses(c, "ovejas"));                               // INSERT

        await c.ExecuteAsync("UPDATE Verses SET Text = 'El rey construyó una ciudad' WHERE Id = @id", new { id = verseId });
        Assert.Empty(SearchVerses(c, "ovejas"));                                // UPDATE: sale el texto viejo
        Assert.Single(SearchVerses(c, "ciudad"));                               //         entra el nuevo

        await c.ExecuteAsync("DELETE FROM Verses WHERE Id = @id", new { id = verseId });
        Assert.Empty(SearchVerses(c, "ciudad"));                                // DELETE
        Assert.Equal(0L, await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM VersesFts WHERE VersesFts MATCH 'ciudad OR ovejas'"));
    }

    [Fact]
    public async Task Fts_integrity_check_passes_after_changes()
    {
        await _db.InitializeAsync();
        await using var c = await _db.OpenAsync();
        var (_, _, id) = await TestDatabase.InsertVerseAsync(c, "primero");
        await c.ExecuteAsync("UPDATE Verses SET Text = 'segundo' WHERE Id = @id", new { id });
        await c.ExecuteAsync("DELETE FROM Verses WHERE Id = @id", new { id });

        // Lanza SqliteException si el índice FTS no coincide con la tabla de contenido.
        await c.ExecuteAsync("INSERT INTO VersesFts (VersesFts, rank) VALUES ('integrity-check', 1)");
    }

    private static long[] SearchSongs(Microsoft.Data.Sqlite.SqliteConnection c, string match) =>
        c.Query<long>("SELECT rowid FROM SongsFts WHERE SongsFts MATCH @m", new { m = match }).ToArray();

    [Fact]
    public async Task Song_search_covers_title_author_and_section_text_and_follows_changes()
    {
        await _db.InitializeAsync();
        await using var c = await _db.OpenAsync();

        var songId = await c.ExecuteScalarAsync<long>("INSERT INTO Songs (Title, Author) VALUES ('Canción de prueba', 'Autora Ficticia'); SELECT last_insert_rowid();");
        Assert.Equal([songId], SearchSongs(c, "cancion"));                       // título, sin acento
        Assert.Equal([songId], SearchSongs(c, "ficticia"));                      // autor

        var sectionId = await c.ExecuteScalarAsync<long>(
            "INSERT INTO SongSections (SongId, Position, Kind, Label, Text) VALUES (@s, 0, 'verse', 'Verso 1', 'Lalala sintético con gozo'); SELECT last_insert_rowid();", new { s = songId });
        Assert.Equal([songId], SearchSongs(c, "sintetico"));                     // letra, sin acento

        await c.ExecuteAsync("UPDATE SongSections SET Text = 'Tararí con alegria' WHERE Id = @id", new { id = sectionId });
        Assert.Empty(SearchSongs(c, "gozo"));
        Assert.Equal([songId], SearchSongs(c, "alegría"));

        await c.ExecuteAsync("UPDATE Songs SET Title = 'Himno nuevo', Author = NULL WHERE Id = @id", new { id = songId });
        Assert.Empty(SearchSongs(c, "cancion"));
        Assert.Empty(SearchSongs(c, "ficticia"));
        Assert.Equal([songId], SearchSongs(c, "himno"));

        await c.ExecuteAsync("DELETE FROM SongSections WHERE Id = @id", new { id = sectionId });
        Assert.Empty(SearchSongs(c, "alegria"));

        await c.ExecuteAsync("DELETE FROM Songs WHERE Id = @id", new { id = songId });
        Assert.Empty(SearchSongs(c, "himno"));
    }

    [Fact]
    public async Task Deleting_a_song_cascades_sections_arrangement_and_revisions_and_clears_search()
    {
        await _db.InitializeAsync();
        await using var c = await _db.OpenAsync();
        var songId = await c.ExecuteScalarAsync<long>("INSERT INTO Songs (Title) VALUES ('Tema'); SELECT last_insert_rowid();");
        var sectionId = await c.ExecuteScalarAsync<long>("INSERT INTO SongSections (SongId, Position, Kind, Label, Text) VALUES (@s, 0, 'other', 'Unica', 'estrofa unica'); SELECT last_insert_rowid();", new { s = songId });
        var arrangementId = await c.ExecuteScalarAsync<long>("INSERT INTO SongArrangements (SongId, Name, IsDefault) VALUES (@s, 'Principal', 1); SELECT last_insert_rowid();", new { s = songId });
        await c.ExecuteAsync("INSERT INTO SongArrangementItems (ArrangementId, Position, SectionId) VALUES (@a, 0, @sec), (@a, 1, @sec)", new { a = arrangementId, sec = sectionId });
        await c.ExecuteAsync("INSERT INTO SongRevisions (SongId, Reason, Snapshot) VALUES (@s, 'save', '{}')", new { s = songId });

        // La clave foránea del orden hacia la sección es NO ACTION: borrar la canción entera funciona porque la cascada ya quitó los elementos al final de la sentencia.
        await c.ExecuteAsync("DELETE FROM Songs WHERE Id = @s", new { s = songId });

        foreach (var table in new[] { "SongSections", "SongArrangements", "SongArrangementItems", "SongRevisions" })
            Assert.Equal(0L, await c.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {table}"));
        Assert.Empty(SearchSongs(c, "estrofa"));
        Assert.Empty(SearchSongs(c, "tema"));
    }

    [Fact]
    public async Task A_section_used_by_the_arrangement_cannot_be_deleted_silently()
    {
        await _db.InitializeAsync();
        await using var c = await _db.OpenAsync();
        var songId = await c.ExecuteScalarAsync<long>("INSERT INTO Songs (Title) VALUES ('Tema'); SELECT last_insert_rowid();");
        var sectionId = await c.ExecuteScalarAsync<long>("INSERT INTO SongSections (SongId, Position, Kind, Label, Text) VALUES (@s, 0, 'chorus', 'Coro', 'texto del coro'); SELECT last_insert_rowid();", new { s = songId });
        var arrangementId = await c.ExecuteScalarAsync<long>("INSERT INTO SongArrangements (SongId, Name, IsDefault) VALUES (@s, 'Principal', 1); SELECT last_insert_rowid();", new { s = songId });
        await c.ExecuteAsync("INSERT INTO SongArrangementItems (ArrangementId, Position, SectionId) VALUES (@a, 0, @sec)", new { a = arrangementId, sec = sectionId });

        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => c.ExecuteAsync("DELETE FROM SongSections WHERE Id = @id", new { id = sectionId }));

        Assert.Equal(1L, await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM SongArrangementItems"));   // el orden sigue intacto
    }
}
