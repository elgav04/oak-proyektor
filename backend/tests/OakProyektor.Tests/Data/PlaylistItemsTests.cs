using Dapper;
using Microsoft.Data.Sqlite;

namespace OakProyektor.Tests.Data;

/// <summary>I13: PlaylistItems con claves foráneas; qué pasa al borrar un registro que está en un servicio.</summary>
public sealed class PlaylistItemsTests : IDisposable
{
    private readonly TestDatabase _db = new();
    public void Dispose() => _db.Dispose();

    private sealed record Item(long Id, long SequenceIndex, string ItemType, string Title, long? SongId, long? PresentationId, long? BookId, long? ItemRefId, long IsMissing);

    private sealed record Fixture(long PlaylistId, long SongA, long SongB, long Presentation, long BibleId, long BookId);

    private async Task<(SqliteConnection Connection, Fixture F)> SeedAsync()
    {
        await _db.InitializeAsync();
        var c = await _db.OpenAsync();
        var songA = await c.ExecuteScalarAsync<long>("INSERT INTO Songs (Title) VALUES ('Canción A'); SELECT last_insert_rowid();");
        var songB = await c.ExecuteScalarAsync<long>("INSERT INTO Songs (Title) VALUES ('Canción B'); SELECT last_insert_rowid();");
        var pres = await c.ExecuteScalarAsync<long>("INSERT INTO Presentations (Title, SourceType) VALUES ('Anuncios', 'CUSTOM'); SELECT last_insert_rowid();");
        var (bibleId, bookId, _) = await TestDatabase.InsertVerseAsync(c, "Texto sintético");
        var pl = await c.ExecuteScalarAsync<long>("INSERT INTO Playlists (Name, ServiceDate) VALUES ('Domingo', '2026-11-22'); SELECT last_insert_rowid();");

        await c.ExecuteAsync("INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title, SongId) VALUES (@p, 0, 'SONG', 'Canción A', @s)", new { p = pl, s = songA });
        await c.ExecuteAsync("INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title, SongId) VALUES (@p, 1, 'SONG', 'Canción B', @s)", new { p = pl, s = songB });
        await c.ExecuteAsync("INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title, PresentationId) VALUES (@p, 2, 'PRESENTATION', 'Anuncios', @x)", new { p = pl, x = pres });
        await c.ExecuteAsync("INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title, BookId, Chapter, VerseStart, VerseEnd) VALUES (@p, 3, 'PASSAGE', 'Libro uno 1:1-3', @b, 1, 1, 3)", new { p = pl, b = bookId });
        return (c, new Fixture(pl, songA, songB, pres, bibleId, bookId));
    }

    private static async Task<List<Item>> ItemsAsync(SqliteConnection c, long playlistId) =>
        (await c.QueryAsync<Item>("SELECT Id, SequenceIndex, ItemType, Title, SongId, PresentationId, BookId, ItemRefId, IsMissing FROM PlaylistItems WHERE PlaylistId = @p ORDER BY SequenceIndex", new { p = playlistId })).ToList();

    [Fact]
    public async Task ItemsJson_is_gone_and_items_expose_their_reference_and_a_missing_flag()
    {
        var (c, f) = await SeedAsync();
        await using var _ = c;

        Assert.DoesNotContain("ItemsJson", await c.QueryAsync<string>("SELECT name FROM pragma_table_info('Playlists')"));
        var items = await ItemsAsync(c, f.PlaylistId);

        Assert.Equal([f.SongA, f.SongB, f.Presentation, f.BookId], items.Select(i => i.ItemRefId));
        Assert.All(items, i => Assert.Equal(0L, i.IsMissing));
    }

    [Fact]
    public async Task Deleting_a_song_used_in_a_service_marks_the_item_missing_keeps_its_title_and_leaves_no_broken_reference()
    {
        var (c, f) = await SeedAsync();
        await using var _ = c;

        await c.ExecuteAsync("DELETE FROM Songs WHERE Id = @id", new { id = f.SongA });                  // se puede borrar: no se bloquea

        var items = await ItemsAsync(c, f.PlaylistId);
        Assert.Equal(4, items.Count);                                                                       // el elemento NO desapareció
        Assert.Equal(("Canción A", (long?)null, 1L, (long?)null), (items[0].Title, items[0].SongId, items[0].IsMissing, items[0].ItemRefId));   // título conservado, faltante marcado
        Assert.Equal([0L, 0L, 0L], items.Skip(1).Select(i => i.IsMissing));                                // los demás intactos
        Assert.Equal([0L, 1L, 2L, 3L], items.Select(i => i.SequenceIndex));                                     // el orden del servicio no cambió
        Assert.Empty(await c.QueryAsync("PRAGMA foreign_key_check"));                                       // y no queda ninguna referencia rota
    }

    [Fact]
    public async Task Deleting_a_presentation_or_a_whole_bible_also_marks_their_items_missing()
    {
        var (c, f) = await SeedAsync();
        await using var _ = c;

        await c.ExecuteAsync("DELETE FROM Presentations WHERE Id = @id", new { id = f.Presentation });
        await c.ExecuteAsync("DELETE FROM Bibles WHERE Id = @id", new { id = f.BibleId });                // cascada Bibles → Books → Verses

        var items = await ItemsAsync(c, f.PlaylistId);
        Assert.Equal([0L, 0L, 1L, 1L], items.Select(i => i.IsMissing));
        Assert.Equal(["Canción A", "Canción B", "Anuncios", "Libro uno 1:1-3"], items.Select(i => i.Title));
        Assert.Empty(await c.QueryAsync("PRAGMA foreign_key_check"));
    }

    [Fact]
    public async Task Deleting_a_song_does_not_touch_other_services_and_deleting_a_service_removes_only_its_items()
    {
        var (c, f) = await SeedAsync();
        await using var _ = c;
        var other = await c.ExecuteScalarAsync<long>("INSERT INTO Playlists (Name) VALUES ('Miércoles'); SELECT last_insert_rowid();");
        await c.ExecuteAsync("INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title, SongId) VALUES (@p, 0, 'SONG', 'Canción B', @s)", new { p = other, s = f.SongB });

        await c.ExecuteAsync("DELETE FROM Playlists WHERE Id = @id", new { id = f.PlaylistId });

        Assert.Equal(0L, await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM PlaylistItems WHERE PlaylistId = @p", new { p = f.PlaylistId }));
        var remaining = await ItemsAsync(c, other);
        Assert.Single(remaining);
        Assert.Equal(f.SongB, remaining[0].SongId);
        Assert.Equal(1L, await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Songs WHERE Id = @id", new { id = f.SongB }));   // la canción sigue existiendo
    }

    [Theory]
    [InlineData("INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title, SongId) VALUES ({p}, 9, 'VIDEO', 'x', {s})")]                                  // tipo inválido
    [InlineData("INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title, SongId, BookId) VALUES ({p}, 9, 'SONG', 'x', {s}, {b})")]                    // dos referencias
    [InlineData("INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title, BookId) VALUES ({p}, 9, 'PASSAGE', 'x', {b})")]                              // pasaje sin capítulo
    [InlineData("INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title, BookId, Chapter, VerseStart, VerseEnd) VALUES ({p}, 9, 'PASSAGE', 'x', {b}, 1, 5, 2)")]   // fin antes que inicio
    [InlineData("INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title, SongId, Chapter) VALUES ({p}, 9, 'SONG', 'x', {s}, 3)")]                      // canción con capítulo
    [InlineData("INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title) VALUES ({p}, 9, 'SONG', 'x')")]                                              // elemento NUEVO sin referencia
    [InlineData("INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title, SongId) VALUES ({p}, 9, 'SONG', 'x', 123456)")]                              // referencia a un registro inexistente
    [InlineData("INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title, SongId) VALUES (424242, 9, 'SONG', 'x', {s})")]                              // servicio inexistente
    public async Task Invalid_items_are_rejected_by_the_database(string template)
    {
        var (c, f) = await SeedAsync();
        await using var _ = c;
        var sql = template.Replace("{p}", f.PlaylistId.ToString()).Replace("{s}", f.SongA.ToString()).Replace("{b}", f.BookId.ToString());

        await Assert.ThrowsAsync<SqliteException>(() => c.ExecuteAsync(sql));
    }

    [Fact]
    public async Task Finding_where_a_song_is_used_before_deleting_it_uses_an_index()
    {
        var (c, f) = await SeedAsync();
        await using var _ = c;

        var rows = await c.QueryAsync("EXPLAIN QUERY PLAN SELECT DISTINCT PlaylistId FROM PlaylistItems WHERE SongId = @s", new { s = f.SongA });
        var plan = string.Join(" | ", rows.Select(r => (string)((IDictionary<string, object>)r)["detail"]));

        Assert.Contains("IX_PlaylistItems_SongId", plan);
    }
}