using OakProyektor.Core.Scripture;
using OakProyektor.Data.Scripture;

namespace OakProyektor.Tests.Scripture;

public sealed class BibleQueryServiceTests : IAsyncLifetime
{
    private BibleEnv _env = null!;
    private string? _default;
    private int _historyLimit = 100;

    public async Task InitializeAsync()
    {
        _env = await BibleEnv.CreateAsync(new BibleQueryOptions(() => _default, () => _historyLimit));
        await _env.ImportAsync(SyntheticBible.Small(), "SIN");
    }

    public Task DisposeAsync() { _env.Dispose(); return Task.CompletedTask; }

    private BibleQueryService Q => _env.Query;

    private static async Task<BibleException> FailsAsync(Func<Task> action) => await Assert.ThrowsAsync<BibleException>(action);

    // ───────────────────────────── pasajes ─────────────────────────────

    [Fact]
    public async Task A_single_verse_is_found_by_its_written_reference()
    {
        var passage = await Q.LookupAsync("Mt 5:3", "SIN", false, CancellationToken.None);

        Assert.Equal("Mt 5:3", passage.Reference);
        Assert.Equal("SIN", passage.Bible);
        var verse = Assert.Single(passage.Verses);
        Assert.Equal((40, "Mateo", 5, 3, SyntheticBible.TextOf(40, 5, 3)), (verse.BookNumber, verse.BookName, verse.Chapter, verse.VerseNumber, verse.Text));
    }

    [Fact]
    public async Task Ranges_lists_chapters_and_cross_chapter_ranges_come_back_in_order()
    {
        var range = await Q.LookupAsync("Mateo 5:3-5", null, false, CancellationToken.None);
        Assert.Equal([3, 4, 5], range.Verses.Select(v => v.VerseNumber));

        var list = await Q.LookupAsync("Mt 5:3,5,7", null, false, CancellationToken.None);
        Assert.Equal([3, 5, 7], list.Verses.Select(v => v.VerseNumber));

        var chapter = await Q.LookupAsync("Sal 47", null, false, CancellationToken.None);
        Assert.Equal(10, chapter.Verses.Count);
        Assert.Equal("Sal 47", chapter.Reference);

        var cross = await Q.LookupAsync("Gn 1:4-2:2", null, false, CancellationToken.None);
        Assert.Equal([(1, 4), (1, 5), (2, 1), (2, 2)], cross.Verses.Select(v => (v.Chapter, v.VerseNumber)));

        var chapters = await Q.LookupAsync("Gn 1-2", null, false, CancellationToken.None);
        Assert.Equal(9, chapters.Verses.Count);

        var single = await Q.LookupAsync("Jud 3-4", null, false, CancellationToken.None);       // libro de un solo capítulo
        Assert.Equal([3, 4], single.Verses.Select(v => v.VerseNumber));
    }

    [Fact]
    public async Task Spelling_variants_reach_the_same_verse()
    {
        foreach (var input in new[] { "Mt 5:3", "mateo 5:3", "MT5:3", "Mat 5.3", "S. Mateo 5:3" })
        {
            var passage = await Q.LookupAsync(input, null, false, CancellationToken.None);
            Assert.Equal(SyntheticBible.TextOf(40, 5, 3), passage.Verses.Single().Text);
        }
    }

    [Fact]
    public async Task A_chapter_by_book_number_returns_all_its_verses()
    {
        var passage = await Q.GetChapterAsync("SIN", 19, 119, CancellationToken.None);

        Assert.Equal(24, passage.Verses.Count);
        Assert.Equal("Sal 119", passage.Reference);
        Assert.Equal(Enumerable.Range(1, 24), passage.Verses.Select(v => v.VerseNumber));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(67, 1)]
    [InlineData(40, 0)]
    [InlineData(40, 29)]
    public async Task A_chapter_outside_the_canon_is_refused(int book, int chapter)
    {
        var ex = await FailsAsync(() => Q.GetChapterAsync("SIN", book, chapter, CancellationToken.None));
        Assert.True(ex.Code is BibleErrorCodes.InvalidRequest or BibleErrorCodes.InvalidReference, ex.Code);
    }

    [Fact]
    public async Task Errors_are_specific_and_carry_a_clear_message()
    {
        var bad = await FailsAsync(() => Q.LookupAsync("Xyz 1:1", "SIN", false, CancellationToken.None));
        Assert.Equal(BibleErrorCodes.InvalidReference, bad.Code);
        Assert.Contains("No existe el libro", bad.Message);

        var ambiguous = await FailsAsync(() => Q.LookupAsync("Jo 1:1", "SIN", false, CancellationToken.None));
        Assert.Contains("ambiguo", ambiguous.Message);

        var noBible = await FailsAsync(() => Q.LookupAsync("Mt 5:3", "NOEXISTE", false, CancellationToken.None));
        Assert.Equal(BibleErrorCodes.NotFound, noBible.Code);

        // Referencia válida pero que esta Biblia no tiene (Juan no está en la Biblia sintética; Mateo 5:99 tampoco).
        var missingBook = await FailsAsync(() => Q.LookupAsync("Jn 3:16", "SIN", false, CancellationToken.None));
        Assert.Equal(BibleErrorCodes.PassageNotFound, missingBook.Code);
        var missingVerse = await FailsAsync(() => Q.LookupAsync("Mt 5:99", "SIN", false, CancellationToken.None));
        Assert.Equal(BibleErrorCodes.PassageNotFound, missingVerse.Code);
        Assert.Contains("Mt 5:99", missingVerse.Message);
    }

    [Fact]
    public async Task The_default_bible_is_used_when_none_is_given_and_it_falls_back_to_the_first_one()
    {
        await _env.ImportAsync(SyntheticBible.Chapter(40, 5, 3).Select(v => v with { Text = "Otra Biblia " + v.Verse }), "OTR");

        _default = "OTR";
        Assert.Equal("OTR", (await Q.LookupAsync("Mt 5:1", null, false, CancellationToken.None)).Bible);
        Assert.Equal("SIN", (await Q.LookupAsync("Mt 5:1", "SIN", false, CancellationToken.None)).Bible);     // lo explícito manda

        _default = "BORRADA";                                                                                   // ya no está instalada
        Assert.Equal("SIN", (await Q.LookupAsync("Mt 5:1", null, false, CancellationToken.None)).Bible);

        _default = null;
        Assert.Equal("SIN", (await Q.LookupAsync("Mt 5:1", null, false, CancellationToken.None)).Bible);
    }

    [Fact]
    public async Task With_no_bible_installed_the_error_says_so()
    {
        using var empty = await BibleEnv.CreateAsync();
        var ex = await FailsAsync(() => empty.Query.LookupAsync("Mt 5:1", null, false, CancellationToken.None));
        Assert.Equal(BibleErrorCodes.NotFound, ex.Code);
        Assert.Contains("ninguna Biblia", ex.Message);
        Assert.Empty(await empty.Query.ListBiblesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task The_list_has_counts_profile_and_attribution()
    {
        await _env.Query.SetAttributionAsync("SIN", "Aviso editable", CancellationToken.None);

        var bible = Assert.Single(await Q.ListBiblesAsync(CancellationToken.None));

        Assert.Equal(("SIN", "Biblia sintética", "es", "Aviso editable", null, 5, SyntheticBible.Small().Count),
            (bible.Abbreviation, bible.Name, bible.Language, bible.Attribution, bible.NumberingProfile, bible.Books, bible.Verses));
    }

    [Fact]
    public async Task The_attribution_travels_with_every_passage_and_can_be_edited_and_removed()
    {
        Assert.Equal("", (await Q.LookupAsync("Mt 5:3", null, false, CancellationToken.None)).Attribution);   // vacío por defecto

        await Q.SetAttributionAsync("SIN", "Texto de aviso inventado", CancellationToken.None);
        Assert.Equal("Texto de aviso inventado", (await Q.LookupAsync("Mt 5:3", null, false, CancellationToken.None)).Attribution);
        Assert.Equal("Texto de aviso inventado", (await Q.GetChapterAsync(null, 40, 5, CancellationToken.None)).Attribution);

        await Q.SetAttributionAsync("SIN", "", CancellationToken.None);
        Assert.Equal("", (await Q.LookupAsync("Mt 5:3", null, false, CancellationToken.None)).Attribution);

        Assert.Equal(BibleErrorCodes.NotFound, (await FailsAsync(() => Q.SetAttributionAsync("NOEXISTE", "x", CancellationToken.None))).Code);
        Assert.Equal(BibleErrorCodes.InvalidRequest, (await FailsAsync(() => Q.SetAttributionAsync("SIN", new string('x', BibleQueryService.MaxAttributionLength + 1), CancellationToken.None))).Code);
    }

    // ───────────────────────────── búsqueda ─────────────────────────────

    [Fact]
    public async Task Free_text_search_finds_a_word_without_accents_or_capitals_and_returns_a_fragment_with_the_match_marked()
    {
        await _env.ImportAsync([SyntheticBible.Verse(1, 1, 1, "Jesús caminó junto al lago de Galilea")], "ACC");

        var result = await Q.SearchAsync("JESUS", "ACC", 10, CancellationToken.None);

        var hit = Assert.Single(result.Hits);
        Assert.Equal(("ACC", 1, "Génesis", 1, 1), (hit.Bible, hit.BookNumber, hit.BookName, hit.Chapter, hit.VerseNumber));
        Assert.Contains("«Jesús»", hit.Fragment);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task Several_words_all_have_to_appear_and_a_quoted_phrase_must_appear_in_order()
    {
        await _env.ImportAsync(
            [SyntheticBible.Verse(1, 1, 1, "el pastor cuidaba las ovejas del monte"), SyntheticBible.Verse(1, 1, 2, "las ovejas del pastor bebían"), SyntheticBible.Verse(1, 1, 3, "el monte era alto")], "FRA");

        Assert.Equal([1, 2], (await Q.SearchAsync("pastor ovejas", "FRA", 10, CancellationToken.None)).Hits.Select(h => h.VerseNumber).Order());
        Assert.Equal([1], (await Q.SearchAsync("\"pastor cuidaba\"", "FRA", 10, CancellationToken.None)).Hits.Select(h => h.VerseNumber));
        Assert.Empty((await Q.SearchAsync("\"ovejas pastor\"", "FRA", 10, CancellationToken.None)).Hits);        // el orden importa en una frase
        Assert.Empty((await Q.SearchAsync("pastor montaña", "FRA", 10, CancellationToken.None)).Hits);          // una palabra que no está
        Assert.Equal([1, 3], (await Q.SearchAsync("monte", "FRA", 10, CancellationToken.None)).Hits.Select(h => h.VerseNumber).Order());
    }

    [Fact]
    public async Task Search_results_are_limited_and_say_when_there_were_more()
    {
        await _env.ImportAsync(Enumerable.Range(1, 30).Select(v => SyntheticBible.Verse(1, 1, v, "palabracomun numero" + v)), "MUC");

        var limited = await Q.SearchAsync("palabracomun", "MUC", 10, CancellationToken.None);
        Assert.Equal(10, limited.Hits.Count);
        Assert.True(limited.Truncated);

        var all = await Q.SearchAsync("palabracomun", "MUC", 0, CancellationToken.None);       // 0 = el límite por defecto (50)
        Assert.Equal(30, all.Hits.Count);
        Assert.False(all.Truncated);

        var capped = await Q.SearchAsync("palabracomun", "MUC", 100_000, CancellationToken.None);
        Assert.True(capped.Hits.Count <= BibleQueryService.MaxSearchLimit);
    }

    [Fact]
    public async Task Search_can_be_restricted_to_one_bible_or_cover_all_of_them()
    {
        await _env.ImportAsync([SyntheticBible.Verse(1, 1, 1, "compartida solo en otra")], "OTR");
        await _env.ImportAsync([SyntheticBible.Verse(1, 1, 1, "compartida solo en tercera")], "TER");

        Assert.Equal(2, (await Q.SearchAsync("compartida", null, 10, CancellationToken.None)).Hits.Count);
        Assert.Equal(["TER"], (await Q.SearchAsync("compartida", "TER", 10, CancellationToken.None)).Hits.Select(h => h.Bible));
        Assert.Equal(BibleErrorCodes.NotFound, (await FailsAsync(() => Q.SearchAsync("compartida", "NOEXISTE", 10, CancellationToken.None))).Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"\"")]
    [InlineData("*** ... ---")]
    public async Task A_query_with_no_words_is_refused(string query)
    {
        Assert.Equal(BibleErrorCodes.EmptyQuery, (await FailsAsync(() => Q.SearchAsync(query, null, 10, CancellationToken.None))).Code);
    }

    [Theory]
    [InlineData("OR")]
    [InlineData("NOT lorem")]
    [InlineData("lorem AND")]
    [InlineData("\"sin cerrar")]
    [InlineData("lorem*")]
    [InlineData("col:lorem")]
    [InlineData("(lorem")]
    [InlineData("lorem NEAR/2 ipsum")]
    [InlineData("^lorem")]
    [InlineData("'lorem'")]
    public async Task FTS_operators_in_what_the_user_types_are_neutralized_and_never_cause_a_syntax_error(string query)
    {
        var result = await Q.SearchAsync(query, null, 10, CancellationToken.None);    // no lanza
        Assert.NotNull(result);
    }

    [Fact]
    public void The_match_expression_keeps_only_words_and_phrases()
    {
        Assert.Equal("\"hola\" \"mundo\"", BibleQueryService.BuildMatch("hola mundo"));
        Assert.Equal("\"hola mundo\"", BibleQueryService.BuildMatch("\"hola mundo\""));
        Assert.Equal("\"buen dia\" \"hola\"", BibleQueryService.BuildMatch("hola \"buen dia\""));
        Assert.Equal("\"OR\"", BibleQueryService.BuildMatch("OR*"));
        Assert.Equal("\"Jesús\"", BibleQueryService.BuildMatch("(Jesús)"));
        Assert.Null(BibleQueryService.BuildMatch("***"));
        Assert.Null(BibleQueryService.BuildMatch(null));
    }

    [Fact]
    public void The_match_expression_normalizes_to_NFC_so_decomposed_accents_do_not_split_words()
    {
        var nfc = "Jesús caminó";                       // «Jesús caminó» con las vocales ya compuestas
        var nfd = "Jesús caminó";                    // la misma frase con acentos combinantes (u + ´, o + ´)
        Assert.NotEqual(nfc, nfd);
        Assert.Equal("\"Jesús\" \"caminó\"", BibleQueryService.BuildMatch(nfc));
        Assert.Equal(BibleQueryService.BuildMatch(nfc), BibleQueryService.BuildMatch(nfd));   // antes la marca partía la palabra: «Jesu» + «s»
        Assert.Equal("\"Jesús caminó\"", BibleQueryService.BuildMatch("\"" + nfd + "\""));
        // una marca combinante suelta (sin base compuesta) se queda dentro de su palabra, no la parte
        Assert.Equal("\"x́y\"", BibleQueryService.BuildMatch("x́y"));
    }

    [Fact]
    public void The_match_expression_is_bounded_in_terms_and_in_processed_text()
    {
        var many = string.Join(' ', Enumerable.Range(1, 500).Select(i => "palabra" + i));
        var match = BibleQueryService.BuildMatch(many)!;
        var terms = match.Split(' ');
        Assert.Equal(BibleQueryService.MaxQueryTerms, terms.Length);
        Assert.Equal("\"palabra1\"", terms[0]);                      // se conservan los PRIMEROS términos
        Assert.True(match.Length < 1000);

        // un pegado de cientos de miles de caracteres no arma una consulta gigante ni tarda
        var huge = new string('a', 300_000) + " " + string.Join(' ', Enumerable.Repeat("b", 100_000));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var bounded = BibleQueryService.BuildMatch(huge)!;
        Assert.True(sw.ElapsedMilliseconds < 500, $"tardó {sw.ElapsedMilliseconds} ms");
        Assert.True(bounded.Length <= BibleQueryService.MaxQueryChars + 2 * BibleQueryService.MaxQueryTerms + 2);

        // frases y palabras sueltas cuentan como términos, y también están acotadas
        var phrases = string.Join(' ', Enumerable.Range(1, 100).Select(i => "\"dos" + i + " tres\""));
        Assert.Equal(BibleQueryService.MaxQueryTerms, BibleQueryService.BuildMatch(phrases)!.Split("\" \"").Length);
    }

    [Fact]
    public void The_match_expression_survives_invalid_unicode_and_hostile_input()
    {
        Assert.Null(BibleQueryService.BuildMatch("\ud800"));                      // sustituto suelto: no lanza
        Assert.NotNull(BibleQueryService.BuildMatch("abc\ud800def"));
        foreach (var hostile in new[] { "NEAR(a b)", "a OR b", "-x", "col:valor", "\"", "\"\"", "́́", "a\0b", "{a b}" })
            Assert.Null(Record.Exception(() => BibleQueryService.BuildMatch(hostile)));
    }

    [Fact]
    public async Task A_search_with_decomposed_accents_finds_the_stored_text()
    {
        await _env.ImportAsync([SyntheticBible.Verse(1, 1, 1, "Jesús caminaba junto al lago")], "NFC");
        var hits = await Q.SearchAsync("Jesús", "NFC", 10, CancellationToken.None);     // escrito con el acento combinante
        Assert.Single(hits.Hits);
    }

    // ───────────────────────────── rangos enormes (H4) ─────────────────────────────

    [Fact]
    public async Task A_range_over_the_per_query_limit_is_rejected_with_a_clear_error_and_never_truncated_silently()
    {
        // «Salmos» sintético: 3 capítulos de 100 versículos (300 en total).
        await _env.ImportAsync(Enumerable.Range(1, 3).SelectMany(c => SyntheticBible.Chapter(19, c, 100)), "GRA");
        Assert.Equal(200, BibleQueryService.MaxPassageVerses);

        var two = await Q.LookupAsync("Sal 1-2", "GRA", false, CancellationToken.None);            // justo en el límite
        Assert.Equal(200, two.Verses.Count);

        var ex = await FailsAsync(() => Q.LookupAsync("Sal 1-3", "GRA", false, CancellationToken.None));
        Assert.Equal(BibleErrorCodes.RangeTooLarge, ex.Code);
        Assert.Contains("demasiado grande", ex.Message);
        Assert.Contains("200", ex.Message);
        Assert.Equal(BibleErrorCodes.RangeTooLarge, (await FailsAsync(() => Q.LookupAsync("Sal 1:1-3:100", "GRA", false, CancellationToken.None))).Code);
        // varios segmentos que juntos pasan el límite también
        Assert.Equal(BibleErrorCodes.RangeTooLarge, (await FailsAsync(() => Q.LookupAsync("Sal 1:1-100; 2:1-100; 3:1-5", "GRA", false, CancellationToken.None))).Code);
        // un capítulo, un versículo o un rango corto siguen igual
        Assert.Equal(100, (await Q.LookupAsync("Sal 3", "GRA", false, CancellationToken.None)).Verses.Count);
        Assert.Equal(3, (await Q.LookupAsync("Sal 1:1-3", "GRA", false, CancellationToken.None)).Verses.Count);
        Assert.Equal(100, (await Q.GetChapterAsync("GRA", 19, 2, CancellationToken.None)).Verses.Count);
    }

    [Fact]
    public async Task A_huge_reference_like_psalms_1_to_150_answers_fast_without_reading_thousands_of_rows()
    {
        await _env.ImportAsync(Enumerable.Range(1, 150).SelectMany(c => SyntheticBible.Chapter(19, c, 16)), "PSA");   // 2 400 versículos
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = await FailsAsync(() => Q.LookupAsync("Sal 1-150", "PSA", false, CancellationToken.None));
        sw.Stop();
        Assert.Equal(BibleErrorCodes.RangeTooLarge, ex.Code);
        Assert.True(sw.ElapsedMilliseconds < 1000, $"tardó {sw.ElapsedMilliseconds} ms");
        Assert.Empty(await Q.GetHistoryAsync(0, CancellationToken.None));          // un rechazo no se anota
    }
    // ───────────────────────────── historial ─────────────────────────────

    [Fact]
    public async Task A_lookup_is_only_recorded_when_asked_and_the_newest_comes_first()
    {
        await Q.LookupAsync("Mt 5:3", null, recordHistory: false, CancellationToken.None);
        Assert.Empty(await Q.GetHistoryAsync(0, CancellationToken.None));

        await Q.LookupAsync("Mt 5:3", null, recordHistory: true, CancellationToken.None);
        await Task.Delay(5);
        await Q.LookupAsync("Sal 47:1-3", null, recordHistory: true, CancellationToken.None);

        var history = await Q.GetHistoryAsync(0, CancellationToken.None);
        Assert.Equal(["Sal 47:1-3", "Mt 5:3"], history.Select(h => h.Label));
        var first = history[0];
        Assert.Equal(("SIN", 19, "Salmos", 47, 1, 3), (first.Bible, first.BookNumber, first.BookName, first.Chapter, first.VerseStart, first.VerseEnd));
    }

    [Fact]
    public async Task A_failed_lookup_is_not_recorded()
    {
        await FailsAsync(() => Q.LookupAsync("Jn 3:16", null, recordHistory: true, CancellationToken.None));
        await FailsAsync(() => Q.LookupAsync("basura", null, recordHistory: true, CancellationToken.None));
        Assert.Empty(await Q.GetHistoryAsync(0, CancellationToken.None));
    }

    [Fact]
    public async Task The_history_keeps_only_the_configured_number_of_entries_and_repeating_a_citation_moves_it_to_the_top()
    {
        _historyLimit = 3;
        foreach (var verse in new[] { 1, 2, 3, 4, 5 })
        {
            await Q.LookupAsync($"Mt 5:{verse}", null, recordHistory: true, CancellationToken.None);
            await Task.Delay(3);
        }
        Assert.Equal(["Mt 5:5", "Mt 5:4", "Mt 5:3"], (await Q.GetHistoryAsync(0, CancellationToken.None)).Select(h => h.Label));

        await Q.LookupAsync("Mt 5:3", null, recordHistory: true, CancellationToken.None);          // se repite: sube, no se duplica
        Assert.Equal(["Mt 5:3", "Mt 5:5", "Mt 5:4"], (await Q.GetHistoryAsync(0, CancellationToken.None)).Select(h => h.Label));

        Assert.Equal(2, (await Q.GetHistoryAsync(2, CancellationToken.None)).Count);
        Assert.Equal(3, (await Q.GetHistoryAsync(50, CancellationToken.None)).Count);             // nunca más que el límite
    }

    [Fact]
    public async Task A_limit_of_zero_turns_the_history_off_and_lowering_it_hides_the_old_entries()
    {
        _historyLimit = 5;
        foreach (var verse in new[] { 1, 2, 3 }) { await Q.LookupAsync($"Mt 5:{verse}", null, true, CancellationToken.None); await Task.Delay(3); }
        _historyLimit = 2;
        Assert.Equal(2, (await Q.GetHistoryAsync(0, CancellationToken.None)).Count);

        _historyLimit = 0;
        await Q.LookupAsync("Mt 5:4", null, true, CancellationToken.None);
        Assert.Empty(await Q.GetHistoryAsync(0, CancellationToken.None));
    }

    [Fact]
    public async Task The_history_survives_reimporting_or_deleting_a_bible_because_it_stores_references_not_ids()
    {
        await Q.LookupAsync("Mt 5:3", null, recordHistory: true, CancellationToken.None);

        await _env.ImportAsync(SyntheticBible.Small(), "SIN");                                      // reimportar
        await using (var c = await _env.OpenAsync())
            await Dapper.SqlMapper.ExecuteAsync(c, "DELETE FROM Bibles");                           // y hasta borrarla del todo

        var entry = Assert.Single(await Q.GetHistoryAsync(0, CancellationToken.None));
        Assert.Equal(("Mt 5:3", "SIN"), (entry.Label, entry.Bible));
    }
}
