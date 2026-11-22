using Dapper;
using OakProyektor.Core.Scripture;
using OakProyektor.Tests.Data;

namespace OakProyektor.Tests.Scripture;

public class CanonicalBooksTests
{
    [Fact]
    public void There_are_66_books_numbered_1_to_66_in_canonical_order()
    {
        Assert.Equal(66, CanonicalBooks.All.Count);
        Assert.Equal(Enumerable.Range(1, 66), CanonicalBooks.All.Select(b => b.Number));
        Assert.Equal("GEN", CanonicalBooks.Get(1).Usfm);
        Assert.Equal("MAL", CanonicalBooks.Get(39).Usfm);
        Assert.Equal("MAT", CanonicalBooks.Get(40).Usfm);
        Assert.Equal("REV", CanonicalBooks.Get(66).Usfm);
    }

    [Fact]
    public void The_testaments_split_at_book_39_and_the_chapter_totals_are_the_canon_ones()
    {
        Assert.Equal(39, CanonicalBooks.All.Count(b => b.Testament == CanonicalBook.OldTestament));
        Assert.Equal(27, CanonicalBooks.All.Count(b => b.Testament == CanonicalBook.NewTestament));
        Assert.All(CanonicalBooks.All.Take(39), b => Assert.Equal("OT", b.Testament));
        Assert.All(CanonicalBooks.All.Skip(39), b => Assert.Equal("NT", b.Testament));
        Assert.Equal(929, CanonicalBooks.All.Where(b => b.Number <= 39).Sum(b => b.Chapters));
        Assert.Equal(260, CanonicalBooks.All.Where(b => b.Number > 39).Sum(b => b.Chapters));
        Assert.Equal(CanonicalBooks.TotalChapters, CanonicalBooks.All.Sum(b => b.Chapters));
        Assert.Equal(1189, CanonicalBooks.TotalChapters);
    }

    [Fact]
    public void Codes_names_and_short_names_are_unique_and_the_known_single_chapter_books_are_right()
    {
        Assert.Equal(66, CanonicalBooks.All.Select(b => b.Usfm).Distinct().Count());
        Assert.Equal(66, CanonicalBooks.All.Select(b => b.Name).Distinct().Count());
        Assert.Equal(66, CanonicalBooks.All.Select(b => b.ShortName).Distinct().Count());
        Assert.Equal(["OBA", "PHM", "2JN", "3JN", "JUD"], CanonicalBooks.All.Where(b => b.Chapters == 1).Select(b => b.Usfm));
        Assert.Equal(150, CanonicalBooks.Get(19).Chapters);
        Assert.Equal(22, CanonicalBooks.Get(66).Chapters);
    }

    [Fact]
    public void Lookup_helpers_are_safe_with_bad_input()
    {
        Assert.Null(CanonicalBooks.Find(0));
        Assert.Null(CanonicalBooks.Find(67));
        Assert.Throws<ArgumentOutOfRangeException>(() => CanonicalBooks.Get(0));
        Assert.Equal(43, CanonicalBooks.FindByUsfm("jhn")!.Number);
        Assert.Null(CanonicalBooks.FindByUsfm("XXX"));
        Assert.Null(CanonicalBooks.FindByUsfm(null));
    }
}

public sealed class BookNamesTests : IDisposable
{
    private readonly TestDatabase _db = new();
    public void Dispose() => _db.Dispose();

    [Theory]
    [InlineData("Gén.", "gen")]
    [InlineData("  JN ", "jn")]
    [InlineData("Éxodo", "exodo")]
    [InlineData("Núm", "num")]
    [InlineData("I Corintios", "1corintios")]
    [InlineData("Primera de Corintios", "1corintios")]
    [InlineData("Primero de Corintios", "1corintios")]
    [InlineData("1ra Corintios", "1corintios")]
    [InlineData("1 Co", "1co")]
    [InlineData("1Co", "1co")]
    [InlineData("1 Co.", "1co")]
    [InlineData("II Reyes", "2reyes")]
    [InlineData("Segundo de Reyes", "2reyes")]
    [InlineData("III Juan", "3juan")]
    [InlineData("Tercera de Juan", "3juan")]
    [InlineData("S. Juan", "sjuan")]
    [InlineData("Cantar de los Cantares", "cantardeloscantares")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Keys_ignore_case_accents_dots_spaces_and_ordinal_style(string? input, string key)
    {
        Assert.Equal(key, BookNames.NormalizeKey(input));
    }

    [Theory]
    [InlineData("Gn", 1)] [InlineData("Gén", 1)] [InlineData("Génesis", 1)] [InlineData("GENESIS", 1)] [InlineData("génesis", 1)]
    [InlineData("Jn", 43)] [InlineData("Juan", 43)]
    [InlineData("1 Co", 46)] [InlineData("1Co", 46)] [InlineData("I Corintios", 46)] [InlineData("Primera de Corintios", 46)]
    [InlineData("Sal", 19)] [InlineData("Salmo", 19)] [InlineData("Salmos", 19)]
    [InlineData("Cnt", 22)] [InlineData("Cantares", 22)] [InlineData("Cantar de los Cantares", 22)]
    [InlineData("1 Jn", 62)] [InlineData("1 Juan", 62)] [InlineData("3 Jn", 64)]
    [InlineData("Jud", 65)] [InlineData("Judas", 65)] [InlineData("Jue", 7)] [InlineData("Jueces", 7)]
    [InlineData("Fil", 50)] [InlineData("Flm", 57)] [InlineData("Filemón", 57)]
    [InlineData("Ez", 26)] [InlineData("Esd", 15)]
    public void The_requested_names_resolve_to_the_right_book(string text, int book)
    {
        var match = BookNames.Resolve(text);
        Assert.Equal(BookMatchKind.Exact, match.Kind);
        Assert.Equal(book, match.BookNumber);
        Assert.Equal(book, BookNames.FindExact(text));
    }

    [Fact]
    public void Aliases_never_collide_and_every_book_has_several()
    {
        Assert.Equal(BookNames.All.Count, BookNames.All.Select(a => a.Key).Distinct().Count());
        foreach (var group in BookNames.All.GroupBy(a => a.BookNumber)) Assert.True(group.Count() >= 2, $"El libro {group.Key} tiene muy pocos alias");
        Assert.Equal(66, BookNames.All.Select(a => a.BookNumber).Distinct().Count());
        foreach (var book in CanonicalBooks.All)
        {
            Assert.Equal(book.Number, BookNames.FindExact(book.Name));
            Assert.Equal(book.Number, BookNames.FindExact(book.ShortName));
        }
    }

    [Fact]
    public void A_unique_prefix_is_accepted_an_ambiguous_one_is_not_and_an_unknown_one_does_not_exist()
    {
        Assert.Equal(BookMatchKind.UniquePrefix, BookNames.Resolve("Genes").Kind);
        Assert.Equal(1, BookNames.Resolve("Genes").BookNumber);
        Assert.Equal(BookMatchKind.Ambiguous, BookNames.Resolve("Jo").Kind);
        Assert.Equal(BookMatchKind.Ambiguous, BookNames.Resolve("g").Kind);                 // un solo carácter nunca se adivina
        Assert.Equal(BookMatchKind.NotFound, BookNames.Resolve("zzz").Kind);
        Assert.Equal(BookMatchKind.NotFound, BookNames.Resolve("").Kind);
        Assert.Equal(BookMatchKind.NotFound, BookNames.Resolve(null).Kind);
    }

    [Fact]
    public void Suggestions_complete_a_prefix_without_repeating_a_book()
    {
        var jo = BookNames.Suggest("Jo");
        Assert.Equal([6, 18, 29, 32], jo.Select(s => s.BookNumber).Order());
        Assert.Equal(jo.Count, jo.Select(s => s.BookNumber).Distinct().Count());

        var firstCorinthians = BookNames.Suggest("1 c");
        Assert.Contains(46, firstCorinthians.Select(s => s.BookNumber));     // 1 Corintios
        Assert.Contains(13, firstCorinthians.Select(s => s.BookNumber));     // 1 Crónicas
        Assert.DoesNotContain(47, firstCorinthians.Select(s => s.BookNumber));

        Assert.Equal(43, BookNames.Suggest("Jn")[0].BookNumber);              // la coincidencia exacta va primero
        Assert.Equal("Juan", BookNames.Suggest("Jn")[0].Name);
        Assert.Equal(46, BookNames.Suggest("I Cor")[0].BookNumber);           // las formas con número romano también completan
    }

    [Fact]
    public void Suggestions_respect_the_limit_and_stay_quiet_once_a_chapter_is_typed_or_there_is_nothing_typed()
    {
        Assert.Equal(3, BookNames.Suggest("1", 3).Count);
        Assert.Empty(BookNames.Suggest("Sal 23"));
        Assert.Empty(BookNames.Suggest("Jn 3:"));
        Assert.Empty(BookNames.Suggest(""));
        Assert.Empty(BookNames.Suggest(null));
        Assert.Empty(BookNames.Suggest("Jo", 0));
        Assert.Empty(BookNames.Suggest("zzzz"));
        Assert.All(BookNames.Suggest("1"), s => Assert.StartsWith("1", s.Name));     // «1» a secas es el principio de «1 Corintios», no un capítulo
    }

    [Fact]
    public async Task The_BookAliases_table_holds_exactly_the_code_list()
    {
        await _db.InitializeAsync();
        await using var c = await _db.OpenAsync();
        var rows = (await c.QueryAsync<(string Key, long Book, string Alias)>("SELECT AliasKey, BookNumber, Alias FROM BookAliases")).ToList();

        Assert.Equal(BookNames.All.Count, rows.Count);
        Assert.Equal(
            BookNames.All.Select(a => (a.Key, (long)a.BookNumber, a.Alias)).OrderBy(r => r.Key, StringComparer.Ordinal),
            rows.OrderBy(r => r.Key, StringComparer.Ordinal));
    }

    [Fact]
    public async Task The_BookAliases_table_refuses_a_key_for_two_books_and_a_book_outside_the_canon()
    {
        await _db.InitializeAsync();
        await using var c = await _db.OpenAsync();
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => c.ExecuteAsync("INSERT INTO BookAliases (AliasKey, BookNumber, Alias) VALUES ('jn', 1, 'Jn')"));
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => c.ExecuteAsync("INSERT INTO BookAliases (AliasKey, BookNumber, Alias) VALUES ('nuevo', 67, 'Nuevo')"));
    }
}

public class VerseCleanerTests
{
    [Theory]
    [InlineData("  a  b   c  ", "a b c")]
    [InlineData("a\tb\nc\r\nd", "a b c d")]
    [InlineData("a\u00A0b", "a b")]                       // espacio no separable
    [InlineData("a\u2003b\u2028c", "a b c")]              // espacios Unicode
    [InlineData("a\u0000b\u0007c", "abc")]                // controles
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void Whitespace_is_collapsed_and_control_characters_removed(string raw, string expected)
    {
        Assert.Equal(expected, VerseCleaner.Clean(raw));
    }

    [Fact]
    public void Text_is_normalized_to_NFC()
    {
        var decomposed = "Jesús";                 // u + tilde combinante
        var composed = "Jesús";
        Assert.NotEqual(composed, decomposed);
        Assert.Equal(composed, VerseCleaner.Clean(decomposed));
    }

    [Fact]
    public void Punctuation_accents_and_capitals_are_never_touched()
    {
        const string text = "¿Quién, dijo: «Él»; sí! ÉL Éste (sólo) - ñandú... ¡Ay!";
        Assert.Equal(text, VerseCleaner.Clean(text));
    }
}

public class NumberingProfileTests
{
    private static List<SourceVerse> Verses(params (int B, int C, int V, string T)[] v) => [.. v.Select(x => new SourceVerse(x.B, x.C, x.V, x.T))];

    [Fact]
    public void Profiles_are_defined_by_references_only_and_none_is_the_default()
    {
        Assert.True(NumberingProfiles.TryGet(null, out var none));
        Assert.Same(NumberingProfiles.None, none);
        Assert.True(NumberingProfiles.TryGet("", out _));
        Assert.True(NumberingProfiles.TryGet("none", out _));
        Assert.True(NumberingProfiles.TryGet("MERGE-PS47-3JN", out var merge));       // sin distinguir mayúsculas
        Assert.Equal([new VerseMerge(19, 47, 9, 10), new VerseMerge(64, 1, 14, 15)], merge.Merges);
        Assert.False(NumberingProfiles.TryGet("inventado", out _));
        Assert.Empty(NumberingProfiles.None.Merges);
        Assert.Equal("PSA 47:9-10", merge.Merges[0].Label);
        Assert.Equal("3JN 1:14-15", merge.Merges[1].Label);
    }

    [Fact]
    public void A_merge_joins_the_verses_with_a_space_into_the_first_and_removes_the_others()
    {
        var verses = Verses((19, 47, 8, "ocho"), (19, 47, 9, "nueve"), (19, 47, 10, "diez"), (64, 1, 13, "trece"), (64, 1, 14, "catorce"), (64, 1, 15, "quince"));

        var result = NumberingProfileApplier.Apply(verses, NumberingProfiles.MergePsalm47And3John);

        Assert.Equal(["PSA 47:9-10", "3JN 1:14-15"], result.AppliedMerges);
        Assert.Empty(result.SkippedMerges);
        Assert.Equal(
            [new SourceVerse(19, 47, 8, "ocho"), new SourceVerse(19, 47, 9, "nueve diez"), new SourceVerse(64, 1, 13, "trece"), new SourceVerse(64, 1, 14, "catorce quince")],
            result.Verses);
    }

    [Fact]
    public void A_merge_is_skipped_when_the_source_already_has_the_verses_joined()
    {
        var verses = Verses((19, 47, 9, "nueve ya unido"), (64, 1, 14, "catorce ya unido"));

        var result = NumberingProfileApplier.Apply(verses, NumberingProfiles.MergePsalm47And3John);

        Assert.Empty(result.AppliedMerges);
        Assert.Equal(["PSA 47:9-10", "3JN 1:14-15"], result.SkippedMerges);
        Assert.Equal(verses, result.Verses);
    }

    [Fact]
    public void No_profile_changes_nothing_and_the_order_is_kept()
    {
        var verses = Verses((1, 1, 1, "a"), (1, 1, 2, "b"), (2, 1, 1, "c"));
        var result = NumberingProfileApplier.Apply(verses, NumberingProfiles.None);
        Assert.Same(verses, result.Verses);
        Assert.Empty(result.AppliedMerges);
        Assert.Empty(result.SkippedMerges);
    }

    [Fact]
    public void A_partial_source_only_applies_the_merges_it_can()
    {
        var verses = Verses((19, 47, 9, "nueve"), (19, 47, 10, "diez"));   // sin 3 Juan
        var result = NumberingProfileApplier.Apply(verses, NumberingProfiles.MergePsalm47And3John);
        Assert.Equal(["PSA 47:9-10"], result.AppliedMerges);
        Assert.Equal(["3JN 1:14-15"], result.SkippedMerges);
        Assert.Equal([new SourceVerse(19, 47, 9, "nueve diez")], result.Verses);
    }
}

/// <summary>La importación desde el panel ofrece los perfiles de numeración del backend (H: importación por la interfaz).</summary>
public class ImportFormProfilesTests
{
    [Fact]
    public void The_panel_offers_exactly_the_numbering_profiles_the_backend_knows()
    {
        string? source = null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null && source is null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "frontend", "src", "control", "bible", "importController.ts");
            if (File.Exists(candidate)) source = File.ReadAllText(candidate);
        }
        Assert.NotNull(source);
        var block = System.Text.RegularExpressions.Regex.Match(source, @"NUMBERING_PROFILES[^=]*=\s*\[(.*?)\];", System.Text.RegularExpressions.RegexOptions.Singleline).Groups[1].Value;
        var ids = System.Text.RegularExpressions.Regex.Matches(block, @"id:\s*""([^""]+)""").Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(NumberingProfiles.All.Select(p => p.Id), ids);
    }
}