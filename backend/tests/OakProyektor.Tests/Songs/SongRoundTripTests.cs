using System.Text;
using OakProyektor.Core.Songs;

namespace OakProyektor.Tests.Songs;

/// <summary>
/// El texto único con etiquetas es la inversa del parser: secciones + orden → texto → secciones + orden conserva TODO (etiquetas, tipos, cuerpos con sus cortes manuales,
/// orden con repeticiones y secciones sin usar). Pruebas de propiedad con casos aleatorios (semilla fija: reproducibles).
/// </summary>
public class SongRoundTripTests
{
    private const int Cases = 1500;

    [Fact]
    public void Model_to_text_to_model_keeps_everything_for_random_songs()
    {
        for (var seed = 0; seed < Cases; seed++)
        {
            var (sections, arrangement) = SyntheticSong.RandomModel(new Random(seed));
            Assert.Empty(SongValidator.ValidateStructure(sections, arrangement));   // el generador produce modelos válidos

            var text = SongTextFormatter.Format(sections, arrangement);
            var parsed = SongTextParser.Parse(text);

            Assert.True(parsed.Errors.Count == 0, $"semilla {seed}: {string.Join(" | ", parsed.Errors)}\n{text}");
            Assert.True(sections.SequenceEqual(parsed.Sections), $"semilla {seed}: las secciones cambiaron\n{text}");
            Assert.True(arrangement.SequenceEqual(parsed.Arrangement), $"semilla {seed}: el orden cambió ({string.Join(',', arrangement)} → {string.Join(',', parsed.Arrangement)})\n{text}");
            Assert.Equal(SongTextFormat.Brackets, parsed.Format);
        }
    }

    [Fact]
    public void Text_to_model_to_text_is_stable_for_random_texts_so_editing_never_drifts()
    {
        string[] pieces = ["[Coro]", "[Verso 1]", "[Verso 2]", "[Puente]", "[Parte A]", "linea lumari tenoko", "otra linea sintetica", "\n", "\n\n", "Orden: V1 C V2 C", "Orden: Coro, Puente"];
        for (var seed = 0; seed < Cases; seed++)
        {
            var random = new Random(10_000 + seed);
            var sb = new StringBuilder();
            for (var i = 0; i < random.Next(1, 30); i++) sb.Append(pieces[random.Next(pieces.Length)]).Append(random.Next(3) == 0 ? "\n\n" : "\n");
            var first = SongTextParser.Parse(sb.ToString());
            if (first.Errors.Count > 0) continue;   // un texto con errores no se guarda: no hay modelo que estabilizar

            var second = SongTextParser.Parse(SongTextFormatter.Format(first.Sections, first.Arrangement));

            Assert.True(first.Sections.SequenceEqual(second.Sections) && first.Arrangement.SequenceEqual(second.Arrangement),
                $"semilla {seed}: no es estable\n--- original ---\n{sb}\n--- formateado ---\n{SongTextFormatter.Format(first.Sections, first.Arrangement)}");
        }
    }

    [Fact]
    public void A_natural_order_needs_no_order_line_and_a_different_one_gets_it()
    {
        var sections = new List<ParsedSection> { new("Verso 1", "verse", "uno"), new("Coro", "chorus", "dos") };

        Assert.Equal("[Verso 1]\nuno\n\n[Coro]\ndos", SongTextFormatter.Format(sections, [0, 1]));
        Assert.Equal("[Verso 1]\nuno\n\n[Coro]\ndos\n\nOrden: Verso 1, Coro, Verso 1, Coro", SongTextFormatter.Format(sections, [0, 1, 0, 1]));
        Assert.Equal("[Verso 1]\nuno\n\n[Coro]\ndos\n\nOrden: Coro", SongTextFormatter.Format(sections, [1]));   // una sección sin usar también se conserva
        Assert.Equal("", SongTextFormatter.Format([], []));
    }

    [Fact]
    public void A_manual_cut_survives_the_round_trip_exactly()
    {
        var sections = new List<ParsedSection> { new("Verso 1", "verse", "uno\ndos\n\ntres\n\ncuatro") };

        var parsed = SongTextParser.Parse(SongTextFormatter.Format(sections, [0]));

        Assert.Equal("uno\ndos\n\ntres\n\ncuatro", parsed.Sections[0].Text);
    }

    [Fact]
    public void An_explicit_empty_order_round_trips_as_an_error_the_validator_reports()
    {
        var sections = new List<ParsedSection> { new("Verso 1", "verse", "uno") };

        var parsed = SongTextParser.Parse(SongTextFormatter.Format(sections, []));

        Assert.Empty(parsed.Arrangement);
        Assert.NotEmpty(parsed.Errors);
    }

    [Fact]
    public void Formatting_an_invalid_order_does_not_throw()
    {
        var sections = new List<ParsedSection> { new("Verso 1", "verse", "uno") };

        var text = SongTextFormatter.Format(sections, [0, 5, -1, 0]);

        Assert.Contains("Orden: Verso 1, Verso 1", text);
    }

    [Fact]
    public void Labels_are_stable_under_canonicalization()
    {
        for (var seed = 0; seed < 200; seed++)
        {
            var (sections, _) = SyntheticSong.RandomModel(new Random(seed));
            foreach (var section in sections)
                Assert.Equal(section.Label, SectionAliases.CanonicalLabel(section.Label));
        }
    }
}
