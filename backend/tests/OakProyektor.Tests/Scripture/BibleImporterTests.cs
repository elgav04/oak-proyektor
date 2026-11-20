using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using OakProyektor.Core.Library;
using OakProyektor.Core.Scripture;
using OakProyektor.Data;
using OakProyektor.Data.Scripture;

namespace OakProyektor.Tests.Scripture;

public sealed class BibleImporterTests : IAsyncLifetime
{
    private BibleEnv _env = null!;

    public async Task InitializeAsync() => _env = await BibleEnv.CreateAsync();
    public Task DisposeAsync() { _env.Dispose(); return Task.CompletedTask; }

    private async Task<long> CountAsync(string sql, object? args = null)
    {
        await using var c = await _env.OpenAsync();
        return await c.ExecuteScalarAsync<long>(sql, args);
    }

    // ───────────────────────────── primera importación ─────────────────────────────

    [Fact]
    public async Task First_import_creates_the_bible_books_and_verses_and_reports_counts()
    {
        var small = SyntheticBible.Small();

        var report = await _env.ImportAsync(small);

        Assert.True(report.CreatedBible);
        Assert.Equal(small.Count, report.SourceVerses);
        Assert.Equal((small.Count, 0, 0, 0), (report.Inserted, report.Updated, report.Unchanged, report.Deleted));
        Assert.Equal(small.Count, await CountAsync("SELECT COUNT(*) FROM Verses"));
        Assert.Equal(5, await CountAsync("SELECT COUNT(*) FROM Books WHERE BibleId = @id", new { id = report.BibleId }));
        Assert.Equal("youversion-json", report.FormatId);
        Assert.Equal("none", report.NumberingProfile);
        Assert.True(report.FtsIntegrityOk);
        Assert.Equal(2, report.DuplicateChaptersDiscarded);        // 3 Juan y Judas (libros de un solo capítulo) lo repiten en el archivo sintético
        Assert.Equal(0, report.JoinedVerses);
    }

    [Fact]
    public async Task Books_carry_the_canonical_name_number_and_testament()
    {
        var report = await _env.ImportAsync(SyntheticBible.Small());

        await using var c = await _env.OpenAsync();
        var books = (await c.QueryAsync<(long Number, string Name, string Testament)>(
            "SELECT BookNumber, Name, Testament FROM Books WHERE BibleId = @id ORDER BY BookNumber", new { id = report.BibleId })).ToList();
        Assert.Equal([(1L, "Génesis", "OT"), (19L, "Salmos", "OT"), (40L, "Mateo", "NT"), (64L, "3 Juan", "NT"), (65L, "Judas", "NT")], books.Select(b => (b.Number, b.Name, b.Testament)));
    }

    [Fact]
    public async Task The_bible_row_stores_name_language_attribution_and_profile()
    {
        var report = await _env.ImportAsync(SyntheticBible.Small(), "SIN", profile: "merge-ps47-3jn", attribution: "Aviso de prueba", name: "Mi Biblia");

        await using var c = await _env.OpenAsync();
        var row = await c.QuerySingleAsync<(string Abbreviation, string Name, string Language, string Attribution, string? Profile)>(
            "SELECT Abbreviation, Name, Language, Attribution, NumberingProfile FROM Bibles WHERE Id = @id", new { id = report.BibleId });
        Assert.Equal(("SIN", "Mi Biblia", "es", "Aviso de prueba", "merge-ps47-3jn"), row);
    }

    [Fact]
    public async Task The_attribution_is_empty_by_default_nothing_is_preinstalled()
    {
        var report = await _env.ImportAsync(SyntheticBible.Small());
        Assert.Equal("", await _env_Scalar<string>("SELECT Attribution FROM Bibles WHERE Id = @id", new { id = report.BibleId }));
        Assert.Null(await _env_Scalar<string?>("SELECT NumberingProfile FROM Bibles WHERE Id = @id", new { id = report.BibleId }));
    }

    private async Task<T> _env_Scalar<T>(string sql, object args)
    {
        await using var c = await _env.OpenAsync();
        return (await c.ExecuteScalarAsync<T>(sql, args))!;
    }

    [Fact]
    public async Task The_cleaning_is_counted_and_applied_without_showing_any_text()
    {
        var verses = SyntheticBible.Chapter(1, 1, 4).ToList();
        verses[0] = verses[0] with { Text = "  dos   espacios  " };
        verses[1] = verses[1] with { Text = "con\u00A0nbsp" };
        verses[2] = verses[2] with { Text = "Jesús" };                       // NFD
        // el cuarto ya está limpio

        var report = await _env.ImportAsync(verses);

        Assert.Equal(3, report.CleanedVerses);
        await using var c = await _env.OpenAsync();
        Assert.Equal(["dos espacios", "con nbsp", "Jesús", SyntheticBible.TextOf(1, 1, 4)],
            (await c.QueryAsync<string>("SELECT Text FROM Verses ORDER BY VerseNumber")).ToList());
        Assert.Contains(report.Warnings, w => w.Contains("3 versículos se limpiaron"));
    }

    [Fact]
    public async Task Progress_goes_through_the_phases_in_order_and_carries_only_numbers()
    {
        var progress = new RecordingProgress();

        await _env.ImportAsync(SyntheticBible.Small(), progress: progress);

        var phases = progress.Reports.Select(r => r.Phase).Distinct().ToList();
        Assert.Equal(["reading", "cleaning", "saving", "validating"], phases);
        Assert.All(progress.Reports, r => Assert.InRange(r.Done, 0, Math.Max(r.Total, 1)));
        Assert.Equal(new ImportProgress("validating", 1, 1), progress.Reports[^1]);
    }

    // ───────────────────────────── reimportar: en el sitio ─────────────────────────────

    [Fact]
    public async Task Reimporting_the_same_file_keeps_the_BibleId_and_every_row_id_and_changes_nothing()
    {
        var small = SyntheticBible.Small();
        var first = await _env.ImportAsync(small);
        var verseIds = await ReadAllAsync<(long Id, long BookId, long Chapter, long Verse)>("SELECT Id, BookId, Chapter, VerseNumber FROM Verses ORDER BY Id");
        var bookIds = await ReadAllAsync<(long Id, long Number)>("SELECT Id, BookNumber FROM Books ORDER BY Id");

        var second = await _env.ImportAsync(small);

        Assert.Equal(first.BibleId, second.BibleId);
        Assert.False(second.CreatedBible);
        Assert.Equal((0, 0, small.Count, 0), (second.Inserted, second.Updated, second.Unchanged, second.Deleted));
        Assert.Equal(verseIds, await ReadAllAsync<(long Id, long BookId, long Chapter, long Verse)>("SELECT Id, BookId, Chapter, VerseNumber FROM Verses ORDER BY Id"));
        Assert.Equal(bookIds, await ReadAllAsync<(long Id, long Number)>("SELECT Id, BookNumber FROM Books ORDER BY Id"));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM Bibles"));
    }

    private async Task<List<T>> ReadAllAsync<T>(string sql)
    {
        await using var c = await _env.OpenAsync();
        return (await c.QueryAsync<T>(sql)).ToList();
    }

    [Fact]
    public async Task Reimporting_does_not_break_the_passages_saved_in_services()
    {
        var small = SyntheticBible.Small();
        var first = await _env.ImportAsync(small);
        long playlistId;
        await using (var c = await _env.OpenAsync())
        {
            playlistId = await c.ExecuteScalarAsync<long>("INSERT INTO Playlists (Name) VALUES ('Culto'); SELECT last_insert_rowid();");
            await c.ExecuteAsync(
                """
                INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title, BookNumber, Chapter, VerseStart, VerseEnd, BibleAbbreviation)
                VALUES (@p, 0, 'PASSAGE', 'Salmo 47', 19, 47, 1, 5, 'SIN'), (@p, 1, 'PASSAGE', 'Mateo 5', 40, 5, 3, NULL, 'SIN')
                """, new { p = playlistId });
        }

        var second = await _env.ImportAsync(small);   // la reimportación

        Assert.Equal(first.BibleId, second.BibleId);
        Assert.Empty(second.AffectedItems);
        await using var conn = await _env.OpenAsync();
        var resolutions = await PassageResolver.ResolvePlaylistAsync(conn, playlistId, defaultBibleAbbreviation: null, CancellationToken.None);
        Assert.Equal(2, resolutions.Count);
        Assert.All(resolutions, r => Assert.Equal(PassageStatus.Resolved, r.Resolution.Status));
        Assert.Equal(2, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM PlaylistItems WHERE IsMissing = 0"));
    }

    [Fact]
    public async Task A_changed_verse_is_updated_in_place_and_the_search_index_follows()
    {
        var small = SyntheticBible.Small();
        await _env.ImportAsync(small);
        var idBefore = await CountAsync("SELECT Id FROM Verses WHERE Chapter = 5 AND VerseNumber = 3 AND BookId IN (SELECT Id FROM Books WHERE BookNumber = 40)");

        var edited = small.Select(v => v is { Book: 40, Chapter: 5, Verse: 3 } ? v with { Text = "Cambiadoxyz nuevo texto inventado" } : v).ToList();
        var report = await _env.ImportAsync(edited);

        Assert.Equal((0, 1, small.Count - 1, 0), (report.Inserted, report.Updated, report.Unchanged, report.Deleted));
        Assert.Equal(idBefore, await CountAsync("SELECT Id FROM Verses WHERE Chapter = 5 AND VerseNumber = 3 AND BookId IN (SELECT Id FROM Books WHERE BookNumber = 40)"));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM VersesFts WHERE VersesFts MATCH 'cambiadoxyz'"));
        Assert.Equal(0, await CountAsync($"SELECT COUNT(*) FROM VersesFts WHERE VersesFts MATCH '{SyntheticBible.TextOf(40, 5, 3).Split(' ')[0]}'"));
        Assert.True(report.FtsIntegrityOk);
    }

    [Fact]
    public async Task A_new_verse_in_the_source_is_inserted_and_the_name_and_language_follow_the_request()
    {
        var small = SyntheticBible.Small();
        await _env.ImportAsync(small, name: "Nombre viejo");

        var report = await _env.ImportAsync([.. small, SyntheticBible.Verse(1, 3, 1)], name: "Nombre nuevo");

        Assert.Equal(1, report.Inserted);
        Assert.Equal("Nombre nuevo", await _env_Scalar<string>("SELECT Name FROM Bibles WHERE Abbreviation = 'SIN'", new { }));
    }

    [Fact]
    public async Task The_attribution_is_kept_on_reimport_unless_a_new_one_is_given()
    {
        await _env.ImportAsync(SyntheticBible.Small(), attribution: "Editado por el operador");
        await _env.ImportAsync(SyntheticBible.Small());                                       // sin atribución: no la toca
        Assert.Equal("Editado por el operador", await _env_Scalar<string>("SELECT Attribution FROM Bibles WHERE Abbreviation = 'SIN'", new { }));

        await _env.ImportAsync(SyntheticBible.Small(), attribution: "");                      // vacía: la quita
        Assert.Equal("", await _env_Scalar<string>("SELECT Attribution FROM Bibles WHERE Abbreviation = 'SIN'", new { }));
    }

    [Fact]
    public async Task Two_bibles_coexist_and_one_never_touches_the_other()
    {
        var a = await _env.ImportAsync(SyntheticBible.Small(), "AAA");
        var b = await _env.ImportAsync(SyntheticBible.Chapter(1, 1, 3), "BBB");

        Assert.NotEqual(a.BibleId, b.BibleId);
        var reimport = await _env.ImportAsync(SyntheticBible.Chapter(1, 1, 3), "BBB");
        Assert.Equal(0, reimport.Deleted);
        Assert.Equal(SyntheticBible.Small().Count, await CountAsync("SELECT COUNT(*) FROM Verses v JOIN Books b ON b.Id = v.BookId WHERE b.BibleId = @id", new { id = a.BibleId }));
    }

    // ───────────────────────────── perfil de numeración y eliminación explícita ─────────────────────────────

    [Fact]
    public async Task The_numbering_profile_merges_verses_on_import()
    {
        var report = await _env.ImportAsync(SyntheticBible.Small(), profile: "merge-ps47-3jn");

        Assert.Equal(["PSA 47:9-10", "3JN 1:14-15"], report.MergesApplied);
        Assert.Equal(SyntheticBible.Small().Count - 2, report.SourceVerses);
        Assert.Equal($"{SyntheticBible.TextOf(19, 47, 9)} {SyntheticBible.TextOf(19, 47, 10)}",
            await _env_Scalar<string>("SELECT v.Text FROM Verses v JOIN Books b ON b.Id = v.BookId WHERE b.BookNumber = 19 AND v.Chapter = 47 AND v.VerseNumber = 9", new { }));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM Verses v JOIN Books b ON b.Id = v.BookId WHERE b.BookNumber = 19 AND v.Chapter = 47 AND v.VerseNumber = 10"));
    }

    [Fact]
    public async Task Changing_the_profile_deletes_the_verses_that_no_longer_exist_in_the_same_transaction_and_warns_about_what_pointed_to_them()
    {
        var small = SyntheticBible.Small();
        await _env.ImportAsync(small);                        // sin perfil: Salmo 47 con 10 versículos y 3 Juan con 15
        long playlistId;
        await using (var c = await _env.OpenAsync())
        {
            playlistId = await c.ExecuteScalarAsync<long>("INSERT INTO Playlists (Name) VALUES ('Culto'); SELECT last_insert_rowid();");
            await c.ExecuteAsync(
                """
                INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title, BookNumber, Chapter, VerseStart, VerseEnd, BibleAbbreviation) VALUES
                  (@p, 0, 'PASSAGE', 'a', 19, 47, 10, NULL, 'SIN'),      -- el versículo eliminado
                  (@p, 1, 'PASSAGE', 'b', 19, 47, 9, 10, 'SIN'),         -- un rango que lo incluye
                  (@p, 2, 'PASSAGE', 'c', 19, 47, 1, 8, 'SIN'),          -- no lo toca
                  (@p, 3, 'PASSAGE', 'd', 19, 47, 10, NULL, 'OTRA'),     -- otra Biblia: no se avisa
                  (@p, 4, 'PASSAGE', 'e', 64, 1, 15, NULL, 'SIN')        -- 3 Juan 15, eliminado también
                """, new { p = playlistId });
            await c.ExecuteAsync(
                """
                INSERT INTO CitationHistory (BibleAbbreviation, BookNumber, Chapter, VerseStart, VerseEnd, Label) VALUES
                  ('SIN', 19, 47, NULL, NULL, 'Sal 47'),                 -- capítulo entero: lo incluye
                  ('SIN', 19, 47, 2, 3, 'Sal 47:2-3')                    -- no lo toca
                """);
        }

        var report = await _env.ImportAsync(small, profile: "merge-ps47-3jn");

        Assert.Equal((0, 2, small.Count - 4, 2), (report.Inserted, report.Updated, report.Unchanged, report.Deleted));
        Assert.Equal(["PSA 47:10", "PSA 47:9-10", "3JN 1:15", "PSA 47"], report.AffectedItems.Select(a => a.Reference));
        Assert.Equal(["playlist", "playlist", "playlist", "history"], report.AffectedItems.Select(a => a.Kind));
        Assert.All(report.AffectedItems.Where(a => a.Kind == "playlist"), a => Assert.Equal(playlistId, a.PlaylistId));
        Assert.Equal(4, report.Warnings.Count(w => w.Contains("Apuntaba a un versículo eliminado")));
        Assert.Contains(report.Warnings, w => w.Contains("El perfil de numeración cambió de «none» a «merge-ps47-3jn»"));
        Assert.Equal("merge-ps47-3jn", await _env_Scalar<string>("SELECT NumberingProfile FROM Bibles WHERE Abbreviation = 'SIN'", new { }));

        // Nada se rompió: los elementos siguen ahí (el pasaje de 47:10 se resuelve ahora con el 9 unido), y el índice FTS está sincronizado.
        Assert.Equal(5, await CountAsync("SELECT COUNT(*) FROM PlaylistItems"));
        Assert.True(report.FtsIntegrityOk);
        await using var conn = await _env.OpenAsync();
        Assert.Equal(1, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM VersesFts WHERE VersesFts MATCH @m", new { m = SyntheticBible.TextOf(19, 47, 10).Split(' ')[0] }));
    }

    [Fact]
    public async Task Going_back_to_no_profile_inserts_the_split_verses_again_without_changing_the_BibleId()
    {
        var small = SyntheticBible.Small();
        var merged = await _env.ImportAsync(small, profile: "merge-ps47-3jn");

        var split = await _env.ImportAsync(small);

        Assert.Equal(merged.BibleId, split.BibleId);
        Assert.Equal(2, split.Inserted);
        Assert.Equal(2, split.Updated);                      // 47:9 y 3 Juan 14 recuperan su texto propio
        Assert.Equal(0, split.Deleted);
        Assert.Equal(SyntheticBible.TextOf(19, 47, 9), await _env_Scalar<string>("SELECT v.Text FROM Verses v JOIN Books b ON b.Id = v.BookId WHERE b.BookNumber = 19 AND v.Chapter = 47 AND v.VerseNumber = 9", new { }));
        Assert.Null(await _env_Scalar<string?>("SELECT NumberingProfile FROM Bibles WHERE Abbreviation = 'SIN'", new { }));
    }

    [Fact]
    public async Task A_book_that_the_source_no_longer_has_is_removed_explicitly_with_its_verses_and_the_search_index_follows()
    {
        var small = SyntheticBible.Small();
        await _env.ImportAsync(small);

        // Sin Judas (6 versículos): quitar un libro entero es una eliminación explícita de sus versículos y de su fila de libro.
        var withoutJudas = small.Where(v => v.Book != 65).ToList();
        var report = await _env.ImportAsync(withoutJudas, allowLargeDeletion: true);

        Assert.Equal(6, report.Deleted);
        Assert.Equal(1, report.BooksDeleted);
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM Books WHERE BookNumber = 65"));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM VersesFts WHERE VersesFts MATCH @m", new { m = SyntheticBible.TextOf(65, 1, 1).Split(' ')[0] }));
        Assert.True(report.FtsIntegrityOk);
    }

    [Fact]
    public async Task A_reimport_that_would_delete_a_lot_is_refused_and_changes_nothing()
    {
        var big = Enumerable.Range(1, 200).Select(v => SyntheticBible.Verse(1, 1, v)).ToList();
        await _env.ImportAsync(big);

        var ex = await Assert.ThrowsAsync<BibleException>(() => _env.ImportAsync(big.Take(20)));

        Assert.Equal(BibleErrorCodes.TooManyDeletions, ex.Code);
        Assert.Contains("180", ex.Message);
        Assert.Equal(200, await CountAsync("SELECT COUNT(*) FROM Verses"));        // intacto
    }

    [Fact]
    public async Task A_big_deletion_is_possible_when_explicitly_allowed()
    {
        var big = Enumerable.Range(1, 200).Select(v => SyntheticBible.Verse(1, 1, v)).ToList();
        await _env.ImportAsync(big);

        var report = await _env.ImportAsync(big.Take(20), allowLargeDeletion: true);

        Assert.Equal(180, report.Deleted);
        Assert.Equal(20, await CountAsync("SELECT COUNT(*) FROM Verses"));
    }

    // ───────────────────────────── atomicidad ─────────────────────────────

    [Fact]
    public async Task If_the_write_fails_halfway_nothing_changes_not_even_the_bible_row()
    {
        await _env.ImportAsync(SyntheticBible.Chapter(1, 1, 3), "AAA");
        await using (var c = await _env.OpenAsync())
            await c.ExecuteAsync("CREATE TRIGGER Falla BEFORE INSERT ON Verses WHEN new.Text LIKE '%detonador%' BEGIN SELECT RAISE(ABORT, 'falla de prueba'); END");

        var verses = SyntheticBible.Chapter(1, 1, 5).Select(v => v with { Text = v.Verse == 5 ? "detonador" : v.Text }).ToList();
        await Assert.ThrowsAsync<SqliteException>(() => _env.ImportAsync(verses, "BBB"));      // Biblia nueva
        await Assert.ThrowsAsync<SqliteException>(() => _env.ImportAsync(verses, "AAA"));      // Biblia existente: crece de 3 a 5, el 5 falla

        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM Bibles WHERE Abbreviation = 'BBB'"));
        Assert.Equal(3, await CountAsync("SELECT COUNT(*) FROM Verses"));                       // el 4 que sí entró se revirtió
        Assert.Equal(SyntheticBible.TextOf(1, 1, 1), await _env_Scalar<string>("SELECT Text FROM Verses WHERE VerseNumber = 1", new { }));
    }

    // ───────────────────────────── el informe y la validación ─────────────────────────────

    [Fact]
    public async Task The_report_never_contains_bible_text()
    {
        var small = SyntheticBible.Small();
        await _env.ImportAsync(small, profile: null);
        var report = await _env.ImportAsync(small, profile: "merge-ps47-3jn");

        var json = JsonSerializer.Serialize(report);
        foreach (var banned in new[] { "Sintetico", "lorem", "ipsum", "dolor", "Titulo", "HTML" })
            Assert.DoesNotContain(banned, json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Validation_counts_what_is_there_and_warns_about_what_is_missing_without_failing()
    {
        var report = await _env.ImportAsync(SyntheticBible.Small());

        var v = report.Validation;
        Assert.Equal((5, 7, SyntheticBible.Small().Count, 0), (v.Books, v.Chapters, v.Verses, v.EmptyVerses));
        Assert.Equal(61, v.MissingBooks.Count);
        Assert.Contains("PSA", v.ChapterCountMismatches.Single(m => m.StartsWith("PSA", StringComparison.Ordinal)).Split(':')[0]);
        Assert.Equal(v.Verses - 31_102, v.DifferenceFromStandardVerseTotal);
        Assert.Contains(v.Warnings, w => w.Contains("5 libros con versículos"));
        Assert.Contains(v.Warnings, w => w.Contains("31 102") || w.Contains("31102"));
    }

    [Fact]
    public async Task Validation_reports_empty_verses_and_chapter_gaps_by_reference()
    {
        var verses = SyntheticBible.Chapter(1, 1, 5).Where(v => v.Verse != 3).ToList();          // hueco en el 3
        verses.AddRange(SyntheticBible.Chapter(1, 2, 3).Select(v => v.Verse == 2 ? v with { Text = "   " } : v));
        verses.Add(SyntheticBible.Verse(1, 3, 2));                                              // el capítulo 3 empieza en el 2

        var report = await _env.ImportAsync(verses);

        Assert.Equal(1, report.Validation.EmptyVerses);
        Assert.Equal(2, report.Validation.VerseGapChapters);
        Assert.Contains(report.Validation.VerseGaps, g => g.StartsWith("GEN 1:", StringComparison.Ordinal));
        Assert.Contains(report.Validation.VerseGaps, g => g.StartsWith("GEN 3:", StringComparison.Ordinal));
        Assert.Contains(report.Validation.Warnings, w => w.Contains("1 versículos vacíos"));
    }

    [Fact]
    public async Task A_full_canon_with_the_standard_shape_validates_with_no_warnings()
    {
        var verses = new List<SyntheticBible.V>();
        foreach (var book in CanonicalBooks.All)
            for (var chapter = 1; chapter <= book.Chapters; chapter++)
                verses.AddRange(SyntheticBible.Chapter(book.Number, chapter, 3));

        var report = await _env.ImportAsync(verses);

        Assert.Equal((66, 1189, 3567, 0), (report.Validation.Books, report.Validation.Chapters, report.Validation.Verses, report.Validation.EmptyVerses));
        Assert.Empty(report.Validation.MissingBooks);
        Assert.Empty(report.Validation.ChapterCountMismatches);
        Assert.Equal(0, report.Validation.VerseGapChapters);
        Assert.Single(report.Validation.Warnings);      // solo la diferencia con el total de 31 102
    }

    [Fact]
    public async Task Validating_a_bible_with_no_verses_does_not_throw_and_reports_everything_missing()
    {
        await using var c = await _env.OpenAsync();
        var id = await c.ExecuteScalarAsync<long>("INSERT INTO Bibles (Abbreviation, Name, Language) VALUES ('VAC', 'Vacía', 'es'); SELECT last_insert_rowid();");

        var v = await BibleValidator.ValidateAsync(c, id, CancellationToken.None);

        Assert.Equal((0, 0, 0, 0, 66), (v.Books, v.Chapters, v.Verses, v.EmptyVerses, v.MissingBooks.Count));
        Assert.Equal(-31_102, v.DifferenceFromStandardVerseTotal);
        Assert.NotEmpty(v.Warnings);
    }

    [Fact]
    public async Task The_fts_check_repairs_a_drifted_index_and_says_so()
    {
        var report = await _env.ImportAsync(SyntheticBible.Small());
        await using var c = await _env.OpenAsync();
        // Se desincroniza el índice a propósito: se saca del índice un versículo que sigue en Verses.
        var (id, text) = await c.QuerySingleAsync<(long, string)>("SELECT Id, Text FROM Verses LIMIT 1");
        await c.ExecuteAsync("INSERT INTO VersesFts (VersesFts, rowid, Text) VALUES ('delete', @id, @text)", new { id, text });

        var (ok, note) = await BibleImporter.CheckFtsAsync(c, CancellationToken.None);

        Assert.True(ok);
        Assert.NotNull(note);
        Assert.Contains("reconstruyó", note);
        Assert.Equal(1, await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM VersesFts WHERE VersesFts MATCH @m", new { m = text.Split(' ')[0] }));
        Assert.True(report.FtsIntegrityOk);
    }

    // ───────────────────────────── peticiones inválidas ─────────────────────────────

    [Theory]
    [InlineData(" RVR ", "abreviatura")]
    [InlineData("", "abreviatura")]
    [InlineData("ABREVIATURA-DEMASIADO-LARGA-X", "abreviatura")]
    public async Task A_bad_abbreviation_is_refused_before_touching_anything(string abbreviation, string expected)
    {
        var path = SyntheticBible.WriteYouVersion(_env.FilesDirectory, SyntheticBible.Small());
        var ex = await Assert.ThrowsAsync<BibleException>(() => _env.Importer.ImportAsync(new BibleImportRequest(path, abbreviation, "n", "es"), null, CancellationToken.None));
        Assert.Equal(BibleErrorCodes.InvalidRequest, ex.Code);
        Assert.Contains(expected, ex.Message);
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM Bibles"));
    }

    [Fact]
    public async Task Missing_file_unknown_profile_unknown_or_verification_only_format_are_refused()
    {
        var path = SyntheticBible.WriteYouVersion(_env.FilesDirectory, SyntheticBible.Small());
        async Task<BibleException> Fail(BibleImportRequest r) => await Assert.ThrowsAsync<BibleException>(() => _env.Importer.ImportAsync(r, null, CancellationToken.None));

        Assert.Equal(BibleErrorCodes.FileMissing, (await Fail(new BibleImportRequest(Path.Combine(_env.FilesDirectory, "no-existe.json"), "SIN", "n", "es"))).Code);
        Assert.Equal(BibleErrorCodes.InvalidRequest, (await Fail(new BibleImportRequest(path, "SIN", "n", "es", NumberingProfileId: "inventado"))).Code);
        Assert.Equal(BibleErrorCodes.InvalidRequest, (await Fail(new BibleImportRequest(path, "SIN", "n", "es", FormatId: "zefania"))).Code);
        var verifyOnly = await Fail(new BibleImportRequest(path, "SIN", "n", "es", FormatId: "named-books-json"));
        Assert.Contains("solo sirve para verificar", verifyOnly.Message);
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM Bibles"));
    }

    [Fact]
    public async Task A_file_that_is_not_the_format_fails_with_a_clear_error_and_changes_nothing()
    {
        Directory.CreateDirectory(_env.FilesDirectory);
        var broken = Path.Combine(_env.FilesDirectory, "roto.json");
        File.WriteAllText(broken, "{ no es json");

        var ex = await Assert.ThrowsAsync<BibleException>(() => _env.Importer.ImportAsync(new BibleImportRequest(broken, "SIN", "n", "es"), null, CancellationToken.None));

        Assert.Equal(BibleErrorCodes.SourceInvalid, ex.Code);
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM Bibles"));
    }

    [Fact]
    public async Task A_source_with_no_verses_is_refused()
    {
        Directory.CreateDirectory(_env.FilesDirectory);
        var empty = Path.Combine(_env.FilesDirectory, "vacio.json");
        File.WriteAllText(empty, """{"books":[]}""");

        var ex = await Assert.ThrowsAsync<BibleException>(() => _env.Importer.ImportAsync(new BibleImportRequest(empty, "SIN", "n", "es"), null, CancellationToken.None));
        Assert.Equal(BibleErrorCodes.SourceInvalid, ex.Code);
    }

    [Fact]
    public async Task Cancelling_before_the_write_changes_nothing()
    {
        var path = SyntheticBible.WriteYouVersion(_env.FilesDirectory, SyntheticBible.Small());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _env.Importer.ImportAsync(new BibleImportRequest(path, "SIN", "n", "es"), null, cts.Token));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM Bibles"));
    }

    [Fact]
    public async Task Existing_data_other_than_verses_is_not_touched_by_a_reimport()
    {
        var small = SyntheticBible.Small();
        await _env.ImportAsync(small);
        await using (var c = await _env.OpenAsync())
            await c.ExecuteAsync("INSERT INTO Songs (Title) VALUES ('Canción de prueba'); INSERT INTO Playlists (Name) VALUES ('Servicio')");

        await _env.ImportAsync(small, profile: "merge-ps47-3jn");

        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM Songs"));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM Playlists"));
    }
}
