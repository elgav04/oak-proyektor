using System.Text.Json;
using OakProyektor.Core.Scripture;

namespace OakProyektor.Tests.Scripture;

public sealed class SourceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oak-proyektor-tests", "src-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (Exception) { /* best-effort */ } }

    private static readonly IBibleSourceFormat YouVersion = new YouVersionJsonSource();

    [Fact]
    public async Task Reads_every_verse_with_canonical_book_numbers_and_discards_everything_else()
    {
        var path = SyntheticBible.WriteYouVersion(_dir, SyntheticBible.Small());

        var data = await YouVersion.ReadAsync(path, CancellationToken.None);

        Assert.Equal(SyntheticBible.Small().Count, data.Verses.Count);
        Assert.Equal(SyntheticBible.TextOf(43, 3, 16), SyntheticBible.TextOf(43, 3, 16));   // el texto inventado es determinista
        var psalm = data.Verses.Single(v => v is { BookNumber: 19, Chapter: 47, VerseNumber: 10 });
        Assert.Equal(SyntheticBible.TextOf(19, 47, 10), psalm.Text);
        Assert.DoesNotContain(data.Verses, v => v.Text.Contains("HTML", StringComparison.Ordinal));          // el html no se lee
        Assert.DoesNotContain(data.Verses, v => v.Text.Contains("Titulo sintetico", StringComparison.Ordinal)); // los títulos no son versículos
        Assert.Equal(data.Verses.OrderBy(v => v.BookNumber).ThenBy(v => v.Chapter).ThenBy(v => v.VerseNumber), data.Verses);
    }

    [Fact]
    public async Task A_single_chapter_book_that_repeats_its_chapter_is_read_once_and_the_repeat_is_counted()
    {
        var path = SyntheticBible.WriteYouVersion(_dir, SyntheticBible.Chapter(65, 1, 6), repeatSingleChapterBooks: true);

        var data = await YouVersion.ReadAsync(path, CancellationToken.None);

        Assert.Equal(6, data.Verses.Count);                          // sin repetir: nada de «versículo dos veces»
        Assert.Equal(1, data.DuplicateChaptersDiscarded);
        Assert.All(data.Verses, v => Assert.Equal(SyntheticBible.TextOf(v.BookNumber, v.Chapter, v.VerseNumber), v.Text));
    }

    [Fact]
    public async Task Without_the_repeat_nothing_is_discarded()
    {
        var path = SyntheticBible.WriteYouVersion(_dir, SyntheticBible.Chapter(65, 1, 6), repeatSingleChapterBooks: false);
        Assert.Equal(0, (await YouVersion.ReadAsync(path, CancellationToken.None)).DuplicateChaptersDiscarded);
    }

    [Fact]
    public async Task A_verse_split_into_several_lines_is_joined_with_a_space_and_counted()
    {
        var path = SyntheticBible.WriteYouVersion(_dir, SyntheticBible.Chapter(1, 1, 3), splitInTwoLines: true);

        var data = await YouVersion.ReadAsync(path, CancellationToken.None);

        Assert.Equal(3, data.JoinedVerses);
        Assert.Equal(SyntheticBible.TextOf(1, 1, 2), data.Verses[1].Text);
    }

    [Fact]
    public async Task A_verse_split_over_two_items_is_joined_in_order()
    {
        var path = Path.Combine(_dir, "dos-items.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, """
            {"books":[{"usfm":"GEN","chapters":[{"usfm":"GEN.1","items":[
              {"type":"verse","verse_numbers":[1],"lines":["primera parte"]},
              {"type":"verse","verse_numbers":[1],"lines":["segunda parte"]},
              {"type":"verse","verse_numbers":[2],"lines":["otro"]}]}]}]}
            """);

        var data = await YouVersion.ReadAsync(path, CancellationToken.None);

        Assert.Equal(["primera parte segunda parte", "otro"], data.Verses.Select(v => v.Text));
        Assert.Equal(1, data.JoinedVerses);
    }

    [Fact]
    public async Task Unknown_books_bad_chapter_codes_and_items_without_numbers_are_skipped_with_a_note()
    {
        var path = Path.Combine(_dir, "raros.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, """
            {"books":[
              {"usfm":"ZZZ","chapters":[{"usfm":"ZZZ.1","items":[{"type":"verse","verse_numbers":[1],"lines":["x"]}]}]},
              {"usfm":"GEN","chapters":[
                {"usfm":"GEN.intro","items":[{"type":"verse","verse_numbers":[1],"lines":["x"]}]},
                {"usfm":"GEN.1","items":[
                  {"type":"verse","verse_numbers":[],"lines":["sin numero"]},
                  {"type":"label","verse_numbers":[],"lines":["rotulo"]},
                  {"type":"verse","verse_numbers":[1],"lines":["bueno"]}]}]}]}
            """);

        var data = await YouVersion.ReadAsync(path, CancellationToken.None);

        Assert.Equal([new SourceVerse(1, 1, 1, "bueno")], data.Verses);
        Assert.Equal(2, data.Notes.Count);
        Assert.All(data.Notes, n => Assert.DoesNotContain("bueno", n));   // los avisos no llevan texto del archivo
    }

    [Fact]
    public async Task A_file_that_is_not_the_format_fails_clearly()
    {
        Directory.CreateDirectory(_dir);
        var broken = Path.Combine(_dir, "roto.json"); File.WriteAllText(broken, "{ esto no es json");
        var other = Path.Combine(_dir, "otro.json"); File.WriteAllText(other, """{"algo":[1,2,3]}""");

        await Assert.ThrowsAsync<JsonException>(() => YouVersion.ReadAsync(broken, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => YouVersion.ReadAsync(other, CancellationToken.None));
    }

    [Fact]
    public async Task The_verification_only_format_maps_book_names_including_the_irregular_ones()
    {
        var verses = new[] { SyntheticBible.Verse(1, 1, 1), SyntheticBible.Verse(40, 5, 3), SyntheticBible.Verse(43, 3, 16), SyntheticBible.Verse(22, 2, 1) };
        var path = SyntheticBible.WriteNamedBooks(_dir, verses);

        var data = await new NamedBooksJsonSource().ReadAsync(path, CancellationToken.None);

        Assert.Equal([1, 22, 40, 43], data.Verses.Select(v => v.BookNumber));       // «S. Mateo» → 40; «lang» se ignora
        Assert.Equal(SyntheticBible.TextOf(40, 5, 3), data.Verses.Single(v => v.BookNumber == 40).Text);
        Assert.Empty(data.Notes);
    }

    [Fact]
    public void Only_the_approved_format_can_import_and_formats_are_found_by_id()
    {
        Assert.Equal("youversion-json", BibleSourceFormats.Default.Id);
        Assert.True(BibleSourceFormats.Default.CanImport);
        Assert.False(BibleSourceFormats.Find("named-books-json")!.CanImport);
        Assert.Same(BibleSourceFormats.Default, BibleSourceFormats.Find("YOUVERSION-JSON"));
        Assert.Null(BibleSourceFormats.Find("zefania"));
        Assert.Null(BibleSourceFormats.Find(null));
    }

    /// <summary>La interfaz permite añadir un formato sin tocar el importador: este de prueba se usa en BibleImporterTests.</summary>
    [Fact]
    public async Task A_new_format_only_needs_to_implement_the_interface()
    {
        IBibleSourceFormat custom = new InMemoryFormat([new SourceVerse(1, 1, 1, "x")]);
        var data = await custom.ReadAsync("ignorado", CancellationToken.None);
        Assert.Single(data.Verses);
    }

    internal sealed class InMemoryFormat(IReadOnlyList<SourceVerse> verses) : IBibleSourceFormat
    {
        public string Id => "memory";
        public string Description => "prueba";
        public bool CanImport => true;
        public Task<BibleSourceData> ReadAsync(string path, CancellationToken cancellationToken) => Task.FromResult(new BibleSourceData(verses, 0, 0, []));
    }
}
