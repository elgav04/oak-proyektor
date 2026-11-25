using System.Diagnostics;
using System.Text;
using OakProyektor.Core.Songs;

namespace OakProyektor.Tests.Songs;

/// <summary>El parser de texto de canciones: función pura que nunca lanza. Toda la letra de estas pruebas es sintética.</summary>
public class SongTextParserTests
{
    private static ParsedSong Parse(string text, bool merge = true) => SongTextParser.Parse(text, new SongParseOptions(merge));

    private static string[] Labels(ParsedSong p) => [.. p.Sections.Select(s => s.Label)];

    // ───────────────────────────── etiquetas y alias ─────────────────────────────

    [Fact]
    public void Bracket_labels_define_sections_in_order_with_kind_and_text()
    {
        var p = Parse("[Verso 1]\nlinea uno\nlinea dos\n\n[Coro]\nlinea coro\n\n[Puente]\nlinea puente");

        Assert.Equal(SongTextFormat.Brackets, p.Format);
        Assert.Equal(["Verso 1", "Coro", "Puente"], Labels(p));
        Assert.Equal(["verse", "chorus", "bridge"], p.Sections.Select(s => s.Kind));
        Assert.Equal("linea uno\nlinea dos", p.Sections[0].Text);
        Assert.Equal([0, 1, 2], p.Arrangement);
        Assert.Empty(p.Errors);
        Assert.Empty(p.Warnings);
    }

    [Theory]
    [InlineData("[VERSO 1]", "Verso 1", "verse")]
    [InlineData("[verso1]", "Verso 1", "verse")]
    [InlineData("[Verso   01]", "Verso 1", "verse")]
    [InlineData("[estrofa 2]", "Estrofa 2", "verse")]
    [InlineData("[CORO]", "Coro", "chorus")]
    [InlineData("[Estribillo]", "Estribillo", "chorus")]
    [InlineData("[Pre-coro]", "Pre-coro", "prechorus")]
    [InlineData("[precoro]", "Pre-coro", "prechorus")]
    [InlineData("[PRE CORO 2]", "Pre-coro 2", "prechorus")]
    [InlineData("[Pre-Estribillo]", "Pre-estribillo", "prechorus")]
    [InlineData("[puente]", "Puente", "bridge")]
    [InlineData("[FINAL]", "Final", "ending")]
    [InlineData("[Coda]", "Coda", "ending")]
    [InlineData("[intro]", "Intro", "intro")]
    [InlineData("[Introduccion]", "Introducción", "intro")]
    [InlineData("[INTRODUCCIÓN]", "Introducción", "intro")]
    [InlineData("[interludio]", "Interludio", "interlude")]
    [InlineData("[Instrumental]", "Instrumental", "interlude")]
    [InlineData("[Verso 1]:", "Verso 1", "verse")]          // dos puntos finales tolerados
    [InlineData("[ Coro ]", "Coro", "chorus")]
    public void Aliases_are_case_accent_and_hyphen_insensitive_and_get_their_canonical_name(string line, string label, string kind)
    {
        var p = Parse(line + "\ntexto sintetico");
        Assert.Equal([label], Labels(p));
        Assert.Equal(kind, p.Sections[0].Kind);
    }

    [Theory]
    [InlineData("[Coro final]", "Coro final", "chorus")]          // empieza por un nombre conocido: tipo coro, etiqueta tal cual
    [InlineData("[Parte A]", "Parte A", "other")]
    [InlineData("[Mi sección extra]", "Mi sección extra", "other")]
    [InlineData("[Verso 2b]", "Verso 2b", "verse")]
    public void Unknown_or_composite_labels_keep_their_text_and_derive_the_kind_from_the_first_word(string line, string label, string kind)
    {
        var p = Parse(line + "\ntexto sintetico");
        Assert.Equal(label, p.Sections[0].Label);
        Assert.Equal(kind, p.Sections[0].Kind);
        Assert.Equal(kind, SectionAliases.KindOf(label));
    }

    // ───────────────────────────── cortes manuales y limpieza ─────────────────────────────

    [Fact]
    public void A_blank_line_inside_a_section_is_a_manual_slide_cut_and_runs_collapse_to_one()
    {
        var p = Parse("[Verso 1]\nuno\ndos\n\n\n\ntres\ncuatro\n\n\n[Coro]\ncinco");

        Assert.Equal("uno\ndos\n\ntres\ncuatro", p.Sections[0].Text);
        Assert.Equal("cinco", p.Sections[1].Text);
    }

    [Fact]
    public void Text_is_cleaned_without_touching_punctuation_accents_or_case()
    {
        var p = Parse("\uFEFF[Verso 1]\r\n  ¡Lumarí,   TENOKO!  \r\n\tseñor;  ñandú…  \r\n");

        Assert.Equal("¡Lumarí, TENOKO!\nseñor; ñandú…", p.Sections[0].Text);
    }

    [Fact]
    public void Text_is_normalized_to_NFC()
    {
        var p = Parse("[Verso 1]\nsen\u0303or cafe\u0301");   // n + tilde combinante, e + acento combinante

        Assert.Equal("se\u00F1or caf\u00E9", p.Sections[0].Text);
    }

    [Fact]
    public void Control_and_invisible_characters_are_removed_and_counted_in_a_warning()
    {
        var p = Parse("[Verso 1]\nli\u0000nea\u0007 uno\u200B\u202E\ndos\u0001");

        Assert.Equal("linea uno\ndos", p.Sections[0].Text);
        Assert.Contains(p.Warnings, w => w.Contains("5 caracteres de control"));
    }

    [Fact]
    public void Lone_surrogates_are_removed_but_real_pairs_survive()
    {
        var p = Parse("[Verso 1]\nuno \uD800 dos \uDC00 tres \uD83C\uDFB5");

        Assert.Equal("uno dos tres \uD83C\uDFB5", p.Sections[0].Text);
    }

    [Fact]
    public void Unicode_line_separators_and_old_mac_line_endings_split_lines()
    {
        var p = Parse("[Verso 1]\runo\u2028dos\u2029tres\u0085cuatro");

        Assert.Equal("uno\ndos\ntres\ncuatro", p.Sections[0].Text);
    }

    // ───────────────────────────── repeticiones ─────────────────────────────

    [Fact]
    public void A_bare_label_repeats_the_section_in_the_arrangement_without_duplicating_its_text()
    {
        var p = Parse("[Verso 1]\nuno\n\n[Coro]\ncoro\n\n[Verso 2]\ndos\n\n[Coro]\n\n[Coro]\n");

        Assert.Equal(["Verso 1", "Coro", "Verso 2"], Labels(p));
        Assert.Equal([0, 1, 2, 1, 1], p.Arrangement);
        Assert.Empty(p.Errors);
    }

    [Fact]
    public void A_label_repeated_with_the_same_text_is_a_repetition()
    {
        var p = Parse("[Coro]\nla letra del coro\n\n[Verso 1]\nuno\n\n[coro]\nla letra del coro");

        Assert.Equal(["Coro", "Verso 1"], Labels(p));
        Assert.Equal([0, 1, 0], p.Arrangement);
        Assert.Contains(p.Warnings, w => w.Contains("repetición"));
    }

    [Fact]
    public void A_label_repeated_with_different_text_makes_a_new_section_and_warns()
    {
        var p = Parse("[Coro]\nprimera version\n\n[Coro]\nsegunda version\n\n[Coro]\ntercera version");

        Assert.Equal(["Coro", "Coro 2", "Coro 3"], Labels(p));
        Assert.Equal(["chorus", "chorus", "chorus"], p.Sections.Select(s => s.Kind));
        Assert.Equal([0, 1, 2], p.Arrangement);
        Assert.Equal(2, p.Warnings.Count(w => w.Contains("texto distinto")));
        Assert.Empty(p.Errors);
    }

    [Fact]
    public void The_generated_name_never_collides_with_a_label_written_later_in_the_text()
    {
        var p = Parse("[Coro]\nversion a\n\n[Coro]\nversion b\n\n[Coro 2]\nversion propia del coro dos");

        Assert.Equal(3, p.Sections.Count);
        Assert.Equal(3, p.Sections.Select(s => SectionAliases.Key(s.Label)).Distinct().Count());
        Assert.Contains("Coro 2", Labels(p));
        Assert.Equal("version propia del coro dos", p.Sections.Single(s => s.Label == "Coro 2").Text);
    }

    [Fact]
    public void A_bare_label_may_come_before_its_definition()
    {
        var p = Parse("[Coro]\n\n[Verso 1]\nuno\n\n[Coro]\ncoro sintetico");

        Assert.Equal(["Verso 1", "Coro"], Labels(p));
        Assert.Equal([1, 0, 1], p.Arrangement);
    }

    [Fact]
    public void A_bare_label_that_is_never_defined_is_ignored_with_a_warning()
    {
        var p = Parse("[Verso 1]\nuno\n\n[Puente]\n");

        Assert.Equal(["Verso 1"], Labels(p));
        Assert.Equal([0], p.Arrangement);
        Assert.Contains(p.Warnings, w => w.Contains("Puente"));
    }

    [Fact]
    public void Text_before_the_first_label_becomes_an_unnamed_section_with_a_warning()
    {
        var p = Parse("texto suelto sintetico\n\n[Coro]\ncoro");

        Assert.Equal(["Sección 1", "Coro"], Labels(p));
        Assert.Contains(p.Warnings, w => w.Contains("antes de la primera etiqueta"));
    }

    [Fact]
    public void A_label_without_letters_keeps_its_text_as_an_unnamed_section()
    {
        var p = Parse("[Verso 1]\nuno\n\n[!!!]\nconserva este texto");

        Assert.Equal(2, p.Sections.Count);
        Assert.Contains(p.Sections, s => s.Text == "conserva este texto");
        Assert.Contains(p.Warnings, w => w.Contains("sin letras ni números"));
    }

    // ───────────────────────────── sin etiquetas ─────────────────────────────

    [Fact]
    public void Without_labels_blocks_separated_by_blank_lines_are_sections()
    {
        var p = Parse("uno\ndos\n\ntres\ncuatro\n\n\ncinco");

        Assert.Equal(SongTextFormat.Blocks, p.Format);
        Assert.Equal(["Sección 1", "Sección 2", "Sección 3"], Labels(p));
        Assert.Equal(["uno\ndos", "tres\ncuatro", "cinco"], p.Sections.Select(s => s.Text));
        Assert.All(p.Sections, s => Assert.Equal("other", s.Kind));
        Assert.Equal([0, 1, 2], p.Arrangement);
    }

    [Fact]
    public void Identical_blocks_are_merged_as_repetitions_unless_asked_not_to()
    {
        const string text = "primer bloque\n\ncoro del bloque\n\nsegundo bloque\n\ncoro del bloque";

        var merged = Parse(text);
        Assert.Equal(3, merged.Sections.Count);
        Assert.Equal([0, 1, 2, 1], merged.Arrangement);
        Assert.Contains(merged.Warnings, w => w.Contains("repetición"));

        var separate = Parse(text, merge: false);
        Assert.Equal(4, separate.Sections.Count);
        Assert.Equal([0, 1, 2, 3], separate.Arrangement);
    }

    [Fact]
    public void Lines_that_are_exactly_a_known_section_name_are_taken_as_labels_when_there_are_no_brackets()
    {
        var p = Parse("Verso 1\nlinea uno\n\nCoro:\nlinea coro\n\nVerso 2\nlinea dos\n\nCoro");

        Assert.Equal(SongTextFormat.AliasLines, p.Format);
        Assert.Equal(["Verso 1", "Coro", "Verso 2"], Labels(p));
        Assert.Equal([0, 1, 2, 1], p.Arrangement);
    }

    [Fact]
    public void A_lyric_line_that_merely_contains_a_section_word_is_not_a_label()
    {
        var p = Parse("el coro canta aqui\nfinal feliz\n\notra estrofa sintetica");

        Assert.Equal(SongTextFormat.Blocks, p.Format);
        Assert.Equal(2, p.Sections.Count);
    }

    [Fact]
    public void With_bracket_labels_a_bare_alias_line_is_just_lyrics()
    {
        var p = Parse("[Verso 1]\nCoro\nlinea");

        Assert.Equal(SongTextFormat.Brackets, p.Format);
        Assert.Equal("Coro\nlinea", p.Sections[0].Text);
    }

    [Theory]
    [InlineData("[]\nlinea")]
    [InlineData("[   ]\nlinea")]
    [InlineData("[[Coro]]\nlinea")]
    [InlineData("[un texto entre corchetes que es demasiado largo para ser una etiqueta de verdad, sin duda alguna]\nlinea")]
    public void Things_that_are_not_valid_labels_are_just_text(string text)
    {
        var p = Parse(text);

        Assert.Equal(SongTextFormat.Blocks, p.Format);
        Assert.Single(p.Sections);
        Assert.Contains("linea", p.Sections[0].Text);
    }

    // ───────────────────────────── línea «Orden:» ─────────────────────────────

    [Fact]
    public void The_order_line_with_full_labels_sets_the_arrangement()
    {
        var p = Parse("[Verso 1]\nuno\n\n[Coro]\ncoro\n\n[Puente]\npuente\n\nOrden: Verso 1, Coro, Puente, Coro, Coro");

        Assert.Equal([0, 1, 2, 1, 1], p.Arrangement);
        Assert.Equal(3, p.Sections.Count);
        Assert.Empty(p.Errors);
        Assert.DoesNotContain(p.Sections, s => s.Text.Contains("Orden"));
    }

    [Theory]
    [InlineData("Orden: V1 C V2 C", new[] { 0, 2, 1, 2 })]
    [InlineData("orden: v1 c v2 c", new[] { 0, 2, 1, 2 })]
    [InlineData("ORDEN:V1,C,V2,C", new[] { 0, 2, 1, 2 })]
    [InlineData("Orden: Verso 1 Coro Verso 2 Coro", new[] { 0, 2, 1, 2 })]
    [InlineData("Orden: Verso 1 > Coro > Verso 2", new[] { 0, 2, 1 })]
    [InlineData("Orden: P V1 P", new[] { 3, 0, 3 })]
    public void The_order_line_accepts_abbreviations_whitespace_and_other_delimiters(string orderLine, int[] expected)
    {
        var p = Parse("[Verso 1]\nuno\n\n[Verso 2]\ndos\n\n[Coro]\ncoro\n\n[Puente]\npuente\n\n" + orderLine);

        // Las secciones quedan en el orden en que se definieron: Verso 1 (0), Verso 2 (1), Coro (2), Puente (3).
        Assert.Equal(expected, p.Arrangement);
        Assert.Empty(p.Errors);
    }

    [Fact]
    public void An_ambiguous_abbreviation_is_not_guessed()
    {
        var p = Parse("[Coro 1]\nuno\n\n[Coro 2]\ndos\n\nOrden: C C1 C2");

        Assert.Equal([0, 1], p.Arrangement);                                   // «C» es ambigua y se ignora; C1 y C2 se resuelven
        Assert.Contains(p.Warnings, w => w.Contains("elemento 1") || w.Contains("palabra 1"));
    }

    [Fact]
    public void Unknown_tokens_in_the_order_line_are_reported_and_the_rest_is_kept()
    {
        var p = Parse("[Verso 1]\nuno\n\n[Coro]\ncoro\n\nOrden: V1, Z9, C");

        Assert.Equal([0, 1], p.Arrangement);
        Assert.Contains(p.Warnings, w => w.Contains("elemento 2"));
    }

    [Fact]
    public void An_order_line_that_matches_nothing_is_ignored_and_the_natural_order_stays()
    {
        var p = Parse("[Verso 1]\nuno\n\n[Coro]\ncoro\n\nOrden: Q Z");

        Assert.Equal([0, 1], p.Arrangement);
        Assert.Contains(p.Warnings, w => w.Contains("no coincide con ninguna sección"));
    }

    [Fact]
    public void An_empty_order_line_is_an_explicit_empty_order_and_cannot_be_saved()
    {
        var p = Parse("[Verso 1]\nuno\n\nOrden:");

        Assert.Empty(p.Arrangement);
        Assert.Contains(p.Errors, e => e.Contains("orden está vacío"));
    }

    [Fact]
    public void The_order_line_is_only_recognised_as_the_last_non_blank_line()
    {
        var p = Parse("[Verso 1]\nuno\nOrden: algo\nmas texto");

        Assert.Equal("uno\nOrden: algo\nmas texto", p.Sections[0].Text);   // es letra
        Assert.Contains(p.Errors, e => e.Contains("Orden:"));              // pero ambigua: no se puede guardar así
    }

    // ───────────────────────────── límites y entradas hostiles ─────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\t\n  ")]
    [InlineData("\n\n\n")]
    public void Empty_input_gives_an_empty_song_without_errors(string? text)
    {
        var p = SongTextParser.Parse(text);

        Assert.Equal(SongTextFormat.Empty, p.Format);
        Assert.Empty(p.Sections);
        Assert.Empty(p.Arrangement);
        Assert.Empty(p.Errors);
    }

    [Fact]
    public void Text_over_the_size_limit_is_refused_without_processing_it()
    {
        var p = SongTextParser.Parse(new string('a', SongLimits.MaxTextChars + 1));

        Assert.Empty(p.Sections);
        Assert.Single(p.Errors);
        Assert.Contains("demasiado grande", p.Errors[0]);
    }

    [Fact]
    public void Thousands_of_lines_parse_fast_and_report_the_excess_instead_of_cutting_it()
    {
        var sb = new StringBuilder("[Verso 1]\n");
        for (var i = 0; i < 8_000; i++) sb.Append("linea sintetica ").Append(i).Append('\n');
        Assert.True(sb.Length < SongLimits.MaxTextChars);                    // el texto en sí cabe; es la SECCIÓN la que se pasa de largo
        var clock = Stopwatch.StartNew();

        var p = SongTextParser.Parse(sb.ToString());

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"tardó {clock.Elapsed}");
        Assert.Single(p.Sections);
        Assert.Contains(p.Errors, e => e.Contains("demasiado larga"));        // supera las 4000 letras de una sección: se informa, no se recorta
        Assert.Equal(8_000, p.Sections[0].Text.Split('\n').Length);
    }

    [Fact]
    public void Thousands_of_repeated_labels_are_bounded_and_reported()
    {
        var sb = new StringBuilder("[Coro]\ncoro sintetico\n");
        for (var i = 0; i < 6000; i++) sb.Append("[Coro]\n");
        var clock = Stopwatch.StartNew();

        var p = SongTextParser.Parse(sb.ToString());

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"tardó {clock.Elapsed}");
        Assert.Single(p.Sections);
        Assert.Contains(p.Errors, e => e.Contains("orden es demasiado largo"));
    }

    [Fact]
    public void Thousands_of_distinct_sections_with_the_same_label_stay_bounded_and_do_not_go_quadratic()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 4000; i++) sb.Append("[Coro]\ntexto distinto ").Append(i).Append('\n');
        var clock = Stopwatch.StartNew();

        var p = SongTextParser.Parse(sb.ToString());

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"tardó {clock.Elapsed}");
        Assert.Contains(p.Errors, e => e.Contains("demasiadas secciones"));
    }

    [Fact]
    public void More_than_sixty_sections_are_reported_as_an_error()
    {
        var sb = new StringBuilder();
        for (var i = 1; i <= SongLimits.MaxSections + 1; i++) sb.Append("[Seccion ").Append(i).Append("]\ntexto ").Append(i).Append("\n\n");

        var p = SongTextParser.Parse(sb.ToString());

        Assert.Contains(p.Errors, e => e.Contains("demasiadas secciones"));
    }

    [Fact]
    public void A_hostile_order_line_with_thousands_of_tokens_is_bounded()
    {
        var text = "[Coro]\ncoro sintetico\n\nOrden: " + string.Join(' ', Enumerable.Repeat("C", 50_000));
        var clock = Stopwatch.StartNew();

        var p = SongTextParser.Parse(text);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"tardó {clock.Elapsed}");
        Assert.Contains(p.Errors, e => e.Contains("orden es demasiado largo"));
    }

    [Fact]
    public void The_number_of_warnings_is_capped()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 500; i++) sb.Append("[Sin definir ").Append(i).Append("]\n");
        sb.Append("[Verso 1]\nuno\n");

        var p = SongTextParser.Parse(sb.ToString());

        Assert.True(p.Warnings.Count <= SongLimits.MaxWarnings + 1);
        Assert.Contains(p.Warnings, w => w.StartsWith("…y "));
    }

    [Fact]
    public void Warnings_and_errors_never_quote_the_lyrics()
    {
        var p = SongTextParser.Parse($"[Verso 1]\n{SyntheticSong.Sentinel}\nOrden: algo\nmas\n\n[Coro]\n{SyntheticSong.Sentinel}\n\n[Coro]\notra {SyntheticSong.Sentinel}\n\nOrden: Q");

        Assert.NotEmpty(p.Warnings.Concat(p.Errors));
        Assert.All(p.Warnings.Concat(p.Errors), m => Assert.DoesNotContain("Xilofon", m));
    }

    [Fact]
    public void Fuzzing_with_random_text_never_throws_and_always_returns_a_consistent_result()
    {
        string[] pieces = ["[", "]", "[Coro]", "[Verso 1]", "Orden:", "Orden: V1 C", "\n", "\n\n", "\r\n", "\u0000", "\u200B", "\uD800", "\t", " ", ":", "Coro", "texto", "ñ", "é", ",", ";", "[Coro (2)]", "🎵", "\u2028"];
        for (var seed = 0; seed < 3000; seed++)
        {
            var random = new Random(seed);
            var sb = new StringBuilder();
            for (var i = 0; i < random.Next(0, 40); i++) sb.Append(pieces[random.Next(pieces.Length)]);

            var p = SongTextParser.Parse(sb.ToString(), new SongParseOptions(random.Next(2) == 0));

            Assert.All(p.Arrangement, index => Assert.InRange(index, 0, p.Sections.Count - 1));
            Assert.All(p.Sections, s =>
            {
                Assert.False(string.IsNullOrWhiteSpace(s.Text));
                Assert.Equal(SongTextNormalizer.NormalizeBody(s.Text, out _), s.Text);   // ya normalizado
                Assert.Equal(SectionAliases.KindOf(s.Label), s.Kind);
            });
            Assert.Equal(p.Sections.Count, p.Sections.Select(s => SectionAliases.Key(s.Label)).Distinct().Count());   // etiquetas únicas
        }
    }

    // ───────────────────────────── el validador ─────────────────────────────

    [Fact]
    public void The_validator_rejects_lines_that_would_be_read_back_as_labels_or_order_lines()
    {
        var sections = new List<ParsedSection> { new("Verso 1", "verse", "uno\n[Coro]\ndos"), new("Verso 2", "verse", "tres\nOrden: x") };

        var problems = SongValidator.ValidateStructure(sections, [0, 1]);

        Assert.Equal(2, problems.Count);
        Assert.All(problems, p => Assert.DoesNotContain("uno", p));
    }

    [Fact]
    public void The_validator_reports_structural_problems_by_position_not_by_text()
    {
        var sections = new List<ParsedSection>
        {
            new("Coro", "chorus", "uno"), new("coro", "chorus", "dos"), new("", "other", "tres"), new("Con, coma", "other", "cuatro"),
            new("Vacia", "other", "   "), new("Larga", "other", new string('x', SongLimits.MaxSectionChars + 1)), new("Rara", "inventado", "siete"),
        };

        var problems = SongValidator.ValidateStructure(sections, [0, 99]);

        Assert.Contains(problems, p => p.Contains("secciones 1 y 2") && p.Contains("misma etiqueta"));
        Assert.Contains(problems, p => p.StartsWith("La sección 3:"));
        Assert.Contains(problems, p => p.StartsWith("La sección 4:") && p.Contains("comas"));
        Assert.Contains(problems, p => p.Contains("sección 5 no tiene texto"));
        Assert.Contains(problems, p => p.Contains("sección 6 es demasiado larga"));
        Assert.Contains(problems, p => p.Contains("sección 7 tiene un tipo desconocido"));
        Assert.Contains(problems, p => p.Contains("referencia a una sección que no existe"));
    }

    [Fact]
    public void The_validator_requires_an_arrangement_when_there_are_sections()
    {
        Assert.Contains(SongValidator.ValidateStructure([new("Coro", "chorus", "uno")], []), p => p.Contains("orden está vacío"));
        Assert.Empty(SongValidator.ValidateStructure([], []));
    }

    [Fact]
    public void Section_normalization_canonicalizes_the_label_derives_the_kind_and_cleans_the_body()
    {
        var s = SongValidator.NormalizeSection("  pre  CORO 3 ", "\n\n  uno  \n\n\n\n dos\u200B \n\n");

        Assert.Equal(new ParsedSection("Pre-coro 3", "prechorus", "uno\n\ndos"), s);
    }

    [Theory]
    [InlineData("image", "C:\\fondos\\a.jpg", null, true)]
    [InlineData("video", "C:\\fondos\\a.mp4", "#102030", true)]
    [InlineData("color", null, "#112233", true)]
    [InlineData("color", null, "#11223344", true)]
    [InlineData("image", "relativo\\a.jpg", null, false)]
    [InlineData("image", "", null, false)]
    [InlineData("video", "C:\\a.mp4", "rojo", false)]
    [InlineData("color", null, "#12", false)]
    [InlineData("gif", "C:\\a.gif", null, false)]
    public void Song_backgrounds_are_checked_for_shape(string kind, string? path, string? color, bool valid)
    {
        Assert.Equal(valid, SongValidator.ValidateBackground(new SongBackground(kind, path, color, true)) is null);
    }
}
