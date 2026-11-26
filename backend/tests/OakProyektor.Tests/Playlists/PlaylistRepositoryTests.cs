using System.Diagnostics;
using Dapper;
using Microsoft.Data.Sqlite;
using OakProyektor.Core.Library;
using OakProyektor.Core.Playlists;
using OakProyektor.Core.Songs;
using OakProyektor.Data;
using OakProyektor.Data.Playlists;
using OakProyektor.Data.Songs;
using OakProyektor.Tests.Data;
using OakProyektor.Tests.Ipc;
using OakProyektor.Tests.Songs;

namespace OakProyektor.Tests.Playlists;

/// <summary>
/// El repositorio de listas de culto sobre una BD real (SQLite temporal). Todo con datos sintéticos. Lo que más importa: una vista desactualizada no puede mover ni borrar elementos,
/// los faltantes nunca se pierden y la navegación los salta, la misma canción dos veces son dos elementos distintos y los registros llevan solo ids y conteos.
/// </summary>
public sealed class PlaylistRepositoryTests : IAsyncLifetime
{
    private readonly TestDatabase _db = new();
    private readonly ListLogger<PlaylistRepository> _log = new();
    private PlaylistRepository _repo = null!;
    private SongRepository _songs = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _repo = new PlaylistRepository(_db.Connections, new AlwaysReady(), _log, new PlaylistRepositoryOptions { WriteTimeoutSeconds = 1 });
        _songs = new SongRepository(_db.Connections, new AlwaysReady());
    }

    public Task DisposeAsync() { _db.Dispose(); return Task.CompletedTask; }

    private async Task<long> NewSongAsync(string title) =>
        (await _songs.SaveAsync(new SongSaveRequest(null, null, title, null, null, null, null, null, [new(null, "Verso 1", null, SyntheticSong.Text(1))], [0]), default)).SongId;

    private static PlaylistItemInput Song(long id) => new(PlaylistItemTypes.Song, id, null, null, null, null, null, null, null, false);
    private static PlaylistItemInput Passage(int book = 43, int chapter = 3, int start = 16, int? end = 17, string bible = "TST", string? title = null) =>
        new(PlaylistItemTypes.Passage, null, book, chapter, start, end, bible, title, null, false);
    private static PlaylistItemInput Note(string title, string? note = null, bool clear = false) => new(PlaylistItemTypes.Note, null, null, null, null, null, null, title, note, clear);

    private async Task<long> NewListAsync(string name = "Domingo sintético", string? date = "2026-11-29") =>
        (await _repo.CreateAsync(name, date, null, default)).PlaylistId;

    private async Task<PlaylistDetail> GetAsync(long id, string? defaultBible = null) => (await _repo.GetDetailAsync(id, defaultBible, default))!;

    private static async Task FailsAsync(Func<Task> action, string code)
    {
        var ex = await Assert.ThrowsAsync<PlaylistException>(action);
        Assert.Equal(code, ex.Code);
    }

    private async Task<long> CountAsync(string table, string where = "1 = 1")
    {
        await using var c = await _db.OpenAsync();
        return await c.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {table} WHERE {where}");
    }

    // ───────────────────────────── crear y editar la lista ─────────────────────────────

    [Fact]
    public async Task A_new_list_starts_at_revision_1_with_its_date_and_an_update_needs_the_current_revision()
    {
        var created = await _repo.CreateAsync("  Domingo sintético ", "2026-11-29", "notas sintéticas", default);
        Assert.Equal(1, created.Revision);
        var list = await GetAsync(created.PlaylistId);
        Assert.Equal(("Domingo sintético", "2026-11-29", "notas sintéticas", 1), (list.Name, list.ServiceDate, list.Notes, list.Revision));

        var updated = await _repo.UpdateAsync(created.PlaylistId, "Domingo 29", null, "", 1, default);          // sin fecha = plantilla
        Assert.Equal(2, updated.Revision);
        list = await GetAsync(created.PlaylistId);
        Assert.Equal(("Domingo 29", (string?)null, "", 2), (list.Name, list.ServiceDate, list.Notes, list.Revision));

        await FailsAsync(() => _repo.UpdateAsync(created.PlaylistId, "Con revisión vieja", null, null, 1, default), PlaylistErrorCodes.Conflict);
        Assert.Equal("Domingo 29", (await GetAsync(created.PlaylistId)).Name);                                    // nada cambió
        await FailsAsync(() => _repo.UpdateAsync(9999, "x", null, null, 1, default), PlaylistErrorCodes.NotFound);
    }

    [Fact]
    public async Task An_invalid_name_or_date_is_rejected_and_nothing_is_created()
    {
        await FailsAsync(() => _repo.CreateAsync("   ", null, null, default), PlaylistErrorCodes.InvalidRequest);
        await FailsAsync(() => _repo.CreateAsync("Con fecha mala", "2026-02-30", null, default), PlaylistErrorCodes.InvalidRequest);
        await FailsAsync(() => _repo.CreateAsync("Con fecha mala", "30/02/2026", null, default), PlaylistErrorCodes.InvalidRequest);
        Assert.Equal(0, await CountAsync("Playlists"));
        var id = await NewListAsync();
        await FailsAsync(() => _repo.UpdateAsync(id, "ok", "mañana", null, 1, default), PlaylistErrorCodes.InvalidRequest);
        Assert.Equal(1, (await GetAsync(id)).Revision);
    }

    [Fact]
    public async Task The_listing_puts_dated_lists_first_newest_on_top_then_the_templates_with_counts()
    {
        var old = await NewListAsync("Domingo viejo", "2026-11-01");
        var template = await NewListAsync("Plantilla", null);
        var recent = await NewListAsync("Domingo nuevo", "2026-11-29");
        await _repo.AddItemAsync(recent, Note("a"), null, default);
        await _repo.AddItemAsync(recent, Song(await NewSongAsync("Canción sintética")), null, default);

        var list = await _repo.ListAsync(default);

        Assert.False(list.Truncated);
        Assert.Equal([recent, old, template], list.Playlists.Select(p => p.Id));
        var summary = list.Playlists[0];
        Assert.Equal((2, 0, 3), (summary.ItemCount, summary.MissingCount, summary.Revision));   // 1 (crear) + 2 añadidos
    }

    // ───────────────────────────── añadir, mover, quitar ─────────────────────────────

    [Fact]
    public async Task Items_are_added_at_the_end_or_at_a_position_and_positions_stay_contiguous()
    {
        var id = await NewListAsync();
        var song = await NewSongAsync("Canción sintética A");
        var a = (await _repo.AddItemAsync(id, Song(song), null, default)).ItemId!.Value;
        var b = (await _repo.AddItemAsync(id, Passage(), null, default)).ItemId!.Value;
        var c = (await _repo.AddItemAsync(id, Note("Ofrendas", "Pasan los diáconos sintéticos"), 0, default)).ItemId!.Value;     // al principio
        var d = (await _repo.AddItemAsync(id, Note("Anuncios"), 2, default)).ItemId!.Value;                                        // en medio

        var list = await GetAsync(id);
        Assert.Equal([c, a, d, b], list.Items.Select(i => i.Id));
        Assert.Equal([0, 1, 2, 3], list.Items.Select(i => i.Position));
        Assert.Equal(5, list.Revision);   // crear + 4 elementos
        Assert.Equal(["note", "song", "note", "passage"], list.Items.Select(i => i.ItemType));
        Assert.Equal("Libro 43 3:16-17 · TST", list.Items[3].Title);                           // título instantánea de un pasaje: solo números
        Assert.Equal(("Ofrendas", "Pasan los diáconos sintéticos", false), (list.Items[0].Title, list.Items[0].Note, list.Items[0].ClearOnActivate));
        await using var conn = await _db.OpenAsync();
        Assert.Equal([0L, 1, 2, 3], (await conn.QueryAsync<long>("SELECT SequenceIndex FROM PlaylistItems WHERE PlaylistId = @id ORDER BY SequenceIndex", new { id })));
    }

    [Fact]
    public async Task The_same_song_twice_is_two_distinct_items_and_each_one_is_moved_and_removed_by_its_own_id()
    {
        var id = await NewListAsync();
        var song = await NewSongAsync("Canción sintética repetida");
        var first = (await _repo.AddItemAsync(id, Song(song), null, default)).ItemId!.Value;
        var middle = (await _repo.AddItemAsync(id, Note("Oración"), null, default)).ItemId!.Value;
        var second = (await _repo.AddItemAsync(id, Song(song), null, default)).ItemId!.Value;
        Assert.NotEqual(first, second);

        var list = await GetAsync(id);
        Assert.Equal([first, middle, second], list.Items.Select(i => i.Id));
        Assert.All(list.Items.Where(i => i.ItemType == "song"), i => Assert.Equal((song, "Canción sintética repetida", true), (i.SongId, i.Title, i.Projectable)));

        // Mover el SEGUNDO al principio: el primero no se confunde con él aunque sean la misma canción.
        var moved = await _repo.MoveItemAsync(id, second, 0, list.Revision, default);
        Assert.Equal([second, first, middle], (await GetAsync(id)).Items.Select(i => i.Id));

        // Quitar el PRIMERO (por su id): queda el segundo.
        await _repo.RemoveItemAsync(id, first, moved.Revision, default);
        var after = await GetAsync(id);
        Assert.Equal([second, middle], after.Items.Select(i => i.Id));
        Assert.Equal([0, 1], after.Items.Select(i => i.Position));
        Assert.Equal(1, (await _songs.GetAsync(song, default))!.UsedInServices);                  // sigue en 1 lista (una vez por lista, aunque estuviera repetida)
    }

    [Fact]
    public async Task Moving_to_the_same_place_changes_nothing_and_does_not_bump_the_revision_and_a_bad_position_is_rejected()
    {
        var id = await NewListAsync();
        var a = (await _repo.AddItemAsync(id, Note("A"), null, default)).ItemId!.Value;
        await _repo.AddItemAsync(id, Note("B"), null, default);
        var revision = (await GetAsync(id)).Revision;

        Assert.Equal(revision, (await _repo.MoveItemAsync(id, a, 0, revision, default)).Revision);
        Assert.Equal(revision, (await GetAsync(id)).Revision);
        await FailsAsync(() => _repo.MoveItemAsync(id, a, 2, revision, default), PlaylistErrorCodes.InvalidRequest);
        await FailsAsync(() => _repo.MoveItemAsync(id, a, -1, revision, default), PlaylistErrorCodes.InvalidRequest);
        await FailsAsync(() => _repo.MoveItemAsync(id, 99999, 0, revision, default), PlaylistErrorCodes.ItemNotFound);
        await FailsAsync(() => _repo.AddItemAsync(id, Note("C"), 5, default), PlaylistErrorCodes.InvalidRequest);
    }

    [Fact]
    public async Task An_item_of_another_list_cannot_be_moved_removed_or_edited_through_this_one()
    {
        var one = await NewListAsync("Una");
        var two = await NewListAsync("Dos");
        var mine = (await _repo.AddItemAsync(one, Note("Mía"), null, default)).ItemId!.Value;
        var theirs = (await _repo.AddItemAsync(two, Note("Ajena"), null, default)).ItemId!.Value;
        var revision = (await GetAsync(one)).Revision;

        await FailsAsync(() => _repo.RemoveItemAsync(one, theirs, revision, default), PlaylistErrorCodes.ItemNotFound);
        await FailsAsync(() => _repo.MoveItemAsync(one, theirs, 0, revision, default), PlaylistErrorCodes.ItemNotFound);
        await FailsAsync(() => _repo.UpdateItemAsync(one, theirs, new PlaylistItemUpdate("x", null, null, null), revision, default), PlaylistErrorCodes.ItemNotFound);
        Assert.Single((await GetAsync(two)).Items);
        Assert.Equal([mine], (await GetAsync(one)).Items.Select(i => i.Id));
    }

    // ───────────────────────────── vista desactualizada ─────────────────────────────

    [Fact]
    public async Task A_stale_view_cannot_remove_move_or_edit_anything_and_adding_does_not_need_a_revision()
    {
        var id = await NewListAsync();
        var a = (await _repo.AddItemAsync(id, Note("A"), null, default)).ItemId!.Value;
        var b = (await _repo.AddItemAsync(id, Note("B"), null, default)).ItemId!.Value;
        var staleRevision = (await GetAsync(id)).Revision;                  // lo que leyó la vista A

        await _repo.AddItemAsync(id, Note("C"), null, default);             // otra ventana (o la pestaña de Biblia) añade un elemento: la revisión sube
        var before = await GetAsync(id);
        Assert.True(before.Revision > staleRevision);

        await FailsAsync(() => _repo.RemoveItemAsync(id, a, staleRevision, default), PlaylistErrorCodes.Conflict);
        await FailsAsync(() => _repo.MoveItemAsync(id, a, 1, staleRevision, default), PlaylistErrorCodes.Conflict);
        await FailsAsync(() => _repo.UpdateItemAsync(id, b, new PlaylistItemUpdate("Cambiada", null, null, null), staleRevision, default), PlaylistErrorCodes.Conflict);
        await FailsAsync(() => _repo.UpdateAsync(id, "Otro nombre", null, null, staleRevision, default), PlaylistErrorCodes.Conflict);

        var after = await GetAsync(id);
        Assert.Equal(before.Items.Select(i => (i.Id, i.Title, i.Position)), after.Items.Select(i => (i.Id, i.Title, i.Position)));    // NADA cambió
        Assert.Equal((before.Name, before.Revision), (after.Name, after.Revision));

        // Con la revisión al día sí se puede.
        await _repo.RemoveItemAsync(id, a, after.Revision, default);
        Assert.Equal(2, (await GetAsync(id)).Items.Count);
        // Añadir nunca destruye nada: funciona aunque quien añade no conozca la revisión.
        await _repo.AddItemAsync(id, Note("D"), null, default);
        Assert.Equal(3, (await GetAsync(id)).Items.Count);
    }

    [Fact]
    public async Task Two_stale_removals_with_the_same_revision_only_the_first_one_wins()
    {
        var id = await NewListAsync();
        var a = (await _repo.AddItemAsync(id, Note("A"), null, default)).ItemId!.Value;
        var b = (await _repo.AddItemAsync(id, Note("B"), null, default)).ItemId!.Value;
        var revision = (await GetAsync(id)).Revision;

        await Task.WhenAll(
            Task.Run(async () => { try { await _repo.RemoveItemAsync(id, a, revision, default); } catch (PlaylistException) { } }),
            Task.Run(async () => { try { await _repo.RemoveItemAsync(id, b, revision, default); } catch (PlaylistException) { } }));

        Assert.Single((await GetAsync(id)).Items);   // la segunda ya traía una revisión caducada (o esperó el bloqueo y recibió el conflicto)
    }

    // ───────────────────────────── límites y formas inválidas ─────────────────────────────

    [Fact]
    public async Task A_list_holds_at_most_100_items_and_the_extra_one_is_rejected_without_changing_anything()
    {
        var id = await NewListAsync();
        for (var i = 0; i < PlaylistLimits.MaxItems; i++) await _repo.AddItemAsync(id, Note($"Nota {i}"), null, default);
        var revision = (await GetAsync(id)).Revision;

        await FailsAsync(() => _repo.AddItemAsync(id, Note("La 101"), null, default), PlaylistErrorCodes.Full);

        Assert.Equal((PlaylistLimits.MaxItems, revision), ((await GetAsync(id)).Items.Count, (await GetAsync(id)).Revision));
    }

    [Fact]
    public async Task Invalid_items_are_rejected_before_touching_the_list()
    {
        var id = await NewListAsync();
        var song = await NewSongAsync("Canción sintética");
        var revision = (await GetAsync(id)).Revision;

        await FailsAsync(() => _repo.AddItemAsync(id, Song(99999), null, default), PlaylistErrorCodes.InvalidRequest);                       // canción inexistente: no nace «faltante»
        await FailsAsync(() => _repo.AddItemAsync(id, new PlaylistItemInput(PlaylistItemTypes.Song, null, null, null, null, null, null, null, null, false), null, default), PlaylistErrorCodes.InvalidRequest);
        await FailsAsync(() => _repo.AddItemAsync(id, Song(song) with { Note = "no va aquí" }, null, default), PlaylistErrorCodes.InvalidRequest);
        await FailsAsync(() => _repo.AddItemAsync(id, Passage(book: 0), null, default), PlaylistErrorCodes.InvalidRequest);
        await FailsAsync(() => _repo.AddItemAsync(id, Passage(book: 67), null, default), PlaylistErrorCodes.InvalidRequest);
        await FailsAsync(() => _repo.AddItemAsync(id, Passage(chapter: 0), null, default), PlaylistErrorCodes.InvalidRequest);
        await FailsAsync(() => _repo.AddItemAsync(id, Passage(start: 0), null, default), PlaylistErrorCodes.InvalidRequest);
        await FailsAsync(() => _repo.AddItemAsync(id, Passage(start: 5, end: 4), null, default), PlaylistErrorCodes.InvalidRequest);
        await FailsAsync(() => _repo.AddItemAsync(id, Passage(bible: "  "), null, default), PlaylistErrorCodes.InvalidRequest);
        await FailsAsync(() => _repo.AddItemAsync(id, Note("  "), null, default), PlaylistErrorCodes.InvalidRequest);
        await FailsAsync(() => _repo.AddItemAsync(id, Note("ok", new string('n', PlaylistLimits.MaxItemNoteChars + 1)), null, default), PlaylistErrorCodes.InvalidRequest);
        await FailsAsync(() => _repo.AddItemAsync(id, new PlaylistItemInput("PRESENTATION", null, null, null, null, null, null, "x", null, false), null, default), PlaylistErrorCodes.InvalidRequest);
        await FailsAsync(() => _repo.AddItemAsync(9999, Note("x"), null, default), PlaylistErrorCodes.NotFound);

        var list = await GetAsync(id);
        Assert.Empty(list.Items);
        Assert.Equal(revision, list.Revision);
    }

    [Fact]
    public async Task Only_notes_and_the_preferred_bible_of_a_passage_can_be_edited_and_a_note_can_clear_its_text()
    {
        var id = await NewListAsync();
        var song = (await _repo.AddItemAsync(id, Song(await NewSongAsync("Canción sintética")), null, default)).ItemId!.Value;
        var passage = (await _repo.AddItemAsync(id, Passage(), null, default)).ItemId!.Value;
        var note = (await _repo.AddItemAsync(id, Note("Ofrendas", "texto"), null, default)).ItemId!.Value;
        var rev = (await GetAsync(id)).Revision;

        var r1 = await _repo.UpdateItemAsync(id, note, new PlaylistItemUpdate("Ofrenda", "", true, null), rev, default);
        var edited = (await GetAsync(id)).Items.Single(i => i.Id == note);
        Assert.Equal(("Ofrenda", (string?)null, true), (edited.Title, edited.Note, edited.ClearOnActivate));     // «Note» vacío la borra; ClearOnActivate se enciende

        var r2 = await _repo.UpdateItemAsync(id, passage, new PlaylistItemUpdate(null, null, null, " OTRA "), r1.Revision, default);
        Assert.Equal("OTRA", (await GetAsync(id)).Items.Single(i => i.Id == passage).BibleAbbreviation);

        await FailsAsync(() => _repo.UpdateItemAsync(id, song, new PlaylistItemUpdate("x", null, null, null), r2.Revision, default), PlaylistErrorCodes.InvalidRequest);          // una canción no se edita
        await FailsAsync(() => _repo.UpdateItemAsync(id, passage, new PlaylistItemUpdate("x", null, null, null), r2.Revision, default), PlaylistErrorCodes.InvalidRequest);
        await FailsAsync(() => _repo.UpdateItemAsync(id, note, new PlaylistItemUpdate(null, null, null, "TST"), r2.Revision, default), PlaylistErrorCodes.InvalidRequest);
        await FailsAsync(() => _repo.UpdateItemAsync(id, note, new PlaylistItemUpdate("  ", null, null, null), r2.Revision, default), PlaylistErrorCodes.InvalidRequest);
        Assert.Equal(r2.Revision, (await GetAsync(id)).Revision);
    }

    // ───────────────────────────── faltantes ─────────────────────────────

    [Fact]
    public async Task A_deleted_song_stays_in_the_list_as_missing_with_its_title_and_next_skips_it()
    {
        var id = await NewListAsync();
        var kept = await NewSongAsync("Canción sintética que se queda");
        var doomed = await NewSongAsync("Canción sintética que se borra");
        var first = (await _repo.AddItemAsync(id, Song(kept), null, default)).ItemId!.Value;
        var missing = (await _repo.AddItemAsync(id, Song(doomed), null, default)).ItemId!.Value;
        var note = (await _repo.AddItemAsync(id, Note("Anuncios"), null, default)).ItemId!.Value;
        var last = (await _repo.AddItemAsync(id, Song(kept), null, default)).ItemId!.Value;

        var deleted = await _songs.DeleteAsync(doomed, default);
        Assert.Equal(1, deleted.UsedInServices);
        Assert.Equal(id, Assert.Single(deleted.Lists).Id);

        var list = await GetAsync(id);
        Assert.Equal([first, missing, note, last], list.Items.Select(i => i.Id));                    // NINGÚN elemento se pierde
        var gone = list.Items[1];
        Assert.Equal(("Canción sintética que se borra", true, null, false), (gone.Title, gone.IsMissing, gone.SongId, gone.Projectable));   // título conservado: «Falta: …»
        Assert.Equal(1, list.MissingCount);
        Assert.Equal([true, false, false, true], list.Items.Select(i => i.Projectable));
        Assert.Equal(1, Assert.Single((await _repo.ListAsync(default)).Playlists).MissingCount);

        // «Siguiente» desde la primera canción salta el faltante y la nota; «anterior» desde la última, lo mismo.
        Assert.Equal(3, PlaylistNavigation.Step(list.Items, 0, +1));
        Assert.Equal(0, PlaylistNavigation.Step(list.Items, 3, -1));

        // El faltante se puede quitar (no queda atascado en la lista) y el resto sigue.
        await _repo.RemoveItemAsync(id, missing, list.Revision, default);
        Assert.Equal([first, note, last], (await GetAsync(id)).Items.Select(i => i.Id));
    }

    [Fact]
    public async Task A_passage_whose_bible_is_not_installed_is_kept_unresolved_and_the_default_bible_is_offered_never_applied()
    {
        await using (var c = await _db.OpenAsync())
            await TestDatabase.InsertVerseAsync(c, "texto sintético del versículo", "DEF");      // Biblia «DEF» con un versículo del libro 1 capítulo 1
        var id = await NewListAsync();
        var absent = (await _repo.AddItemAsync(id, Passage(book: 1, chapter: 1, start: 1, end: null, bible: "NOEXISTE"), null, default)).ItemId!.Value;
        var installed = (await _repo.AddItemAsync(id, Passage(book: 1, chapter: 1, start: 1, end: null, bible: "DEF"), null, default)).ItemId!.Value;
        var notThere = (await _repo.AddItemAsync(id, Passage(book: 1, chapter: 2, start: 1, end: null, bible: "DEF"), null, default)).ItemId!.Value;   // capítulo que la Biblia no tiene

        var list = await GetAsync(id, defaultBible: "DEF");

        var byId = list.Items.ToDictionary(i => i.Id);
        Assert.Equal((PlaylistResolutions.BibleNotInstalled, "DEF", false), (byId[absent].Resolution, byId[absent].OfferedBible, byId[absent].Projectable));   // se OFRECE la por defecto…
        Assert.Equal("NOEXISTE", byId[absent].BibleAbbreviation);                                                                                             // …pero el elemento conserva SU Biblia
        Assert.Equal((PlaylistResolutions.Resolved, (string?)null, true), (byId[installed].Resolution, byId[installed].OfferedBible, byId[installed].Projectable));
        Assert.Equal((PlaylistResolutions.PassageNotFound, false), (byId[notThere].Resolution, byId[notThere].Projectable));
        Assert.All(list.Items, i => Assert.False(i.IsMissing));                                                                                                // un pasaje nunca es «faltante»
        Assert.Equal(0, list.MissingCount);

        Assert.Null((await GetAsync(id)).Items.First(i => i.Id == absent).OfferedBible);                                                                       // sin Biblia por defecto no se ofrece nada
        // Aceptar lo ofrecido es una acción explícita del operador.
        await _repo.UpdateItemAsync(id, absent, new PlaylistItemUpdate(null, null, null, "DEF"), list.Revision, default);
        Assert.Equal(PlaylistResolutions.Resolved, (await GetAsync(id)).Items.First(i => i.Id == absent).Resolution);
    }

    // ───────────────────────────── duplicar y borrar ─────────────────────────────

    [Fact]
    public async Task Duplicating_copies_order_notes_passages_and_notes_with_a_new_name_and_date_leaving_the_source_untouched()
    {
        var source = (await _repo.CreateAsync("Domingo pasado", "2026-11-22", "notas de la lista", default)).PlaylistId;
        var song = await NewSongAsync("Canción sintética");
        await _repo.AddItemAsync(source, Note("Bienvenida", "Lee el pastor sintético", clear: true), null, default);
        await _repo.AddItemAsync(source, Song(song), null, default);
        await _repo.AddItemAsync(source, Passage(bible: "NOEXISTE"), null, default);                  // Biblia no instalada: se copia igual
        await _repo.AddItemAsync(source, Song(song), null, default);                                  // la misma canción dos veces: dos copias
        var before = await GetAsync(source);

        var result = await _repo.DuplicateAsync(source, "Domingo 29", "2026-11-29", default);

        Assert.Equal((4, 0, 1), (result.Copied, result.Skipped, result.Revision));
        var copy = await GetAsync(result.NewPlaylistId);
        Assert.NotEqual(source, copy.Id);
        Assert.Equal(("Domingo 29", "2026-11-29", "notas de la lista", 1), (copy.Name, copy.ServiceDate, copy.Notes, copy.Revision));
        Assert.Equal(before.Items.Select(i => (i.Position, i.ItemType, i.Title, i.SongId, i.BibleAbbreviation, i.Note, i.ClearOnActivate)),
                     copy.Items.Select(i => (i.Position, i.ItemType, i.Title, i.SongId, i.BibleAbbreviation, i.Note, i.ClearOnActivate)));
        Assert.Empty(copy.Items.Select(i => i.Id).Intersect(before.Items.Select(i => i.Id)));          // ids nuevos
        Assert.Equal(before.Revision, (await GetAsync(source)).Revision);                                // el origen no se tocó
        Assert.Equal(4, (await GetAsync(source)).Items.Count);
        Assert.Equal(2, (await _songs.GetAsync(song, default))!.UsedInServices);                         // la canción está ahora en 2 listas
    }

    [Fact]
    public async Task Duplicating_omits_the_missing_items_and_says_how_many_and_a_template_can_be_dated()
    {
        var template = (await _repo.CreateAsync("Plantilla", null, null, default)).PlaylistId;       // lista sin fecha = plantilla
        var kept = await NewSongAsync("Canción sintética A");
        var doomed = await NewSongAsync("Canción sintética B");
        var doomed2 = await NewSongAsync("Canción sintética C");
        await _repo.AddItemAsync(template, Song(kept), null, default);
        await _repo.AddItemAsync(template, Song(doomed), null, default);
        await _repo.AddItemAsync(template, Note("Oración"), null, default);
        await _repo.AddItemAsync(template, Song(doomed2), null, default);
        await _songs.DeleteAsync(doomed, default);
        await _songs.DeleteAsync(doomed2, default);

        var result = await _repo.DuplicateAsync(template, "Domingo 6 dic", "2026-12-06", default);

        Assert.Equal((2, 2), (result.Copied, result.Skipped));
        var copy = await GetAsync(result.NewPlaylistId);
        Assert.Equal(["song", "note"], copy.Items.Select(i => i.ItemType));
        Assert.Equal([0, 1], copy.Items.Select(i => i.Position));
        Assert.Equal(0, copy.MissingCount);                                                          // la copia no hereda faltantes
        Assert.Equal(4, (await GetAsync(template)).Items.Count);                                     // la plantilla conserva los suyos: nada se pierde del origen
        Assert.Equal(2, (await GetAsync(template)).MissingCount);
        Assert.Equal("2026-12-06", copy.ServiceDate);
    }

    [Fact]
    public async Task Duplicating_validates_before_writing_and_a_missing_source_is_not_found()
    {
        var id = await NewListAsync();
        await FailsAsync(() => _repo.DuplicateAsync(id, "  ", null, default), PlaylistErrorCodes.InvalidRequest);
        await FailsAsync(() => _repo.DuplicateAsync(id, "ok", "2026-02-30", default), PlaylistErrorCodes.InvalidRequest);
        await FailsAsync(() => _repo.DuplicateAsync(9999, "ok", null, default), PlaylistErrorCodes.NotFound);
        Assert.Equal(1, await CountAsync("Playlists"));
    }

    [Fact]
    public async Task Deleting_a_list_takes_its_items_with_it_but_never_the_songs_or_other_lists()
    {
        var one = await NewListAsync("Una");
        var two = await NewListAsync("Dos");
        var song = await NewSongAsync("Canción sintética");
        await _repo.AddItemAsync(one, Song(song), null, default);
        await _repo.AddItemAsync(one, Note("n"), null, default);
        await _repo.AddItemAsync(two, Song(song), null, default);

        await _repo.DeleteAsync(one, (await GetAsync(one)).Revision, default);

        Assert.Null(await _repo.GetDetailAsync(one, null, default));
        Assert.Equal(0, await CountAsync("PlaylistItems", $"PlaylistId = {one}"));
        Assert.Single((await GetAsync(two)).Items);
        Assert.NotNull(await _songs.GetAsync(song, default));
        await FailsAsync(() => _repo.DeleteAsync(one, 1, default), PlaylistErrorCodes.NotFound);
    }

    [Fact]
    public async Task Song_get_names_the_lists_that_use_it_once_each_by_date_and_name()
    {
        var song = await NewSongAsync("Canción sintética");
        var old = await NewListAsync("Domingo viejo", "2026-11-01");
        var recent = await NewListAsync("Domingo nuevo", "2026-11-29");
        var template = await NewListAsync("Plantilla", null);
        await _repo.AddItemAsync(old, Song(song), null, default);
        await _repo.AddItemAsync(recent, Song(song), null, default);
        await _repo.AddItemAsync(recent, Song(song), null, default);        // repetida en la misma lista: cuenta una vez
        await _repo.AddItemAsync(template, Song(song), null, default);

        var detail = (await _songs.GetAsync(song, default))!;

        Assert.Equal(3, detail.UsedInServices);
        Assert.Equal(["Domingo nuevo", "Domingo viejo", "Plantilla"], detail.Lists.Select(l => l.Name));
        Assert.Equal([recent, old, template], detail.Lists.Select(l => l.Id));
    }

    // ───────────────────────────── atomicidad, bloqueo, privacidad ─────────────────────────────

    [Fact]
    public async Task A_failure_halfway_through_an_operation_leaves_no_half_change()
    {
        var id = await NewListAsync();
        var a = (await _repo.AddItemAsync(id, Note("A"), null, default)).ItemId!.Value;
        await _repo.AddItemAsync(id, Note("B"), null, default);
        var before = await GetAsync(id);

        // Una avería simulada: la ÚLTIMA sentencia de cada operación (subir la revisión) falla, después de haber insertado, movido o borrado.
        await using (var c = await _db.OpenAsync())
            await c.ExecuteAsync("CREATE TRIGGER boom BEFORE UPDATE OF UpdatedAt ON Playlists BEGIN SELECT RAISE(ABORT, 'avería simulada'); END;");

        await Assert.ThrowsAsync<SqliteException>(() => _repo.AddItemAsync(id, Note("C"), 0, default));
        await Assert.ThrowsAsync<SqliteException>(() => _repo.MoveItemAsync(id, a, 1, before.Revision, default));
        await Assert.ThrowsAsync<SqliteException>(() => _repo.RemoveItemAsync(id, a, before.Revision, default));
        await Assert.ThrowsAsync<SqliteException>(() => _repo.UpdateItemAsync(id, a, new PlaylistItemUpdate("Cambiada", null, null, null), before.Revision, default));

        await using (var c = await _db.OpenAsync()) await c.ExecuteAsync("DROP TRIGGER boom");
        var after = await GetAsync(id);
        Assert.Equal(before.Items.Select(i => (i.Id, i.Title, i.Position)), after.Items.Select(i => (i.Id, i.Title, i.Position)));
        Assert.Equal(before.Revision, after.Revision);
    }

    [Fact]
    public async Task While_another_writer_holds_the_lock_every_write_gives_up_with_busy_and_changes_nothing()
    {
        var id = await NewListAsync();
        var item = (await _repo.AddItemAsync(id, Note("A"), null, default)).ItemId!.Value;
        var revision = (await GetAsync(id)).Revision;
        await using var blocker = await _db.OpenAsync();
        await blocker.ExecuteAsync("BEGIN IMMEDIATE");   // p. ej. la importación de una Biblia retiene el bloqueo

        var clock = Stopwatch.StartNew();
        await FailsAsync(() => _repo.AddItemAsync(id, Note("B"), null, default), PlaylistErrorCodes.Busy);
        await FailsAsync(() => _repo.RemoveItemAsync(id, item, revision, default), PlaylistErrorCodes.Busy);
        await FailsAsync(() => _repo.MoveItemAsync(id, item, 0, revision, default), PlaylistErrorCodes.Busy);
        await FailsAsync(() => _repo.CreateAsync("Nueva", null, null, default), PlaylistErrorCodes.Busy);
        await FailsAsync(() => _repo.DuplicateAsync(id, "Copia", null, default), PlaylistErrorCodes.Busy);
        await FailsAsync(() => _repo.DeleteAsync(id, revision, default), PlaylistErrorCodes.Busy);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"esperó {clock.Elapsed}: el tiempo del comando debe acotarlo");

        Assert.Single((await GetAsync(id)).Items);                      // las lecturas no esperan al escritor (WAL)
        await blocker.ExecuteAsync("ROLLBACK");
        Assert.Equal((1, revision), ((await GetAsync(id)).Items.Count, (await GetAsync(id)).Revision));
        Assert.Equal(1, await CountAsync("Playlists"));
        await _repo.AddItemAsync(id, Note("B"), null, default);          // y al soltarlo, escribir funciona
        Assert.Equal(2, (await GetAsync(id)).Items.Count);
    }

    [Fact]
    public async Task Nothing_the_repository_logs_or_throws_contains_names_titles_notes_or_lyrics()
    {
        var secret = SyntheticSong.Sentinel;
        var messages = new List<string>();
        void Record(Exception ex) { messages.Add(ex.Message); if (ex.InnerException is { } inner) messages.Add(inner.Message); }

        var song = (await _songs.SaveAsync(new SongSaveRequest(null, null, "Título " + secret, null, null, null, null, null, [new(null, "Verso 1", null, "letra " + secret)], [0]), default)).SongId;
        var id = (await _repo.CreateAsync("Lista " + secret, "2026-11-29", "notas " + secret, default)).PlaylistId;
        var note = (await _repo.AddItemAsync(id, Note("Nota " + secret, "texto " + secret), null, default)).ItemId!.Value;
        var songItem = (await _repo.AddItemAsync(id, Song(song), null, default)).ItemId!.Value;
        await _repo.AddItemAsync(id, Passage(bible: "B-" + secret.Split('-')[0]), null, default);
        var revision = (await GetAsync(id)).Revision;
        await _repo.UpdateItemAsync(id, note, new PlaylistItemUpdate("Otra " + secret, null, null, null), revision, default);
        var copy = await _repo.DuplicateAsync(id, "Copia " + secret, null, default);
        await _songs.DeleteAsync(song, default);
        await _repo.MoveItemAsync(id, songItem, 0, (await GetAsync(id)).Revision, default);

        var latest = (await GetAsync(id)).Revision;
        foreach (var attempt in new Func<Task>[]
        {
            () => _repo.UpdateAsync(id, "Con revisión " + secret, null, null, 1, default),          // conflicto
            () => _repo.AddItemAsync(id, Note(secret + new string('x', 300)), null, default),       // título demasiado largo
            () => _repo.CreateAsync(new string('n', 500) + secret, null, null, default),
            () => _repo.UpdateItemAsync(id, 99999, new PlaylistItemUpdate("x " + secret, null, null, null), latest, default),
            () => _repo.RemoveItemAsync(id, note, 1, default),
        })
        {
            try { await attempt(); }
            catch (Exception ex) { Record(ex); }
        }
        await _repo.DeleteAsync(copy.NewPlaylistId, copy.Revision, default);

        var logged = _log.Entries.Select(e => e.Message).Concat(_log.Entries.Where(e => e.Exception is not null).Select(e => e.Exception!.Message));
        Assert.NotEmpty(_log.Entries);
        foreach (var text in logged.Concat(messages))
        {
            Assert.DoesNotContain(secret, text);
            Assert.DoesNotContain("Xilofon", text);
        }
        // Los registros sí dicen qué pasó, con ids y conteos.
        Assert.Contains(_log.Entries, e => e.Message.Contains($"Lista {id}") && e.Message.Contains("revisión"));
        Assert.Contains(_log.Entries, e => e.Message.Contains("duplicada") && e.Message.Contains("omitidos"));
    }
}
