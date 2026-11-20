using OakProyektor.Core.Scripture;

namespace OakProyektor.Tests.Scripture;

public class ReferenceParserTests
{
    private static ParsedReference Ok(string input)
    {
        var result = ReferenceParser.Parse(input);
        Assert.True(result.Success, $"«{input}» debía interpretarse, pero falló: {result.Error?.Code} {result.Error?.Message}");
        return result.Reference!;
    }

    private static ReferenceError Fails(string? input)
    {
        var result = ReferenceParser.Parse(input);
        Assert.False(result.Success, $"«{input}» debía fallar");
        Assert.NotNull(result.Error);
        Assert.False(string.IsNullOrWhiteSpace(result.Error!.Message));
        return result.Error!;
    }

    // ───────────────────────────── los ejemplos pedidos ─────────────────────────────

    [Fact]
    public void Jn_3_16()
    {
        var r = Ok("Jn 3:16");
        Assert.Equal(43, r.BookNumber);
        Assert.Equal([new ReferenceSegment(3, 16, 3, 16)], r.Segments);
    }

    [Fact]
    public void Juan_3_dot_16_uses_the_dot_as_the_chapter_verse_separator()
    {
        Assert.Equal([new ReferenceSegment(3, 16, 3, 16)], Ok("Juan 3.16").Segments);
        Assert.Equal(43, Ok("Juan 3.16").BookNumber);
    }

    [Fact]
    public void Jn3_16_without_any_space()
    {
        var r = Ok("jn3:16");
        Assert.Equal(43, r.BookNumber);
        Assert.Equal([new ReferenceSegment(3, 16, 3, 16)], r.Segments);
    }

    [Fact]
    public void First_Corinthians_13_4_to_7_with_the_number_belonging_to_the_book()
    {
        var r = Ok("1 co 13:4-7");
        Assert.Equal(46, r.BookNumber);
        Assert.Equal([new ReferenceSegment(13, 4, 13, 7)], r.Segments);
    }

    [Fact]
    public void A_chapter_alone_is_the_whole_chapter()
    {
        var r = Ok("Sal 23");
        Assert.Equal(19, r.BookNumber);
        Assert.Equal([new ReferenceSegment(23, null, 23, null)], r.Segments);
        Assert.True(r.Segments[0].IsWholeChapters);
    }

    [Fact]
    public void A_verse_range_in_one_chapter()
    {
        Assert.Equal([new ReferenceSegment(23, 1, 23, 6)], Ok("Sal 23:1-6").Segments);
    }

    [Fact]
    public void A_comma_lists_verses_of_the_same_chapter()
    {
        var r = Ok("Mt 5:3,5,7");
        Assert.Equal(40, r.BookNumber);
        Assert.Equal([new ReferenceSegment(5, 3, 5, 3), new ReferenceSegment(5, 5, 5, 5), new ReferenceSegment(5, 7, 5, 7)], r.Segments);
    }

    [Fact]
    public void A_range_can_cross_a_chapter()
    {
        var r = Ok("Gn 1:1-2:3");
        Assert.Equal(1, r.BookNumber);
        Assert.Equal([new ReferenceSegment(1, 1, 2, 3)], r.Segments);
    }

    // ───────────────────────────── variantes de forma ─────────────────────────────

    [Theory]
    [InlineData("Jn 3:16", "Jn 3:16")]
    [InlineData("JUAN 3:16", "Jn 3:16")]
    [InlineData("juan 3:16", "Jn 3:16")]
    [InlineData("  Jn   3 : 16  ", "Jn 3:16")]
    [InlineData("Jn 3,16", "Jn 3; 16")]                       // la coma sin «:» enumera capítulos (Juan tiene 21)
    [InlineData("génesis 1:1", "Gn 1:1")]
    [InlineData("GÉN 1:1", "Gn 1:1")]
    [InlineData("Gén. 1:1", "Gn 1:1")]
    [InlineData("Gen. 1:1", "Gn 1:1")]
    [InlineData("Exodo 20:3", "Éx 20:3")]
    [InlineData("Éxodo 20:3", "Éx 20:3")]
    [InlineData("1Co 13:4", "1 Co 13:4")]
    [InlineData("1 Corintios 13:4", "1 Co 13:4")]
    [InlineData("I Corintios 13:4", "1 Co 13:4")]
    [InlineData("i corintios 13:4", "1 Co 13:4")]
    [InlineData("Primera de Corintios 13", "1 Co 13")]
    [InlineData("1ra Corintios 13", "1 Co 13")]
    [InlineData("II Reyes 2:11", "2 R 2:11")]
    [InlineData("Segundo de Reyes 2:11", "2 R 2:11")]
    [InlineData("III Juan 4", "3 Jn 1:4")]
    [InlineData("Tercera de Juan 4", "3 Jn 1:4")]
    [InlineData("S. Juan 3:16", "Jn 3:16")]
    [InlineData("S.Juan 3:16", "Jn 3:16")]
    [InlineData("Cnt 2:1", "Cnt 2:1")]
    [InlineData("Cantares 2:1", "Cnt 2:1")]
    [InlineData("Cantar de los Cantares 2:1", "Cnt 2:1")]
    [InlineData("Salmo 23", "Sal 23")]
    [InlineData("Salmos 23", "Sal 23")]
    [InlineData("Sl 23", "Sal 23")]
    [InlineData("Sal 119:176", "Sal 119:176")]
    [InlineData("Jn 3:16–18", "Jn 3:16-18")]                  // raya corta
    [InlineData("Jn 3:16—18", "Jn 3:16-18")]                  // raya larga
    [InlineData("Jn 3:16-18,20", "Jn 3:16-18,20")]
    [InlineData("Jn 3:16; 4:1", "Jn 3:16; 4:1")]
    [InlineData("Jn 3:16,4:1", "Jn 3:16; 4:1")]               // «4:» es un capítulo nuevo
    [InlineData("Sal 23-25", "Sal 23-25")]
    [InlineData("Sal 23,25", "Sal 23; 25")]
    [InlineData("Sal 23; 25", "Sal 23; 25")]
    [InlineData("Ap 22:21", "Ap 22:21")]
    [InlineData("Hechos de los Apóstoles 2:1", "Hch 2:1")]
    [InlineData("Gene 1:1", "Gn 1:1")]                         // prefijo único
    [InlineData("Apocalip 22:1", "Ap 22:1")]
    public void Accepted_forms_are_normalized(string input, string canonical)
    {
        Assert.Equal(canonical, Ok(input).Format());
    }

    [Theory]
    [InlineData("Jud 14", "Jud 1:14")]
    [InlineData("Jud 3-5", "Jud 1:3-5")]
    [InlineData("Jud 3,5", "Jud 1:3,5")]
    [InlineData("Jud 1:14", "Jud 1:14")]
    [InlineData("3 Jn 4", "3 Jn 1:4")]
    [InlineData("Abd 1:1-21", "Abd 1:1-21")]
    [InlineData("Flm 10", "Flm 1:10")]
    [InlineData("Judas", "Jud")]                                // un libro de un solo capítulo, sin números = el libro entero
    [InlineData("2 Jn", "2 Jn")]
    public void Single_chapter_books_read_bare_numbers_as_verses(string input, string canonical)
    {
        Assert.Equal(canonical, Ok(input).Format());
    }

    [Fact]
    public void A_bare_single_chapter_book_is_the_whole_chapter_one()
    {
        var r = Ok("Judas");
        Assert.Equal(65, r.BookNumber);
        Assert.Equal([new ReferenceSegment(1, null, 1, null)], r.Segments);
    }

    // ───────────────────────────── límites ─────────────────────────────

    [Theory]
    [InlineData("Gn 50:26")]
    [InlineData("Gn 1:1")]
    [InlineData("Sal 150:6")]
    [InlineData("Sal 119:176")]
    [InlineData("Ap 22:21")]
    [InlineData("Jud 1:25")]
    public void The_edges_of_the_canon_are_valid(string input) => Assert.True(ReferenceParser.Parse(input).Success);

    [Fact]
    public void The_last_chapter_of_every_book_is_valid_and_the_next_one_is_not()
    {
        foreach (var book in CanonicalBooks.All)
        {
            Assert.True(ReferenceParser.Parse($"{book.Name} {book.Chapters}:1").Success, $"{book.Name} {book.Chapters}:1");
            if (book.Chapters > 1)
                Assert.Equal(ReferenceErrorCode.ChapterOutOfRange, Fails($"{book.Name} {book.Chapters + 1}:1").Code);
        }
    }

    [Fact]
    public void Every_canonical_name_short_name_and_alias_parses_to_its_book()
    {
        foreach (var alias in BookNames.All)
        {
            var r = ReferenceParser.Parse($"{alias.Alias} 1:1");
            Assert.True(r.Success, $"«{alias.Alias}» 1:1 → {r.Error?.Message}");
            Assert.Equal(alias.BookNumber, r.Reference!.BookNumber);
        }
        foreach (var book in CanonicalBooks.All)
        {
            Assert.Equal(book.Number, Ok($"{book.Name} 1:1").BookNumber);
            Assert.Equal(book.Number, Ok($"{book.ShortName} 1:1").BookNumber);
            Assert.Equal(book.Number, Ok($"{book.Name.ToUpperInvariant()} 1:1").BookNumber);
        }
    }

    [Fact]
    public void A_huge_number_does_not_overflow_it_is_just_out_of_range()
    {
        Assert.Equal(ReferenceErrorCode.ChapterOutOfRange, Fails("Jn 99999999999999999999").Code);
        Assert.Equal(ReferenceErrorCode.InvalidNumber, Fails("Jn 3:99999999999999999999").Code);
    }

    // ───────────────────────────── errores claros ─────────────────────────────

    [Theory]
    [InlineData(null, ReferenceErrorCode.Empty)]
    [InlineData("", ReferenceErrorCode.Empty)]
    [InlineData("   ", ReferenceErrorCode.Empty)]
    [InlineData("3:16", ReferenceErrorCode.MissingBook)]
    [InlineData("16", ReferenceErrorCode.MissingBook)]
    [InlineData("1 3:16", ReferenceErrorCode.MissingBook)]
    [InlineData("Xyz 1:1", ReferenceErrorCode.BookNotFound)]
    [InlineData("Jn3 3:16x", ReferenceErrorCode.UnexpectedText)]
    [InlineData("Jn", ReferenceErrorCode.MissingChapter)]
    [InlineData("Génesis", ReferenceErrorCode.MissingChapter)]
    [InlineData("Jn 0:1", ReferenceErrorCode.InvalidNumber)]
    [InlineData("Jn 0", ReferenceErrorCode.InvalidNumber)]
    [InlineData("Jn 3:0", ReferenceErrorCode.InvalidNumber)]
    [InlineData("Jn 3:177", ReferenceErrorCode.InvalidNumber)]
    [InlineData("Jn 22:1", ReferenceErrorCode.ChapterOutOfRange)]
    [InlineData("Jn 22", ReferenceErrorCode.ChapterOutOfRange)]
    [InlineData("Sal 151", ReferenceErrorCode.ChapterOutOfRange)]
    [InlineData("Jn 3:18-16", ReferenceErrorCode.InvalidRange)]
    [InlineData("Sal 25-23", ReferenceErrorCode.InvalidRange)]
    [InlineData("Gn 2:1-1:3", ReferenceErrorCode.InvalidRange)]
    [InlineData("Gn 1:1-1:0", ReferenceErrorCode.InvalidNumber)]
    [InlineData("Jn 3:", ReferenceErrorCode.IncompleteReference)]
    [InlineData("Jn 3:16-", ReferenceErrorCode.IncompleteReference)]
    [InlineData("Jn 3-", ReferenceErrorCode.IncompleteReference)]
    [InlineData("Jn 3:16,", ReferenceErrorCode.IncompleteReference)]
    [InlineData("Jn 3:16;", ReferenceErrorCode.IncompleteReference)]
    [InlineData("Jn 3:16 x", ReferenceErrorCode.UnexpectedText)]
    [InlineData("Jn 3:16 4", ReferenceErrorCode.UnexpectedText)]
    [InlineData("Jn 3:16!", ReferenceErrorCode.UnexpectedText)]
    [InlineData("Gn 1-2:3", ReferenceErrorCode.UnexpectedText)]
    [InlineData("Jud 3-1", ReferenceErrorCode.InvalidRange)]
    [InlineData("Jud 2:1", ReferenceErrorCode.ChapterOutOfRange)]
    public void Invalid_references_fail_with_a_specific_code(string? input, ReferenceErrorCode code)
    {
        Assert.Equal(code, Fails(input).Code);
    }

    [Fact]
    public void An_ambiguous_book_lists_its_candidates_and_a_missing_book_does_not()
    {
        var jo = Fails("Jo 3:16");
        Assert.Equal(ReferenceErrorCode.BookAmbiguous, jo.Code);
        Assert.Equal([6, 18, 29, 32], jo.Candidates);   // Josué, Job, Joel, Jonás

        var ju = Fails("Ju 3:16");
        Assert.Equal(ReferenceErrorCode.BookAmbiguous, ju.Code);
        Assert.Contains(43, ju.Candidates);   // Juan
        Assert.Contains(65, ju.Candidates);   // Judas
        Assert.Contains(7, ju.Candidates);    // Jueces
        Assert.Contains("Juan", ju.Message);

        var missing = Fails("Zzz 1:1");
        Assert.Empty(missing.Candidates);
        Assert.Contains("Zzz", missing.Message);
    }

    [Fact]
    public void Messages_are_in_spanish_and_never_echo_more_than_a_short_piece_of_the_input()
    {
        var error = Fails(new string('x', 500) + " 1:1");
        Assert.True(error.Message.Length < 200, error.Message);
    }

    // ───────────────────────────── propiedades ─────────────────────────────

    [Theory]
    [InlineData("Jn 3:16")]
    [InlineData("1 Co 13:4-7")]
    [InlineData("Sal 23")]
    [InlineData("Sal 23-25")]
    [InlineData("Mt 5:3,5,7")]
    [InlineData("Gn 1:1-2:3")]
    [InlineData("Jn 3:16-18,20")]
    [InlineData("Jn 3:16; 4:1")]
    [InlineData("Sal 23; 25")]
    [InlineData("Jud")]
    [InlineData("Jud 1:3-5")]
    [InlineData("Jud 1:3,5")]
    public void Format_is_the_inverse_of_Parse(string canonical)
    {
        var first = Ok(canonical);
        Assert.Equal(canonical, first.Format());
        Assert.Equal(first.Segments, Ok(first.Format()).Segments);
    }

    [Fact]
    public void Parse_never_throws_and_anything_it_accepts_round_trips()
    {
        // Entradas aleatorias reproducibles: la mitad empieza con un alias real seguido de piezas del lenguaje en cualquier orden (así hay muchas válidas),
        // la otra mitad es basura con todos los símbolos.
        var random = new Random(20260519);
        string[] pieces = ["", " ", "1", "2", "3", "5", "7", "16", "23", "119", ":", ".", "-", ",", ";", "–", " 3", ":16", "-18", ",20"];
        const string alphabet = "0123456789:.-,; abcdefgjnsmtlco123 –—ÉéñÑ\t";
        var aliases = BookNames.All;
        var accepted = 0;
        for (var i = 0; i < 30_000; i++)
        {
            string input;
            if (i % 2 == 0)
            {
                var sb = new System.Text.StringBuilder(aliases[random.Next(aliases.Count)].Alias).Append(random.Next(2) == 0 ? " " : "");
                for (var n = random.Next(0, 6); n > 0; n--) sb.Append(pieces[random.Next(pieces.Length)]);
                input = sb.ToString();
            }
            else
            {
                var chars = new char[random.Next(0, 14)];
                for (var c = 0; c < chars.Length; c++) chars[c] = alphabet[random.Next(alphabet.Length)];
                input = new string(chars);
            }

            var result = ReferenceParser.Parse(input);
            Assert.True(result.Success ^ (result.Error is not null), "Debe haber exactamente un resultado: referencia o error");
            if (!result.Success) continue;

            accepted++;
            var formatted = result.Reference!.Format();
            var again = ReferenceParser.Parse(formatted);
            Assert.True(again.Success, $"«{input}» → «{formatted}» no se vuelve a interpretar: {again.Error?.Message}");
            Assert.Equal(result.Reference.BookNumber, again.Reference!.BookNumber);
            Assert.Equal(result.Reference.Segments, again.Reference.Segments);
        }
        Assert.True(accepted > 50, $"El generador debe producir referencias válidas para que la prueba valga (hubo {accepted})");
    }

    [Fact]
    public void Segments_are_always_well_formed()
    {
        foreach (var input in new[] { "Gn 1:1-2:3", "Sal 23-25", "Mt 5:3,5,7", "Jn 3:16; 4:1-3", "Jud 3-5" })
        {
            var r = Ok(input);
            var book = CanonicalBooks.Get(r.BookNumber);
            foreach (var s in r.Segments)
            {
                Assert.InRange(s.StartChapter, 1, book.Chapters);
                Assert.InRange(s.EndChapter, s.StartChapter, book.Chapters);
                Assert.Equal(s.StartVerse is null, s.EndVerse is null);
                if (s.StartVerse is { } start) { Assert.True(start >= 1); Assert.True(s.EndChapter > s.StartChapter || s.EndVerse >= start); }
            }
        }
    }
}
