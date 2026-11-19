using Dapper;

namespace OakProyektor.Tests.Data;

public sealed class CascadeTests : IDisposable
{
    private readonly TestDatabase _db = new();
    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Deleting_a_bible_cascades_to_books_and_verses_and_clears_fts()
    {
        await _db.InitializeAsync();
        await using var c = await _db.OpenAsync();
        var (bibleId, bookId, _) = await TestDatabase.InsertVerseAsync(c, "Jesús habló en la montaña", "AAA");
        await c.ExecuteAsync("INSERT INTO Verses (BookId, Chapter, VerseNumber, Text) VALUES (@b, 1, 2, 'segundo verso sintetico')", new { b = bookId });
        // Otra Biblia que debe quedar intacta.
        var (otherBible, _, _) = await TestDatabase.InsertVerseAsync(c, "Texto de otra biblia", "BBB");

        await c.ExecuteAsync("DELETE FROM Bibles WHERE Id = @id", new { id = bibleId });

        Assert.Equal(0L, await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Books WHERE BibleId = @id", new { id = bibleId }));
        Assert.Equal(0L, await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Verses WHERE BookId = @id", new { id = bookId }));
        // Los triggers de DELETE también se disparan en cascada: el índice FTS queda limpio.
        Assert.Equal(0L, await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM VersesFts WHERE VersesFts MATCH 'jesus OR segundo'"));
        // La otra biblia no se tocó.
        Assert.Equal(1L, await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Books WHERE BibleId = @id", new { id = otherBible }));
        Assert.Equal(1L, await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM VersesFts WHERE VersesFts MATCH 'otra'"));
    }

    [Fact]
    public async Task Deleting_a_presentation_cascades_to_its_slides()
    {
        await _db.InitializeAsync();
        await using var c = await _db.OpenAsync();
        var id = await c.ExecuteScalarAsync<long>("INSERT INTO Presentations (Title, SourceType) VALUES ('Anuncios', 'CUSTOM'); SELECT last_insert_rowid();");
        await c.ExecuteAsync("INSERT INTO PresentationSlides (PresentationId, SlideIndex, ImagePath) VALUES (@p, 0, 'a.png'), (@p, 1, 'b.png')", new { p = id });

        await c.ExecuteAsync("DELETE FROM Presentations WHERE Id = @p", new { p = id });

        Assert.Equal(0L, await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM PresentationSlides"));
    }
}
