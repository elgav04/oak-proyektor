using OakProyektor.Core.Songs;

namespace OakProyektor.Tests.Songs;

/// <summary>Reparación de las secciones convertidas por la migración 9 (reglas del código: CanonicalLabel, Key, SongValidator). Letra SINTÉTICA.</summary>
public sealed class SongLabelRepairTests
{
    private static RepairSectionInput S(long id, int position, string label, string text = "texto sintetico") => new(id, position, label, text);

    private static IReadOnlyList<string> Labels(IEnumerable<RepairedSection> repaired) => [.. repaired.Where(r => !r.Remove).Select(r => r.Label)];

    /// <summary>Lo que el guardado exige: ninguna secuencia reparada puede dar un problema de estructura.</summary>
    private static void AssertSavable(IReadOnlyList<RepairedSection> repaired)
    {
        var sections = repaired.Where(r => !r.Remove).Select(r => new ParsedSection(r.Label, r.Kind, r.Text)).ToList();
        Assert.Empty(SongValidator.ValidateStructure(sections, [.. Enumerable.Range(0, sections.Count)]));
        Assert.All(sections, s => Assert.Equal(s.Label, SectionAliases.CanonicalLabel(s.Label)));   // la forma canónica es estable: al guardar no cambia nada
    }

    [Fact]
    public void Coro_2_Coro_Coro_after_the_conversion_get_distinct_canonical_labels()
    {
        // Lo que dejó la migración 9 para las diapositivas «Coro 2», «Coro», «Coro»: la tercera era «Coro (2)», que al guardar se lee «Coro 2» y chocaría con la primera.
        var repaired = SongLabelRepair.Repair([S(1, 0, "Coro 2"), S(2, 1, "Coro"), S(3, 2, "Coro (2)")]);

        Assert.Equal(["Coro 2", "Coro", "Coro 3"], Labels(repaired));
        AssertSavable(repaired);
    }

    [Fact]
    public void A_repeated_known_name_never_changes_meaning_Verso_1_2_is_not_Verso_12()
    {
        var repaired = SongLabelRepair.Repair([S(1, 0, "Verso 1"), S(2, 1, "Verso 1 (2)"), S(3, 2, "Verso 1 (3)")]);

        Assert.Equal(["Verso 1", "Verso 1 bis", "Verso 1 bis 3"], Labels(repaired));   // los mismos nombres que da el intérprete de texto
        Assert.DoesNotContain("Verso 12", Labels(repaired));
        AssertSavable(repaired);
    }

    [Fact]
    public void Two_plain_repeated_labels_Verso_1_Verso_1()
    {
        var repaired = SongLabelRepair.Repair([S(1, 0, "Verso 1"), S(2, 1, "Verso 1 (2)")]);

        Assert.Equal(["Verso 1", "Verso 1 bis"], Labels(repaired));
    }

    [Fact]
    public void A_name_that_is_not_a_known_section_keeps_its_numbered_form()
    {
        var repaired = SongLabelRepair.Repair([S(1, 0, "Parte A"), S(2, 1, "Parte A (2)")]);

        Assert.Equal(["Parte A", "Parte A (2)"], Labels(repaired));   // «Parte A (2)» ya es estable y distinta
        AssertSavable(repaired);
    }

    [Fact]
    public void A_slide_without_text_is_removed_and_the_others_keep_their_ids()
    {
        var repaired = SongLabelRepair.Repair([S(1, 0, "Verso 1"), S(2, 1, "Coro", "  \n \t "), S(3, 2, "Verso 2")]);

        Assert.Equal([false, true, false], repaired.Select(r => r.Remove));
        Assert.Equal([1L, 3L], repaired.Where(r => !r.Remove).Select(r => r.Id));
    }

    [Fact]
    public void Lines_shaped_like_a_label_or_an_order_line_are_wrapped_not_lost()
    {
        var repaired = SongLabelRepair.Repair([S(1, 0, "Verso 1", "antes\n[Coro]\ndespues\nOrden: V1 C\n[x] : ")]);

        var text = repaired.Single().Text;
        Assert.Equal("antes\n([Coro])\ndespues\n(Orden: V1 C)\n([x] :)", text);   // todo el contenido sigue ahí, solo entre paréntesis
        AssertSavable(repaired);
    }

    [Fact]
    public void Semicolons_brackets_commas_and_control_characters_are_removed_from_labels()
    {
        var repaired = SongLabelRepair.Repair([S(1, 0, "A;B"), S(2, 1, "[Raro], x"), S(3, 2, "Tab\tulado\u0001")]);

        Assert.All(Labels(repaired), l => Assert.Null(SongValidator.LabelProblem(l)));
        Assert.Equal(["AB", "Raro x", "Tab ulado"], Labels(repaired));
        AssertSavable(repaired);
    }

    [Fact]
    public void A_label_longer_than_sixty_characters_is_shortened_and_still_unique()
    {
        var longName = new string('x', 80);
        var repaired = SongLabelRepair.Repair([S(1, 0, longName), S(2, 1, longName + "y"), S(3, 2, longName + "z")]);

        Assert.All(Labels(repaired), l => Assert.InRange(l.Length, 1, SongLimits.MaxLabelChars));
        Assert.Equal(3, Labels(repaired).Select(SectionAliases.Key).Distinct().Count());
        AssertSavable(repaired);
    }

    [Fact]
    public void The_type_follows_the_name_so_Coro_is_not_other()
    {
        var repaired = SongLabelRepair.Repair([S(1, 0, "Verso 1"), S(2, 1, "Coro"), S(3, 2, "Puente"), S(4, 3, "Final"), S(5, 4, "Sección 5")]);

        Assert.Equal(["verse", "chorus", "bridge", "ending", "other"], repaired.Select(r => r.Kind));
    }

    [Fact]
    public void Labels_without_letters_or_digits_get_a_positional_name()
    {
        var repaired = SongLabelRepair.Repair([S(1, 0, "!!!"), S(2, 1, "  "), S(3, 2, "")]);

        Assert.Equal(["Sección 1", "Sección 2", "Sección 3"], Labels(repaired));
        AssertSavable(repaired);
    }

    [Fact]
    public void Already_valid_sections_are_left_exactly_as_they_are_and_repairing_twice_changes_nothing()
    {
        var valid = new[] { S(1, 0, "Verso 1", "uno\ndos"), S(2, 1, "Coro", "coro\n\ncorte manual"), S(3, 2, "Verso 2", "tres"), S(4, 3, "Parte A (2)", "cuatro") };

        var once = SongLabelRepair.Repair(valid);

        Assert.Equal(valid.Select(v => (v.Id, v.Label, v.Text)), once.Select(r => (r.Id, r.Label, r.Text)));
        var twice = SongLabelRepair.Repair([.. once.Select((r, i) => S(r.Id, i, r.Label, r.Text))]);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void Any_mix_of_hostile_labels_ends_up_savable_and_never_throws()
    {
        var random = new Random(20261125);
        var pool = new[] { "Coro", "Coro 2", "Coro (2)", "Verso 1", "Verso 1 (2)", "verso1", "Verso-1", "A;B", "[x]", "", "  ", "Coro bis", "Parte A", "Parte A (2)", new string('q', 70), "Pre-coro (3)", "Sección 3", "1", "(2)" };
        for (var round = 0; round < 500; round++)
        {
            var count = random.Next(1, 25);
            var input = Enumerable.Range(0, count).Select(i => S(i + 1, i, pool[random.Next(pool.Length)], random.Next(6) == 0 ? "" : "linea " + i)).ToList();

            var repaired = SongLabelRepair.Repair(input);

            AssertSavable(repaired);
        }
    }
}