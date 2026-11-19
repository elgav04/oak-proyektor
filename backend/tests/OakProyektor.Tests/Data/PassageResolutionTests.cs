using Dapper;
using Microsoft.Data.Sqlite;
using OakProyektor.Core.Library;
using OakProyektor.Data;

namespace OakProyektor.Tests.Data;

/// <summary>
/// Un pasaje se guarda por referencia canónica (libro 1 a 66, capítulo, versículos y la abreviatura de la Biblia como TEXTO) y se RESUELVE al cargar el
/// servicio. Aquí: reimportar o actualizar una Biblia no rompe los pasajes, y una Biblia ausente degrada con aviso, nunca con pérdida.
/// Todo el texto bíblico de estas pruebas es sintético (CLAUDE.md: nada con derechos de autor en el repositorio).
/// </summary>
public sealed class PassageResolutionTests : IDisposable
{
    private readonly TestDatabase _db = new();
    public void Dispose() => _db.Dispose();

    private static readonly PassageReference Tst43_3_16_17 = new(43, 3, 16, 17, "TST");

    private async Task<SqliteConnection> OpenAsync()
    {
        await _db.InitializeAsync();
        return await _db.OpenAsync();
    }

    /// <summary>Instala una Biblia sintética con un libro y sus versículos (como lo haría un importador la primera vez).</summary>
    private static async Task<(long BibleId, long BookId)> InstallAsync(SqliteConnection c, string abbreviation, int bookNumber, params (int Chapter, int Verse, string Text)[] verses)
    {
        var bibleId = await c.ExecuteScalarAsync<long>("INSERT INTO Bibles (Abbreviation, Name, Language) VALUES (@a, @n, 'es'); SELECT last_insert_rowid();", new { a = abbreviation, n = $"Biblia {abbreviation}" });
        var bookId = await c.ExecuteScalarAsync<long>("INSERT INTO Books (BibleId, BookNumber, Name, Testament) VALUES (@b, @n, @name, 'NT'); SELECT last_insert_rowid();", new { b = bibleId, n = bookNumber, name = $"Libro {bookNumber}" });
        foreach (var (chapter, verse, text) in verses)
            await c.ExecuteAsync("INSERT INTO Verses (BookId, Chapter, VerseNumber, Text) VALUES (@k, @c, @v, @t)", new { k = bookId, c = chapter, v = verse, t = text });
        return (bibleId, bookId);
    }

    /// <summary>La regla del importador (CLAUDE.md): actualizar EN EL SITIO, con upsert por libro, capítulo y versículo, dentro de UNA transacción, conservando el BibleId.</summary>
    private static async Task ImportInPlaceAsync(SqliteConnection c, string abbreviation, int bookNumber, params (int Chapter, int Verse, string Text)[] verses)
    {
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync();
        var bibleId = await c.ExecuteScalarAsync<long>(
            "INSERT INTO Bibles (Abbreviation, Name, Language) VALUES (@a, @n, 'es') ON CONFLICT(Abbreviation) DO UPDATE SET Name = excluded.Name RETURNING Id",
            new { a = abbreviation, n = $"Biblia {abbreviation} (reimportada)" }, tx);
        var bookId = await c.ExecuteScalarAsync<long>(
            "INSERT INTO Books (BibleId, BookNumber, Name, Testament) VALUES (@b, @n, @name, 'NT') ON CONFLICT(BibleId, BookNumber) DO UPDATE SET Name = excluded.Name RETURNING Id",
            new { b = bibleId, n = bookNumber, name = $"Libro {bookNumber}" }, tx);
        foreach (var (chapter, verse, text) in verses)
            await c.ExecuteAsync(
                "INSERT INTO Verses (BookId, Chapter, VerseNumber, Text) VALUES (@k, @c, @v, @t) " +
                "ON CONFLICT(BookId, Chapter, VerseNumber) DO UPDATE SET Text = excluded.Text WHERE Text IS NOT excluded.Text",
                new { k = bookId, c = chapter, v = verse, t = text }, tx);
        await tx.CommitAsync();
    }

    private static async Task<(long PlaylistId, long ItemId)> AddServiceWithPassageAsync(SqliteConnection c, string abbreviation = "TST")
    {
        var pl = await c.ExecuteScalarAsync<long>("INSERT INTO Playlists (Name) VALUES ('Domingo'); SELECT last_insert_rowid();");
        var item = await c.ExecuteScalarAsync<long>(
            "INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title, BookNumber, Chapter, VerseStart, VerseEnd, BibleAbbreviation) " +
            "VALUES (@p, 0, 'PASSAGE', 'Libro 43 3:16-17', 43, 3, 16, 17, @a); SELECT last_insert_rowid();", new { p = pl, a = abbreviation });
        return (pl, item);
    }

    /// <summary>Todas las filas de PlaylistItems como texto, para comprobar que NADA cambió.</summary>
    private static async Task<string> SnapshotAsync(SqliteConnection c) =>
        string.Join("\n", (await c.QueryAsync("SELECT * FROM PlaylistItems ORDER BY Id")).Select(r => string.Join("|", ((IDictionary<string, object>)r).Select(kv => $"{kv.Key}={kv.Value}"))));

    private static readonly (int, int, string)[] ThreeVerses = [(3, 16, "texto sintético uno"), (3, 17, "texto sintético dos"), (3, 18, "texto sintético tres")];

    // ───────────────────────────── resolución ─────────────────────────────

    [Fact]
    public async Task A_passage_whose_bible_is_installed_resolves()
    {
        await using var c = await OpenAsync();
        var (bibleId, bookId) = await InstallAsync(c, "TST", 43, ThreeVerses);

        var r = await PassageResolver.ResolveAsync(c, Tst43_3_16_17, null, CancellationToken.None);

        Assert.Equal((PassageStatus.Resolved, bibleId, bookId, 2), (r.Status, r.BibleId, r.BookId, r.VersesFound));
        Assert.True(r.IsResolved);
        Assert.Null(r.Warning);
        Assert.Null(r.Offered);
    }

    [Fact]
    public async Task A_single_verse_and_a_range_are_both_resolved_and_gaps_in_the_range_do_not_fail()
    {
        await using var c = await OpenAsync();
        await InstallAsync(c, "TST", 43, (3, 16, "a"), (3, 18, "c"));                  // falta el 17 (numeraciones distintas entre Biblias)

        Assert.Equal(1, (await PassageResolver.ResolveAsync(c, new PassageReference(43, 3, 16, null, "TST"), null, CancellationToken.None)).VersesFound);
        var range = await PassageResolver.ResolveAsync(c, new PassageReference(43, 3, 16, 18, "TST"), null, CancellationToken.None);
        Assert.Equal((PassageStatus.Resolved, 2), (range.Status, range.VersesFound));
    }

    [Fact]
    public async Task A_bible_that_is_not_installed_degrades_to_a_warning_and_the_item_is_left_exactly_as_it_was()
    {
        await using var c = await OpenAsync();
        var (playlistId, itemId) = await AddServiceWithPassageAsync(c, "XYZ");           // la Biblia «XYZ» nunca se instaló
        var before = await SnapshotAsync(c);

        var results = await PassageResolver.ResolvePlaylistAsync(c, playlistId, null, CancellationToken.None);

        var (id, resolution) = Assert.Single(results);
        Assert.Equal(itemId, id);
        Assert.Equal(PassageStatus.BibleNotInstalled, resolution.Status);
        Assert.Equal("Biblia no instalada: XYZ", resolution.Warning);
        Assert.Null(resolution.BibleId);
        Assert.Null(resolution.Offered);                                                  // sin Biblia por defecto no hay nada que ofrecer
        Assert.Equal(before, await SnapshotAsync(c));                                     // resolver NUNCA modifica el elemento
        Assert.Equal(0L, await c.ExecuteScalarAsync<long>("SELECT IsMissing FROM PlaylistItems WHERE Id = @i", new { i = itemId }));   // ni lo marca como perdido
    }

    [Fact]
    public async Task When_the_bible_is_not_installed_the_default_bible_is_offered_but_never_applied()
    {
        await using var c = await OpenAsync();
        var (playlistId, _) = await AddServiceWithPassageAsync(c, "XYZ");
        var (defaultId, defaultBook) = await InstallAsync(c, "DEF", 43, ThreeVerses);
        var before = await SnapshotAsync(c);

        var (_, resolution) = Assert.Single(await PassageResolver.ResolvePlaylistAsync(c, playlistId, "DEF", CancellationToken.None));

        Assert.Equal(PassageStatus.BibleNotInstalled, resolution.Status);
        Assert.Equal("Biblia no instalada: XYZ", resolution.Warning);
        Assert.NotNull(resolution.Offered);
        Assert.Equal((PassageStatus.Resolved, "DEF", defaultId, defaultBook, 2), (resolution.Offered.Status, resolution.Offered.PreferredBibleAbbreviation, resolution.Offered.BibleId, resolution.Offered.BookId, resolution.Offered.VersesFound));
        Assert.Equal(before, await SnapshotAsync(c));                                     // se OFRECE; el elemento conserva su Biblia preferida («XYZ») hasta que el operador elija
    }

    [Fact]
    public async Task Nothing_is_offered_when_the_default_bible_is_not_installed_lacks_the_passage_or_is_the_missing_one_itself()
    {
        await using var c = await OpenAsync();
        await InstallAsync(c, "NOPE", 1, (1, 1, "otro libro"));                           // «NOPE» está instalada pero NO tiene el libro 43

        Assert.Null((await PassageResolver.ResolveAsync(c, new PassageReference(43, 3, 16, 17, "XYZ"), "NO-INSTALADA", CancellationToken.None)).Offered);
        Assert.Null((await PassageResolver.ResolveAsync(c, new PassageReference(43, 3, 16, 17, "XYZ"), "NOPE", CancellationToken.None)).Offered);
        Assert.Null((await PassageResolver.ResolveAsync(c, new PassageReference(43, 3, 16, 17, "XYZ"), "XYZ", CancellationToken.None)).Offered);   // la «por defecto» es la que falta
        Assert.Null((await PassageResolver.ResolveAsync(c, new PassageReference(43, 3, 16, 17, "XYZ"), "", CancellationToken.None)).Offered);
    }

    [Fact]
    public async Task An_installed_bible_that_lacks_the_book_chapter_or_verses_is_passage_not_found_not_a_crash_and_not_a_loss()
    {
        await using var c = await OpenAsync();
        var (bibleId, bookId) = await InstallAsync(c, "TST", 43, ThreeVerses);

        var noBook = await PassageResolver.ResolveAsync(c, new PassageReference(44, 3, 16, 17, "TST"), null, CancellationToken.None);
        var noChapter = await PassageResolver.ResolveAsync(c, new PassageReference(43, 99, 1, 1, "TST"), null, CancellationToken.None);
        var noVerses = await PassageResolver.ResolveAsync(c, new PassageReference(43, 3, 50, 60, "TST"), null, CancellationToken.None);

        Assert.Equal((PassageStatus.PassageNotFound, bibleId, (long?)null), (noBook.Status, noBook.BibleId, noBook.BookId));
        Assert.Equal((PassageStatus.PassageNotFound, bibleId, bookId, 0), (noChapter.Status, noChapter.BibleId, noChapter.BookId, noChapter.VersesFound));
        Assert.Equal(PassageStatus.PassageNotFound, noVerses.Status);
        Assert.Equal("Pasaje no encontrado en TST", noVerses.Warning);
    }

    [Fact]
    public async Task The_default_bible_is_also_offered_when_the_preferred_one_is_installed_but_incomplete()
    {
        await using var c = await OpenAsync();
        await InstallAsync(c, "TST", 1, (1, 1, "solo el libro 1"));
        await InstallAsync(c, "DEF", 43, ThreeVerses);

        var r = await PassageResolver.ResolveAsync(c, Tst43_3_16_17, "DEF", CancellationToken.None);

        Assert.Equal(PassageStatus.PassageNotFound, r.Status);
        Assert.Equal(PassageStatus.Resolved, r.Offered?.Status);
    }

    [Fact]
    public async Task The_abbreviation_is_compared_exactly_so_a_different_case_is_a_different_bible()
    {
        await using var c = await OpenAsync();
        await InstallAsync(c, "tst", 43, ThreeVerses);                                    // «tst» en minúsculas

        Assert.Equal(PassageStatus.BibleNotInstalled, (await PassageResolver.ResolveAsync(c, Tst43_3_16_17, null, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Resolving_a_service_returns_only_its_passages_in_service_order_each_with_its_own_status()
    {
        await using var c = await OpenAsync();
        await InstallAsync(c, "TST", 43, ThreeVerses);
        var song = await c.ExecuteScalarAsync<long>("INSERT INTO Songs (Title) VALUES ('Canción'); SELECT last_insert_rowid();");
        var pl = await c.ExecuteScalarAsync<long>("INSERT INTO Playlists (Name) VALUES ('Domingo'); SELECT last_insert_rowid();");
        await c.ExecuteAsync("INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title, SongId) VALUES (@p, 0, 'SONG', 'Canción', @s)", new { p = pl, s = song });
        var ok = await c.ExecuteScalarAsync<long>("INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title, BookNumber, Chapter, VerseStart, BibleAbbreviation) VALUES (@p, 2, 'PASSAGE', 'ok', 43, 3, 16, 'TST'); SELECT last_insert_rowid();", new { p = pl });
        var gone = await c.ExecuteScalarAsync<long>("INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title, BookNumber, Chapter, VerseStart, BibleAbbreviation) VALUES (@p, 1, 'PASSAGE', 'sin Biblia', 43, 3, 16, 'XYZ'); SELECT last_insert_rowid();", new { p = pl });

        var results = await PassageResolver.ResolvePlaylistAsync(c, pl, null, CancellationToken.None);

        Assert.Equal([gone, ok], results.Select(r => r.ItemId));                          // en el orden del servicio; la canción no aparece
        Assert.Equal([PassageStatus.BibleNotInstalled, PassageStatus.Resolved], results.Select(r => r.Resolution.Status));
    }

    // ───────────────────────────── reimportar / actualizar una Biblia ─────────────────────────────

    [Fact]
    public async Task Updating_a_bible_in_place_keeps_its_ids_and_never_breaks_a_passage_of_any_service()
    {
        await using var c = await OpenAsync();
        var (bibleId, bookId) = await InstallAsync(c, "TST", 43, ThreeVerses);
        var (playlistId, _) = await AddServiceWithPassageAsync(c);
        var before = await SnapshotAsync(c);

        // Reimportación con texto corregido y un versículo nuevo, en el sitio (la regla del importador).
        await ImportInPlaceAsync(c, "TST", 43, (3, 16, "texto sintético UNO corregido"), (3, 17, "texto sintético dos"), (3, 18, "texto sintético tres"), (3, 19, "versículo nuevo"));

        Assert.Equal(bibleId, await c.ExecuteScalarAsync<long>("SELECT Id FROM Bibles WHERE Abbreviation = 'TST'"));      // el BibleId se conserva
        Assert.Equal(bookId, await c.ExecuteScalarAsync<long>("SELECT Id FROM Books WHERE BibleId = @b AND BookNumber = 43", new { b = bibleId }));
        Assert.Equal(4L, await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Verses WHERE BookId = @k", new { k = bookId }));   // sin duplicados
        Assert.Equal("texto sintético UNO corregido", await c.ExecuteScalarAsync<string>("SELECT Text FROM Verses WHERE BookId = @k AND Chapter = 3 AND VerseNumber = 16", new { k = bookId }));
        Assert.Equal(before, await SnapshotAsync(c));                                                                       // el servicio no se tocó

        var (_, resolution) = Assert.Single(await PassageResolver.ResolvePlaylistAsync(c, playlistId, null, CancellationToken.None));
        Assert.Equal((PassageStatus.Resolved, bibleId, bookId), (resolution.Status, resolution.BibleId, resolution.BookId));

        // El FTS quedó consistente tras la importación (CLAUDE.md) y el texto nuevo se encuentra.
        await c.ExecuteAsync("INSERT INTO VersesFts (VersesFts, rank) VALUES ('integrity-check', 1)");
        Assert.Equal(1L, await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM VersesFts WHERE VersesFts MATCH 'corregido'"));
        Assert.Equal(0L, await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM VersesFts WHERE VersesFts MATCH 'uno' AND rowid IN (SELECT Id FROM Verses WHERE Text NOT LIKE '%corregido%')"));
    }

    [Fact]
    public async Task Importing_the_same_bible_again_is_idempotent_changes_nothing_and_stays_atomic()
    {
        await using var c = await OpenAsync();
        var (bibleId, _) = await InstallAsync(c, "TST", 43, ThreeVerses);

        await ImportInPlaceAsync(c, "TST", 43, ThreeVerses);
        await ImportInPlaceAsync(c, "TST", 43, ThreeVerses);

        Assert.Equal(1L, await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Bibles"));
        Assert.Equal(3L, await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Verses"));
        Assert.Equal(bibleId, await c.ExecuteScalarAsync<long>("SELECT Id FROM Bibles WHERE Abbreviation = 'TST'"));
        await c.ExecuteAsync("INSERT INTO VersesFts (VersesFts, rank) VALUES ('integrity-check', 1)");
    }

    [Fact]
    public async Task Even_the_forbidden_delete_and_import_again_loses_no_passage_it_only_degrades_while_the_bible_is_absent()
    {
        // La regla del importador pide actualizar en el sitio; pero la referencia canónica hace que, incluso si alguien borra e importa de nuevo,
        // NO se pierda ningún pasaje: antes de este cambio, TODOS quedaban marcados como faltantes.
        await using var c = await OpenAsync();
        var (oldBibleId, _) = await InstallAsync(c, "TST", 43, ThreeVerses);
        await InstallAsync(c, "ZZZ", 1, (1, 1, "otra Biblia"));                              // con otra Biblia detrás, SQLite no reutiliza el id al reimportar
        var (playlistId, itemId) = await AddServiceWithPassageAsync(c);
        var before = await SnapshotAsync(c);

        await c.ExecuteAsync("DELETE FROM Bibles WHERE Abbreviation = 'TST'");
        var whileAbsent = Assert.Single(await PassageResolver.ResolvePlaylistAsync(c, playlistId, null, CancellationToken.None));
        Assert.Equal(PassageStatus.BibleNotInstalled, whileAbsent.Resolution.Status);       // degrada con aviso…
        Assert.Equal(before, await SnapshotAsync(c));                                       // …sin perder nada
        Assert.Equal(0L, await c.ExecuteScalarAsync<long>("SELECT IsMissing FROM PlaylistItems WHERE Id = @i", new { i = itemId }));

        var (newBibleId, _) = await InstallAsync(c, "TST", 43, ThreeVerses);                // se reimporta (otro BibleId)
        Assert.NotEqual(oldBibleId, newBibleId);
        var recovered = Assert.Single(await PassageResolver.ResolvePlaylistAsync(c, playlistId, null, CancellationToken.None));
        Assert.Equal((PassageStatus.Resolved, newBibleId), (recovered.Resolution.Status, recovered.Resolution.BibleId));
        Assert.Equal(before, await SnapshotAsync(c));
    }

    [Fact]
    public async Task A_verse_is_unique_per_book_chapter_and_number_which_is_what_makes_the_importers_upsert_possible()
    {
        await using var c = await OpenAsync();
        var (_, bookId) = await InstallAsync(c, "TST", 43, (3, 16, "uno"));

        await Assert.ThrowsAsync<SqliteException>(() =>
            c.ExecuteAsync("INSERT INTO Verses (BookId, Chapter, VerseNumber, Text) VALUES (@k, 3, 16, 'duplicado')", new { k = bookId }));
        Assert.Equal(1L, await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM pragma_index_list('Verses') WHERE name = 'IX_Verses_BookId_Chapter_VerseNumber' AND \"unique\" = 1"));
    }

    // ───────────────────────────── el modelo de Core ─────────────────────────────

    [Fact]
    public void A_passage_reference_is_built_from_a_passage_item_and_only_from_a_complete_one()
    {
        var passage = new PlaylistItem(1, 1, 0, PlaylistItemTypes.Passage, "Libro 43 3:16-17", null, null, 43, 3, 16, 17, "TST", false);
        var single = passage with { VerseEnd = null };

        Assert.Equal(new PassageReference(43, 3, 16, 17, "TST"), PassageReference.From(passage));
        Assert.Equal(16, PassageReference.From(single)!.LastVerse);                          // un solo versículo: el final es el inicial
        Assert.Null(PassageReference.From(passage with { ItemType = PlaylistItemTypes.Song, SongId = 5, BookNumber = null }));
        Assert.Null(PassageReference.From(passage with { BibleAbbreviation = null }));
        Assert.Null(PassageReference.From(passage with { Chapter = null }));
    }
}
