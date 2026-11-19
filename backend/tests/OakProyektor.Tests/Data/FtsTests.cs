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
    public async Task Song_search_covers_title_and_slide_content_and_follows_changes()
    {
        await _db.InitializeAsync();
        await using var c = await _db.OpenAsync();

        var songId = await c.ExecuteScalarAsync<long>("INSERT INTO Songs (Title) VALUES ('Canción de prueba'); SELECT last_insert_rowid();");
        Assert.Equal([songId], SearchSongs(c, "cancion"));                       // título, sin acento

        var slideId = await c.ExecuteScalarAsync<long>(
            "INSERT INTO SongSlides (SongId, SequenceIndex, Label, Content) VALUES (@s, 0, 'Verso 1', 'Alabad al Señor con gozo'); SELECT last_insert_rowid();", new { s = songId });
        Assert.Equal([songId], SearchSongs(c, "senor"));                         // letra, sin acento

        await c.ExecuteAsync("UPDATE SongSlides SET Content = 'Cantad con alegria' WHERE Id = @id", new { id = slideId });
        Assert.Empty(SearchSongs(c, "gozo"));
        Assert.Equal([songId], SearchSongs(c, "alegría"));

        await c.ExecuteAsync("UPDATE Songs SET Title = 'Himno nuevo' WHERE Id = @id", new { id = songId });
        Assert.Empty(SearchSongs(c, "cancion"));
        Assert.Equal([songId], SearchSongs(c, "himno"));

        await c.ExecuteAsync("DELETE FROM SongSlides WHERE Id = @id", new { id = slideId });
        Assert.Empty(SearchSongs(c, "alegria"));

        await c.ExecuteAsync("DELETE FROM Songs WHERE Id = @id", new { id = songId });
        Assert.Empty(SearchSongs(c, "himno"));
    }

    [Fact]
    public async Task Deleting_a_song_cascades_slides_and_clears_search()
    {
        await _db.InitializeAsync();
        await using var c = await _db.OpenAsync();
        var songId = await c.ExecuteScalarAsync<long>("INSERT INTO Songs (Title) VALUES ('Tema'); SELECT last_insert_rowid();");
        await c.ExecuteAsync("INSERT INTO SongSlides (SongId, SequenceIndex, Content) VALUES (@s, 0, 'estrofa unica')", new { s = songId });

        await c.ExecuteAsync("DELETE FROM Songs WHERE Id = @s", new { s = songId });

        Assert.Equal(0L, await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM SongSlides"));
        Assert.Empty(SearchSongs(c, "estrofa"));
        Assert.Empty(SearchSongs(c, "tema"));
    }
}
