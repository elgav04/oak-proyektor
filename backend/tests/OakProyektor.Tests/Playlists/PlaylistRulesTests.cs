using System.Text.Json;
using OakProyektor.Core;
using OakProyektor.Core.Library;
using OakProyektor.Core.Output;
using OakProyektor.Core.Playlists;
using OakProyektor.Ipc;

namespace OakProyektor.Tests.Playlists;

/// <summary>Las reglas puras de las listas de culto (Core): validación, navegación que salta lo que no se proyecta, ajustes y el origen del slide. Datos sintéticos.</summary>
public sealed class PlaylistRulesTests
{
    // ───────────────────────────── fechas y textos ─────────────────────────────

    [Theory]
    [InlineData("2026-11-29", true)]
    [InlineData("2028-02-29", true)]      // bisiesto
    [InlineData("2027-02-29", false)]     // no existe
    [InlineData("2026-13-01", false)]
    [InlineData("2026-11-31", false)]
    [InlineData("26-11-29", false)]
    [InlineData("2026/11/29", false)]
    [InlineData("2026-1-9", false)]
    [InlineData("hoy", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_a_real_day_in_yyyy_mm_dd_is_a_valid_date(string? value, bool expected) => Assert.Equal(expected, PlaylistRules.IsValidDate(value));

    [Fact]
    public void An_empty_date_is_a_template_and_a_malformed_one_is_rejected_not_silently_dropped()
    {
        Assert.Null(PlaylistRules.NormalizeDate(null));
        Assert.Null(PlaylistRules.NormalizeDate("   "));
        Assert.Equal("2026-11-29", PlaylistRules.NormalizeDate(" 2026-11-29 "));
        Assert.Equal(PlaylistErrorCodes.InvalidRequest, Assert.Throws<PlaylistException>(() => PlaylistRules.NormalizeDate("29/11/2026")).Code);
    }

    [Fact]
    public void Names_titles_and_notes_are_trimmed_and_bounded_and_never_cut_silently()
    {
        Assert.Equal("Domingo sintético", PlaylistRules.NormalizeName("  Domingo sintético  "));
        foreach (var bad in new[] { null, "", "   ", new string('a', PlaylistLimits.MaxNameChars + 1), "con\u0001control" })
            Assert.Equal(PlaylistErrorCodes.InvalidRequest, Assert.Throws<PlaylistException>(() => PlaylistRules.NormalizeName(bad)).Code);
        Assert.Equal(PlaylistLimits.MaxNameChars, PlaylistRules.NormalizeName(new string('a', PlaylistLimits.MaxNameChars)).Length);

        Assert.Null(PlaylistRules.NormalizeItemNote("   "));
        Assert.Equal("Lee la hermana sintética", PlaylistRules.NormalizeItemNote(" Lee la hermana sintética "));
        Assert.Throws<PlaylistException>(() => PlaylistRules.NormalizeItemNote(new string('n', PlaylistLimits.MaxItemNoteChars + 1)));
        Assert.Throws<PlaylistException>(() => PlaylistRules.NormalizeNotes(new string('n', PlaylistLimits.MaxNotesChars + 1)));
        Assert.Equal("", PlaylistRules.NormalizeNotes(null));
        Assert.Throws<PlaylistException>(() => PlaylistRules.NormalizeItemTitle("  "));
    }

    [Fact]
    public void The_passage_title_snapshot_is_made_of_numbers_only_never_bible_text()
    {
        Assert.Equal("Libro 43 3:16 · TST", PlaylistRules.PassageTitle(43, 3, 16, null, "TST"));
        Assert.Equal("Libro 43 3:16-18 · TST", PlaylistRules.PassageTitle(43, 3, 16, 18, "TST"));
        Assert.Equal("Libro 43 3:16 · TST", PlaylistRules.PassageTitle(43, 3, 16, 16, "TST"));
    }

    // ───────────────────────────── navegación ─────────────────────────────

    private static PlaylistItemDetail Item(int position, string type, bool missing = false, string? resolution = null) =>
        new(100 + position, position, type.ToLowerInvariant(), $"Elemento {position}", missing, null, null, null, null, null, null, null, false, resolution, null,
            PlaylistNavigation.IsProjectable(type, missing, resolution));

    [Fact]
    public void Only_an_existing_song_or_a_resolved_passage_is_projectable()
    {
        Assert.True(PlaylistNavigation.IsProjectable(PlaylistItemTypes.Song, false, null));
        Assert.False(PlaylistNavigation.IsProjectable(PlaylistItemTypes.Song, true, null));                     // canción borrada
        Assert.True(PlaylistNavigation.IsProjectable(PlaylistItemTypes.Passage, false, PlaylistResolutions.Resolved));
        Assert.False(PlaylistNavigation.IsProjectable(PlaylistItemTypes.Passage, false, PlaylistResolutions.BibleNotInstalled));
        Assert.False(PlaylistNavigation.IsProjectable(PlaylistItemTypes.Passage, false, PlaylistResolutions.PassageNotFound));
        Assert.False(PlaylistNavigation.IsProjectable(PlaylistItemTypes.Note, false, null));
        Assert.False(PlaylistNavigation.IsProjectable(PlaylistItemTypes.Presentation, false, null));
    }

    [Fact]
    public void Next_and_previous_skip_missing_items_notes_and_unresolved_passages_without_removing_them_from_the_list()
    {
        IReadOnlyList<PlaylistItemDetail> items =
        [
            Item(0, PlaylistItemTypes.Song),
            Item(1, PlaylistItemTypes.Song, missing: true),                                         // «Falta: …»
            Item(2, PlaylistItemTypes.Note),                                                         // separador
            Item(3, PlaylistItemTypes.Passage, resolution: PlaylistResolutions.BibleNotInstalled),   // Biblia no instalada
            Item(4, PlaylistItemTypes.Passage, resolution: PlaylistResolutions.Resolved),
            Item(5, PlaylistItemTypes.Song, missing: true),
            Item(6, PlaylistItemTypes.Song),
        ];

        Assert.Equal(4, PlaylistNavigation.Step(items, 0, +1));      // salta faltante, nota y pasaje sin Biblia
        Assert.Equal(6, PlaylistNavigation.Step(items, 4, +1));      // salta el faltante del medio
        Assert.Null(PlaylistNavigation.Step(items, 6, +1));          // no hay más
        Assert.Equal(4, PlaylistNavigation.Step(items, 6, -1));
        Assert.Equal(0, PlaylistNavigation.Step(items, 4, -1));
        Assert.Null(PlaylistNavigation.Step(items, 0, -1));
        Assert.Equal(0, PlaylistNavigation.Step(items, null, +1));   // sin elemento activo: el primero proyectable
        Assert.Equal(6, PlaylistNavigation.Step(items, null, -1));   // y hacia atrás, el último
        Assert.Equal(7, items.Count);                                // la navegación no quita nada
        Assert.Throws<ArgumentOutOfRangeException>(() => PlaylistNavigation.Step(items, 0, 2));
    }

    [Fact]
    public void A_list_with_nothing_projectable_or_empty_has_no_next_item()
    {
        Assert.Null(PlaylistNavigation.Step([], null, +1));
        Assert.Null(PlaylistNavigation.Step([Item(0, PlaylistItemTypes.Note), Item(1, PlaylistItemTypes.Song, missing: true)], null, +1));
    }

    // ───────────────────────────── ajustes ─────────────────────────────

    [Fact]
    public void The_end_of_an_item_stays_by_default_and_an_unknown_value_is_invalid()
    {
        Assert.Equal(PlaylistOnItemEnd.Stay, new PlaylistDisplaySettings().OnItemEnd);
        Assert.Equal(PlaylistOnItemEnd.Stay, new AppSettings().Playlists.OnItemEnd);          // un settings.json sin el bloque usa el valor por defecto
        foreach (var ok in Enum.GetValues<PlaylistOnItemEnd>()) Assert.Null(PlaylistDisplaySettings.Validate(new PlaylistDisplaySettings(ok)));
        Assert.NotNull(PlaylistDisplaySettings.Validate(new PlaylistDisplaySettings((PlaylistOnItemEnd)99)));
    }

    // ───────────────────────────── origen del slide ─────────────────────────────

    [Fact]
    public void A_missing_origin_is_valid_it_means_the_projection_does_not_come_from_a_list()
    {
        Assert.Null(ContentValidator.ValidateOrigin(null));
        Assert.Null(ContentValidator.ValidateSlide(["texto sintético"], TextTheme.Default, 400, null));
    }

    [Theory]
    [InlineData(1L, 1L, "song", 0)]
    [InlineData(7L, 42L, "passage", 3)]
    [InlineData(1L, 1L, "song", SlideOrigin.MaxPosition)]
    public void A_well_formed_origin_is_accepted(long list, long item, string type, int position) =>
        Assert.Null(ContentValidator.ValidateOrigin(new SlideOrigin(list, item, type, position)));

    [Theory]
    [InlineData(0L, 1L, "song", 0)]
    [InlineData(1L, 0L, "song", 0)]
    [InlineData(-1L, 1L, "song", 0)]
    [InlineData(1L, 1L, "note", 0)]                          // una nota no proyecta nada: no puede ser el origen de un slide
    [InlineData(1L, 1L, "Song", 0)]
    [InlineData(1L, 1L, "", 0)]
    [InlineData(1L, 1L, "song", -1)]
    [InlineData(1L, 1L, "song", SlideOrigin.MaxPosition + 1)]
    public void A_malformed_origin_is_rejected_before_it_reaches_the_output(long list, long item, string type, int position)
    {
        Assert.NotNull(ContentValidator.ValidateOrigin(new SlideOrigin(list, item, type, position)));
        Assert.NotNull(ContentValidator.ValidateSlide(["texto sintético"], TextTheme.Default, 400, new SlideOrigin(list, item, type, position)));
    }

    [Fact]
    public void The_origin_serializes_with_only_ids_and_numbers_and_the_state_schema_is_3()
    {
        var slide = new Slide("s1", [new TextBlock("texto sintético de prueba")], TextTheme.Default, new SlideOrigin(3, 9, SlideOrigin.Song, 2));
        var state = OutputState.Initial.WithSlide(slide, 400);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(state, IpcSerializer.Options));
        Assert.Equal(OutputState.CurrentSchemaVersion, doc.RootElement.GetProperty("schemaVersion").GetInt32());   // 3 desde la v15 (overlays = { alert }); 4 desde la v18 (foreground)
        var origin = doc.RootElement.GetProperty("content").GetProperty("slide").GetProperty("origin");
        Assert.Equal(["playlistId", "itemId", "itemType", "position"], origin.EnumerateObject().Select(p => p.Name));
        Assert.Equal(JsonValueKind.Number, origin.GetProperty("playlistId").ValueKind);
        Assert.Equal(JsonValueKind.Number, origin.GetProperty("itemId").ValueKind);
        Assert.Equal("song", origin.GetProperty("itemType").GetString());      // el único texto del origen es el tipo, de un conjunto cerrado
        Assert.Equal(2, origin.GetProperty("position").GetInt32());

        // Sin lista: el origen viaja como null (la proyección suelta de la Biblia o de una canción).
        using var plain = JsonDocument.Parse(JsonSerializer.Serialize(OutputState.Initial.WithSlide(new Slide("s2", [new TextBlock("x")], TextTheme.Default), 400), IpcSerializer.Options));
        Assert.Equal(JsonValueKind.Null, plain.RootElement.GetProperty("content").GetProperty("slide").GetProperty("origin").ValueKind);
    }
}
