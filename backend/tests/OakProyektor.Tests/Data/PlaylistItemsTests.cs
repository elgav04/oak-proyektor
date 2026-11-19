using Dapper;
using Microsoft.Data.Sqlite;

namespace OakProyektor.Tests.Data;

/// <summary>
/// I13 + menor de la ronda 2: PlaylistItems. Canciones y presentaciones tienen clave foránea real (borrar el registro las marca faltantes); un PASAJE se
/// guarda por referencia canónica (libro 1 a 66, capítulo, versículos y la abreviatura de la Biblia como texto) y NO depende de ninguna Biblia instalada.
/// </summary>
public sealed class PlaylistItemsTests : IDisposable
{
    private readonly TestDatabase _db = new();
    public void Dispose() => _db.Dispose();

    private sealed record Item(long Id, long SequenceIndex, string ItemType, string Title, long? SongId, long? PresentationId, long? BookNumber, long? Chapter,
        long? VerseStart, long? VerseEnd, string? BibleAbbreviation, long? ItemRefId, long IsMissing);

    private sealed record Fixture(long PlaylistId, long SongA, long SongB, long Presentation);

    private async Task<(SqliteConnection Connection, Fixture F)> SeedAsync()
    {
        await _db.InitializeAsync();
        var c = await _db.OpenAsync();
        var songA = await c.ExecuteScalarAsync<long>("INSERT INTO Songs (Title) VALUES ('Canción A'); SELECT last_insert_rowid();");
        var songB = await c.ExecuteScalarAsync<long>("INSERT INTO Songs (Title) VALUES ('Canción B'); SELECT last_insert_rowid();");
        var pres = await c.ExecuteScalarAsync<long>("INSERT INTO Presentations (Title, SourceType) VALUES ('Anuncios', 'CUSTOM'); SELECT last_insert_rowid();");
        var pl = await c.ExecuteScalarAsync<long>("INSERT INTO Playlists (Name, ServiceDate) VALUES ('Domingo', '2026-11-22'); SELECT last_insert_rowid();");

        await c.ExecuteAsync("INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title, SongId) VALUES (@p, 0, 'SONG', 'Canción A', @s)", new { p = pl, s = songA });
        await c.ExecuteAsync("INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title, SongId) VALUES (@p, 1, 'SONG', 'Canción B', @s)", new { p = pl, s = songB });
        await c.ExecuteAsync("INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title, PresentationId) VALUES (@p, 2, 'PRESENTATION', 'Anuncios', @x)", new { p = pl, x = pres });
        // El pasaje se guarda SIN que exista ninguna Biblia: es una referencia canónica, no una clave foránea.
        await c.ExecuteAsync(
            "INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title, BookNumber, Chapter, VerseStart, VerseEnd, BibleAbbreviation) " +
            "VALUES (@p, 3, 'PASSAGE', 'Libro 43 3:16-17', 43, 3, 16, 17, 'TST')", new { p = pl });
        return (c, new Fixture(pl, songA, songB, pres));
    }

    private static async Task<List<Item>> ItemsAsync(SqliteConnection c, long playlistId) =>
        (await c.QueryAsync<Item>(
            "SELECT Id, SequenceIndex, ItemType, Title, SongId, PresentationId, BookNumber, Chapter, VerseStart, VerseEnd, BibleAbbreviation, ItemRefId, IsMissing " +
            "FROM PlaylistItems WHERE PlaylistId = @p ORDER BY SequenceIndex", new { p = playlistId })).ToList();

    [Fact]
    public async Task ItemsJson_is_gone_and_items_expose_their_reference_and_a_missing_flag()
    {
        var (c, f) = await SeedAsync();
        await using var _ = c;

        Assert.DoesNotContain("ItemsJson", await c.QueryAsync<string>("SELECT name FROM pragma_table_info('Playlists')"));
        Assert.DoesNotContain("BookId", await c.QueryAsync<string>("SELECT name FROM pragma_table_info('PlaylistItems')"));   // ya no hay clave foránea a un libro
        var items = await ItemsAsync(c, f.PlaylistId);

        Assert.Equal<long?>([f.SongA, f.SongB, f.Presentation, null], items.Select(i => i.ItemRefId));   // el pasaje no tiene un único registro referenciado
        Assert.All(items, i => Assert.Equal(0L, i.IsMissing));
    }

    [Fact]
    public async Task A_passage_is_stored_by_canonical_reference_with_the_bible_as_plain_text()
    {
        var (c, f) = await SeedAsync();
        await using var _ = c;

        var passage = (await ItemsAsync(c, f.PlaylistId))[3];

        Assert.Equal(("PASSAGE", 43L, 3L, 16L, 17L, "TST"), (passage.ItemType, passage.BookNumber, passage.Chapter, passage.VerseStart, passage.VerseEnd, passage.BibleAbbreviation));
        Assert.Null(passage.SongId);
        Assert.Null(passage.PresentationId);
        // «TST» no es una clave foránea: no hay ninguna Biblia instalada y el pasaje existe igual.
        Assert.Equal(0L, await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Bibles"));
        Assert.DoesNotContain(await c.QueryAsync("SELECT * FROM pragma_foreign_key_list('PlaylistItems')"), r => (string)((IDictionary<string, object>)r)["from"] is "BibleAbbreviation" or "BookNumber");
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
        Assert.Equal([0L, 0L, 0L], items.Skip(1).Select(i => i.IsMissing));                                // los demás intactos (también el pasaje)
        Assert.Equal([0L, 1L, 2L, 3L], items.Select(i => i.SequenceIndex));                                     // el orden del servicio no cambió
        Assert.Empty(await c.QueryAsync("PRAGMA foreign_key_check"));                                       // y no queda ninguna referencia rota
    }

    [Fact]
    public async Task Deleting_a_presentation_marks_it_missing_but_deleting_a_whole_bible_never_touches_a_passage()
    {
        var (c, f) = await SeedAsync();
        await using var _ = c;
        var before = (await ItemsAsync(c, f.PlaylistId))[3];
        await TestDatabase.InsertVerseAsync(c, "Texto sintético", abbreviation: "TST");                    // la Biblia «TST» sí está instalada…

        await c.ExecuteAsync("DELETE FROM Presentations WHERE Id = @id", new { id = f.Presentation });
        await c.ExecuteAsync("DELETE FROM Bibles WHERE Abbreviation = 'TST'");                              // …y se borra entera (cascada Bibles → Books → Verses)

        var items = await ItemsAsync(c, f.PlaylistId);
        Assert.Equal([0L, 0L, 1L, 0L], items.Select(i => i.IsMissing));                                     // solo la presentación; el pasaje NO es «faltante»
        Assert.Equal(before, items[3]);                                                                      // el pasaje quedó EXACTAMENTE igual: nada se perdió
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
    [InlineData("INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title, SongId, PresentationId) VALUES ({p}, 9, 'SONG', 'x', {s}, {x})")]             // dos referencias
    [InlineData("INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title, SongId, Chapter) VALUES ({p}, 9, 'SONG', 'x', {s}, 3)")]                      // canción con capítulo
    [InlineData("INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title, SongId, BibleAbbreviation) VALUES ({p}, 9, 'SONG', 'x', {s}, 'TST')")]        // canción con Biblia
    [InlineData("INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title, SongId, BookNumber) VALUES ({p}, 9, 'SONG', 'x', {s}, 1)")]                  // canción con libro
    [InlineData("INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title) VALUES ({p}, 9, 'SONG', 'x')")]                                              // canción NUEVA sin referencia
    [InlineData("INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title) VALUES ({p}, 9, 'PRESENTATION', 'x')")]                                      // presentación NUEVA sin referencia
    [InlineData("INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title, SongId) VALUES ({p}, 9, 'SONG', 'x', 123456)")]                              // referencia a un registro inexistente
    [InlineData("INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title, SongId) VALUES (424242, 9, 'SONG', 'x', {s})")]                              // servicio inexistente
    // pasajes
    [InlineData("INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title, Chapter, VerseStart, BibleAbbreviation) VALUES ({p}, 9, 'PASSAGE', 'x', 1, 1, 'TST')")]        // sin libro
    [InlineData("INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title, BookNumber, VerseStart, BibleAbbreviation) VALUES ({p}, 9, 'PASSAGE', 'x', 1, 1, 'TST')")]   // sin capítulo
    [InlineData("INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title, BookNumber, Chapter, BibleAbbreviation) VALUES ({p}, 9, 'PASSAGE', 'x', 1, 1, 'TST')")]     // sin versículo
    [InlineData("INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title, BookNumber, Chapter, VerseStart) VALUES ({p}, 9, 'PASSAGE', 'x', 1, 1, 1)")]                 // sin Biblia
    [InlineData("INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title, BookNumber, Chapter, VerseStart, BibleAbbreviation) VALUES ({p}, 9, 'PASSAGE', 'x', 0, 1, 1, 'TST')")]    // libro 0
    [InlineData("INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title, BookNumber, Chapter, VerseStart, BibleAbbreviation) VALUES ({p}, 9, 'PASSAGE', 'x', 67, 1, 1, 'TST')")]   // libro 67 (no canónico)
    [InlineData("INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title, BookNumber, Chapter, VerseStart, BibleAbbreviation) VALUES ({p}, 9, 'PASSAGE', 'x', 1, 0, 1, 'TST')")]    // capítulo 0
    [InlineData("INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title, BookNumber, Chapter, VerseStart, BibleAbbreviation) VALUES ({p}, 9, 'PASSAGE', 'x', 1, 1, 0, 'TST')")]    // versículo 0
    [InlineData("INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title, BookNumber, Chapter, VerseStart, VerseEnd, BibleAbbreviation) VALUES ({p}, 9, 'PASSAGE', 'x', 1, 1, 5, 2, 'TST')")]   // fin antes que inicio
    [InlineData("INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title, BookNumber, Chapter, VerseStart, BibleAbbreviation) VALUES ({p}, 9, 'PASSAGE', 'x', 1, 1, 1, '')")]        // Biblia vacía
    [InlineData("INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title, BookNumber, Chapter, VerseStart, BibleAbbreviation) VALUES ({p}, 9, 'PASSAGE', 'x', 1, 1, 1, '   ')")]     // Biblia en blanco
    [InlineData("INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title, SongId, BookNumber, Chapter, VerseStart, BibleAbbreviation) VALUES ({p}, 9, 'PASSAGE', 'x', {s}, 1, 1, 1, 'TST')")]   // pasaje con canción
    public async Task Invalid_items_are_rejected_by_the_database(string template)
    {
        var (c, f) = await SeedAsync();
        await using var _ = c;
        var sql = template.Replace("{p}", f.PlaylistId.ToString()).Replace("{s}", f.SongA.ToString()).Replace("{x}", f.Presentation.ToString());

        await Assert.ThrowsAsync<SqliteException>(() => c.ExecuteAsync(sql));
    }

    [Theory]
    [InlineData(1, 1, 1, null)]
    [InlineData(66, 22, 21, null)]      // Apocalipsis, el último libro canónico
    [InlineData(43, 3, 16, 17)]
    [InlineData(43, 3, 16, 16)]         // un rango de un solo versículo
    public async Task Valid_passages_are_accepted_including_the_first_and_last_canonical_book(int book, int chapter, int first, int? last)
    {
        var (c, f) = await SeedAsync();
        await using var _ = c;

        await c.ExecuteAsync(
            "INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title, BookNumber, Chapter, VerseStart, VerseEnd, BibleAbbreviation) VALUES (@p, 9, 'PASSAGE', 'x', @b, @c, @s, @e, 'TST')",
            new { p = f.PlaylistId, b = book, c = chapter, s = first, e = last });
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

    // ───────── guardia BEFORE UPDATE: «faltante» solo nace de borrar el registro, nunca de un UPDATE a mano ─────────

    [Fact]
    public async Task A_song_that_still_exists_cannot_be_detached_by_hand()
    {
        var (c, f) = await SeedAsync();
        await using var _ = c;

        var ex = await Assert.ThrowsAsync<SqliteException>(() => c.ExecuteAsync("UPDATE PlaylistItems SET SongId = NULL WHERE SongId = @s", new { s = f.SongA }));

        Assert.Contains("solo queda faltante al borrar su registro", ex.Message);
        Assert.Equal(f.SongA, (await ItemsAsync(c, f.PlaylistId))[0].SongId);                              // sin cambios
    }

    [Fact]
    public async Task A_presentation_that_still_exists_cannot_be_detached_by_hand()
    {
        var (c, f) = await SeedAsync();
        await using var _ = c;

        await Assert.ThrowsAsync<SqliteException>(() => c.ExecuteAsync("UPDATE PlaylistItems SET PresentationId = NULL WHERE PresentationId = @x", new { x = f.Presentation }));
        Assert.Equal(f.Presentation, (await ItemsAsync(c, f.PlaylistId))[2].PresentationId);
    }

    [Fact]
    public async Task Changing_an_items_type_to_song_or_presentation_without_a_reference_is_rejected()
    {
        var (c, f) = await SeedAsync();
        await using var _ = c;

        // El CHECK ya impide la mezcla de columnas; lo que añade el guardia es el caso en que el UPDATE deja al elemento «faltante» desde su nacimiento.
        await Assert.ThrowsAsync<SqliteException>(() => c.ExecuteAsync(
            "UPDATE PlaylistItems SET ItemType = 'SONG', BookNumber = NULL, Chapter = NULL, VerseStart = NULL, VerseEnd = NULL, BibleAbbreviation = NULL WHERE ItemType = 'PASSAGE'"));
        await Assert.ThrowsAsync<SqliteException>(() => c.ExecuteAsync(
            "UPDATE PlaylistItems SET ItemType = 'PRESENTATION', SongId = NULL WHERE Id = (SELECT Id FROM PlaylistItems WHERE ItemType = 'SONG' LIMIT 1)"));
        Assert.Equal(["SONG", "SONG", "PRESENTATION", "PASSAGE"], (await ItemsAsync(c, f.PlaylistId)).Select(i => i.ItemType));
    }

    [Fact]
    public async Task An_item_can_be_repointed_to_another_existing_record_and_its_other_columns_edited_freely()
    {
        var (c, f) = await SeedAsync();
        await using var _ = c;

        await c.ExecuteAsync("UPDATE PlaylistItems SET SongId = @b WHERE SongId = @a", new { a = f.SongA, b = f.SongB });          // reapuntar: permitido
        await c.ExecuteAsync("UPDATE PlaylistItems SET Title = 'Otro título', SequenceIndex = 10 WHERE ItemType = 'PASSAGE'");     // título y orden: permitido
        await c.ExecuteAsync("UPDATE PlaylistItems SET BibleAbbreviation = 'OTRA' WHERE ItemType = 'PASSAGE'");                    // cambiar de Biblia preferida: permitido

        var items = await ItemsAsync(c, f.PlaylistId);
        Assert.Equal(f.SongB, items.First(i => i.SequenceIndex == 0).SongId);
        Assert.Equal("OTRA", items.Single(i => i.ItemType == "PASSAGE").BibleAbbreviation);
    }

    [Fact]
    public async Task The_guard_does_not_get_in_the_way_of_deleting_the_record_which_is_how_an_item_becomes_missing()
    {
        var (c, f) = await SeedAsync();
        await using var _ = c;

        await c.ExecuteAsync("DELETE FROM Songs WHERE Id = @s", new { s = f.SongA });
        await c.ExecuteAsync("DELETE FROM Presentations WHERE Id = @x", new { x = f.Presentation });

        Assert.Equal([1L, 0L, 1L, 0L], (await ItemsAsync(c, f.PlaylistId)).Select(i => i.IsMissing));
        // Ya faltante, tocar la referencia (a NULL otra vez) no es un error: el registro ya no existe.
        await c.ExecuteAsync("UPDATE PlaylistItems SET SongId = NULL WHERE ItemType = 'SONG' AND SongId IS NULL");
    }
}
