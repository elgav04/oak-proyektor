using System.Text.Json;
using OakProyektor.Data.Scripture;

namespace OakProyektor.Tests.Scripture;

public sealed class BibleCrossCheckerTests : IAsyncLifetime
{
    private BibleEnv _env = null!;
    public async Task InitializeAsync() => _env = await BibleEnv.CreateAsync();
    public Task DisposeAsync() { _env.Dispose(); return Task.CompletedTask; }

    private Task<CrossCheckReport> CompareAsync(IEnumerable<SyntheticBible.V> fileVerses, string abbreviation = "SIN", int max = 50)
    {
        var path = SyntheticBible.WriteNamedBooks(_env.FilesDirectory, fileVerses, $"otra-{Guid.NewGuid():N}.json");
        return _env.CrossChecker.CompareAsync(abbreviation, path, null, max, CancellationToken.None);
    }

    private static List<SyntheticBible.V> Edit(IEnumerable<SyntheticBible.V> verses, Func<SyntheticBible.V, string?> change) =>
        [.. verses.Select(v => change(v) is { } text ? v with { Text = text } : v)];

    [Fact]
    public async Task The_same_content_is_identical_everywhere_and_the_result_is_the_expected_one()
    {
        var small = SyntheticBible.Small();
        await _env.ImportAsync(small);

        var report = await CompareAsync(small);

        Assert.Equal((small.Count, small.Count, small.Count), (report.InDatabase, report.InFile, report.Identical));
        Assert.Equal((0, 0, 0, 0, 0, 0), (report.PunctuationOrSpacingOnly, report.AccentOrCaseOnly, report.WordsDiffer, report.KnownLabelVerses, report.OnlyInDatabase, report.OnlyInFile));
        Assert.Empty(report.Warnings);
    }

    [Fact]
    public async Task Differences_are_classified_as_spacing_accent_or_words()
    {
        var small = SyntheticBible.Small();
        await _env.ImportAsync(small);

        var file = Edit(small, v => (v.Book, v.Chapter, v.Verse) switch
        {
            (1, 1, 1) => "Sintetico1x1x1,lorem ipsum dolor",          // espacio de menos + coma (a)
            (1, 1, 2) => v.Text.ToUpperInvariant(),                    // mayúsculas (b)
            (1, 1, 3) => v.Text.Replace("lorem", "lórem"),             // tilde (b)
            (1, 1, 4) => v.Text + " palabranueva",                     // palabras distintas (c)
            (40, 5, 1) => "texto completamente distinto",              // (c)
            _ => null,
        });

        var report = await CompareAsync(file);

        Assert.Equal(1, report.PunctuationOrSpacingOnly);
        Assert.Equal(2, report.AccentOrCaseOnly);
        Assert.Equal(["GEN 1:2", "GEN 1:3"], report.AccentOrCaseRefs);
        Assert.Equal(2, report.WordsDiffer);
        Assert.Equal(["GEN 1:4", "MAT 5:1"], report.WordsDifferRefs);
        Assert.Equal(small.Count - 5, report.Identical);
    }

    [Fact]
    public async Task Verses_only_on_one_side_are_listed_by_reference()
    {
        var small = SyntheticBible.Small();
        await _env.ImportAsync(small);

        var file = small.Where(v => v is not { Book: 40, Chapter: 5, Verse: 2 }).Append(SyntheticBible.Verse(1, 3, 1)).ToList();
        var report = await CompareAsync(file);

        Assert.Equal((1, 1), (report.OnlyInDatabase, report.OnlyInFile));
        Assert.Equal(["MAT 5:2"], report.OnlyInDatabaseRefs);
        Assert.Equal(["GEN 3:1"], report.OnlyInFileRefs);
        Assert.Contains(report.Warnings, w => w.Contains("solo en la BD") && w.Contains("solo en el archivo"));
    }

    [Fact]
    public async Task The_numbering_profile_of_the_bible_is_applied_to_the_file_before_comparing()
    {
        var small = SyntheticBible.Small();
        await _env.ImportAsync(small, profile: "merge-ps47-3jn");          // la BD tiene el Salmo 47:9-10 unido

        var report = await CompareAsync(small);                              // el archivo los trae partidos

        Assert.Equal("merge-ps47-3jn", report.NumberingProfile);
        Assert.Equal((0, 0, 0, 0), (report.OnlyInDatabase, report.OnlyInFile, report.WordsDiffer, report.AccentOrCaseOnly));
        Assert.Equal(report.InDatabase, report.Identical);
    }

    [Fact]
    public async Task The_psalm_119_stanza_label_is_recognized_as_a_known_defect_and_not_as_a_text_difference()
    {
        var small = SyntheticBible.Small();
        await _env.ImportAsync(small);

        var labeled = Edit(small, v => v is { Book: 19, Chapter: 119 } && BibleCrossChecker.StanzaLabelFor(v.Verse) is { } label ? v.Text + " " + label : null);
        var report = await CompareAsync(labeled);

        Assert.Equal(3, report.KnownLabelVerses);                                    // los versículos 8, 16 y 24 de la Biblia sintética
        Assert.Equal(["PSA 119:8", "PSA 119:16", "PSA 119:24"], report.KnownLabelRefs);
        Assert.Equal(0, report.WordsDiffer);
        Assert.Contains(report.Warnings, w => w.Contains("se esperaban 21"));          // 3 ≠ 21: se avisa
    }

    [Fact]
    public async Task The_label_is_recognized_even_when_the_source_also_lost_the_spaces_after_punctuation_and_changed_the_final_mark()
    {
        // El patrón real: la fuente pierde el espacio tras la coma, cambia el signo final y arrastra el rótulo.
        var db = new[] { SyntheticBible.Verse(19, 119, 8, "uno, dos tres cuatro."), SyntheticBible.Verse(19, 119, 16, "cinco; seis siete") };
        await _env.ImportAsync(db);

        var report = await CompareAsync([SyntheticBible.Verse(19, 119, 8, "uno,dos tres cuatro.Bet"), SyntheticBible.Verse(19, 119, 16, "cinco;seis siete,Guimel")]);

        Assert.Equal(2, report.KnownLabelVerses);
        Assert.Equal(0, report.WordsDiffer);
        Assert.Equal(["PSA 119:8", "PSA 119:16"], report.KnownLabelRefs);
    }

    [Fact]
    public void The_stanza_labels_follow_the_hebrew_alphabet_one_per_eight_verses()
    {
        Assert.Equal("bet", BibleCrossChecker.StanzaLabelFor(8));
        Assert.Equal("guimel", BibleCrossChecker.StanzaLabelFor(16));
        Assert.Equal("tau", BibleCrossChecker.StanzaLabelFor(168));
        Assert.Null(BibleCrossChecker.StanzaLabelFor(176));     // el último versículo no tiene rótulo siguiente
        Assert.Null(BibleCrossChecker.StanzaLabelFor(9));
        Assert.Null(BibleCrossChecker.StanzaLabelFor(0));
        Assert.Equal(21, Enumerable.Range(1, 176).Count(v => BibleCrossChecker.StanzaLabelFor(v) is not null));
    }

    [Fact]
    public async Task A_real_text_difference_in_psalm_119_is_not_mistaken_for_a_label()
    {
        await _env.ImportAsync([SyntheticBible.Verse(19, 119, 8, "uno dos tres cuatro")]);

        var twoExtraWords = await CompareAsync([SyntheticBible.Verse(19, 119, 8, "uno dos tres cuatro cinco seis")]);
        var replaced = await CompareAsync([SyntheticBible.Verse(19, 119, 8, "uno dos tres cambiado")]);
        var notEighth = await CompareAsync([SyntheticBible.Verse(19, 119, 9, "uno dos tres cuatro Bet")]);

        Assert.Equal((0, 1), (twoExtraWords.KnownLabelVerses, twoExtraWords.WordsDiffer));
        var wrongLetter = await CompareAsync([SyntheticBible.Verse(19, 119, 8, "uno dos tres cuatro Dalet")]);          // la letra que toca tras el 8 es Bet
        Assert.Equal((0, 1), (wrongLetter.KnownLabelVerses, wrongLetter.WordsDiffer));
        Assert.Equal((0, 1), (replaced.KnownLabelVerses, replaced.WordsDiffer));
        Assert.Equal(0, notEighth.KnownLabelVerses);                                  // el 9 no es múltiplo de 8 (y además no existe en la BD)
    }

    [Fact]
    public async Task Exactly_21_labels_in_a_full_psalm_119_is_the_expected_result_with_no_warning()
    {
        var psalm = SyntheticBible.Chapter(19, 119, 176).ToList();
        await _env.ImportAsync(psalm);

        var labeled = Edit(psalm, v => BibleCrossChecker.StanzaLabelFor(v.Verse) is { } label ? v.Text + " " + label : null);
        var report = await CompareAsync(labeled);

        Assert.Equal(21, report.KnownLabelVerses);
        Assert.Empty(report.Warnings);
    }

    [Fact]
    public async Task Forty_or_more_word_differences_raise_a_warning_but_never_fail()
    {
        var verses = Enumerable.Range(1, 60).Select(v => SyntheticBible.Verse(1, 1, v)).ToList();
        await _env.ImportAsync(verses);

        var report = await CompareAsync(Edit(verses, v => v.Verse <= 45 ? "cambio total " + v.Verse : null));

        Assert.Equal(45, report.WordsDiffer);
        Assert.Equal(45, report.WordsDifferRefs.Count);
        Assert.Contains(report.Warnings, w => w.Contains("45 versículos con palabras distintas"));
    }

    [Fact]
    public async Task The_reference_lists_are_capped_but_the_counts_are_not()
    {
        var verses = Enumerable.Range(1, 120).Select(v => SyntheticBible.Verse(1, 1, v)).ToList();
        await _env.ImportAsync(verses);

        var report = await CompareAsync(Edit(verses, v => v.Text.ToUpperInvariant()), max: 10);

        Assert.Equal(120, report.AccentOrCaseOnly);
        Assert.Equal(10, report.AccentOrCaseRefs.Count);
    }

    [Fact]
    public async Task The_report_never_contains_bible_text_and_the_second_file_is_not_imported()
    {
        var small = SyntheticBible.Small();
        await _env.ImportAsync(small);

        var report = await CompareAsync(Edit(small, v => v.Verse == 1 ? v.Text + " diferencia" : null));

        var json = JsonSerializer.Serialize(report);
        foreach (var banned in new[] { "Sintetico", "lorem", "ipsum", "dolor", "diferencia" })
            Assert.DoesNotContain(banned, json, StringComparison.OrdinalIgnoreCase);
        await using var c = await _env.OpenAsync();
        Assert.Equal(1L, await Dapper.SqlMapper.ExecuteScalarAsync<long>(c, "SELECT COUNT(*) FROM Bibles"));     // no se importó nada
    }

    [Fact]
    public async Task Bad_requests_fail_clearly()
    {
        await _env.ImportAsync(SyntheticBible.Small());
        var path = SyntheticBible.WriteNamedBooks(_env.FilesDirectory, SyntheticBible.Small());

        Assert.Equal(BibleErrorCodes.NotFound, (await Assert.ThrowsAsync<BibleException>(() => _env.CrossChecker.CompareAsync("NOEXISTE", path, null, 50, CancellationToken.None))).Code);
        Assert.Equal(BibleErrorCodes.FileMissing, (await Assert.ThrowsAsync<BibleException>(() => _env.CrossChecker.CompareAsync("SIN", Path.Combine(_env.FilesDirectory, "no.json"), null, 50, CancellationToken.None))).Code);
        Assert.Equal(BibleErrorCodes.InvalidRequest, (await Assert.ThrowsAsync<BibleException>(() => _env.CrossChecker.CompareAsync("SIN", path, "zefania", 50, CancellationToken.None))).Code);

        var broken = Path.Combine(_env.FilesDirectory, "roto.json"); File.WriteAllText(broken, "{ no es json");
        Assert.Equal(BibleErrorCodes.SourceInvalid, (await Assert.ThrowsAsync<BibleException>(() => _env.CrossChecker.CompareAsync("SIN", broken, null, 50, CancellationToken.None))).Code);
    }

    [Fact]
    public async Task The_other_file_can_be_in_the_import_format_too()
    {
        var small = SyntheticBible.Small();
        await _env.ImportAsync(small);
        var path = SyntheticBible.WriteYouVersion(_env.FilesDirectory, small, fileName: "mismo-formato.json");

        var report = await _env.CrossChecker.CompareAsync("SIN", path, "youversion-json", 50, CancellationToken.None);

        Assert.Equal(report.InDatabase, report.Identical);
        Assert.Equal("youversion-json", report.FormatId);
    }
}
