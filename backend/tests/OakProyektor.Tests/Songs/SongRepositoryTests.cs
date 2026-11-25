using System.Diagnostics;
using Dapper;
using Microsoft.Data.Sqlite;
using OakProyektor.Core.Songs;
using OakProyektor.Data;
using OakProyektor.Data.Songs;
using OakProyektor.Tests.Data;
using OakProyektor.Tests.Ipc;

namespace OakProyektor.Tests.Songs;

/// <summary>El repositorio de canciones sobre una BD real (SQLite en un archivo temporal). Toda la letra es sintética.</summary>
public sealed class SongRepositoryTests : IAsyncLifetime
{
    private readonly TestDatabase _db = new();
    private readonly ListLogger<SongRepository> _log = new();
    private SongRepository _repo = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _repo = new SongRepository(_db.Connections, new AlwaysReady(), _log, new SongRepositoryOptions { WriteTimeoutSeconds = 1 });
    }

    public Task DisposeAsync() { _db.Dispose(); return Task.CompletedTask; }

    private static SongSaveRequest Create(string title = "Canción sintética", string? author = null, params (string Label, string Text)[] sections) =>
        new(null, null, title, author, null, null, null, null,
            [.. (sections.Length == 0 ? [("Verso 1", SyntheticSong.Text(1)), ("Coro", SyntheticSong.Text(2))] : sections).Select(s => new SongSectionInput(null, s.Label, null, s.Text))],
            [.. Enumerable.Range(0, sections.Length == 0 ? 2 : sections.Length)]);

    private async Task<SongDetail> NewSongAsync(string title = "Canción sintética", params (string, string)[] sections)
    {
        var result = await _repo.SaveAsync(Create(title, null, sections), CancellationToken.None);
        return (await _repo.GetAsync(result.SongId, CancellationToken.None))!;
    }

    /// <summary>La petición de actualizar <paramref name="song"/> con los cambios de <paramref name="change"/>.</summary>
    private static SongSaveRequest Update(SongDetail song, Func<SongSaveRequest, SongSaveRequest>? change = null, int? expected = null)
    {
        var request = new SongSaveRequest(song.Id, expected ?? song.Revision, song.Title, song.Author, song.Copyright, song.CcliNumber, song.Notes, song.Background,
            [.. song.Sections.Select(s => new SongSectionInput(s.Id, s.Label, s.Kind, s.Text))], song.Arrangement);
        return change?.Invoke(request) ?? request;
    }

    private async Task<long> CountAsync(string table, string where = "1 = 1")
    {
        await using var c = await _db.OpenAsync();
        return await c.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {table} WHERE {where}");
    }

    private async Task<SongException> FailsAsync(Func<Task> action, string code)
    {
        var ex = await Assert.ThrowsAsync<SongException>(action);
        Assert.Equal(code, ex.Code);
        return ex;
    }

    // ───────────────────────────── crear y leer ─────────────────────────────

    [Fact]
    public async Task A_song_is_created_with_sections_arrangement_and_normalized_metadata()
    {
        var result = await _repo.SaveAsync(new SongSaveRequest(null, null, "  Mi   canción\n sintética ", "  Autora Ficticia ", " ", "CCLI 000", "una nota\n\n\n  otra",
            new SongBackground("video", "C:\\fondos\\a.mp4", "#102030", false),
            [new(null, "verso 1", "ignorado", "uno\n\n\n\ndos  \n"), new(null, "CORO", null, "coro sintetico"), new(null, "Parte A", null, "otro")], [0, 1, 0, 2, 1]), CancellationToken.None);

        Assert.Equal((1, true), (result.Revision, result.Created));
        var song = (await _repo.GetAsync(result.SongId, CancellationToken.None))!;
        Assert.Equal("Mi canción sintética", song.Title);
        Assert.Equal(("Autora Ficticia", null, "CCLI 000", "una nota\n\notra"), (song.Author, song.Copyright, song.CcliNumber, song.Notes));
        Assert.Equal([("Verso 1", "verse", "uno\n\ndos"), ("Coro", "chorus", "coro sintetico"), ("Parte A", "other", "otro")], song.Sections.Select(s => (s.Label, s.Kind, s.Text)));
        Assert.Equal([0, 1, 0, 2, 1], song.Arrangement);
        Assert.Equal(new SongBackground("video", "C:\\fondos\\a.mp4", "#102030", false), song.Background);
        Assert.Equal((1, 0), (song.Revision, song.UsedInServices));
        Assert.True(song.UpdatedAt >= song.CreatedAt.AddSeconds(-1));
        Assert.Equal("[Verso 1]\nuno\n\ndos\n\n[Coro]\ncoro sintetico\n\n[Parte A]\notro\n\nOrden: Verso 1, Coro, Verso 1, Parte A, Coro", song.Text);
        Assert.Equal(0, await CountAsync("SongRevisions"));   // crear no genera copia previa: no hay estado anterior
    }

    [Fact]
    public async Task A_missing_song_is_null_and_a_title_only_song_is_allowed()
    {
        Assert.Null(await _repo.GetAsync(12345, CancellationToken.None));

        var result = await _repo.SaveAsync(new SongSaveRequest(null, null, "Solo título", null, null, null, null, null, [], []), CancellationToken.None);

        var song = (await _repo.GetAsync(result.SongId, CancellationToken.None))!;
        Assert.Empty(song.Sections);
        Assert.Empty(song.Arrangement);
        Assert.Equal("", song.Text);
        Assert.Equal(1, await CountAsync("SongArrangements", $"SongId = {result.SongId}"));
    }

    [Fact]
    public async Task Invalid_requests_fail_with_a_code_and_change_nothing()
    {
        var s = SyntheticSong.Text(1);
        SongSectionInput Sec(string label, string text = "uno") => new(null, label, null, text);
        SongSaveRequest With(string title = "Titulo", IReadOnlyList<SongSectionInput>? sections = null, IReadOnlyList<int>? order = null, SongBackground? bg = null) =>
            new(null, null, title, null, null, null, null, bg, sections ?? [Sec("Verso 1")], order ?? [0]);

        await FailsAsync(() => _repo.SaveAsync(With(title: "  "), default), SongErrorCodes.InvalidRequest);
        await FailsAsync(() => _repo.SaveAsync(With(title: new string('x', SongLimits.MaxTitleChars + 1)), default), SongErrorCodes.InvalidRequest);
        await FailsAsync(() => _repo.SaveAsync(With(sections: [Sec("Coro"), Sec("coro", "dos")], order: [0, 1]), default), SongErrorCodes.InvalidRequest);
        await FailsAsync(() => _repo.SaveAsync(With(sections: [Sec("A, B")]), default), SongErrorCodes.InvalidRequest);
        await FailsAsync(() => _repo.SaveAsync(With(sections: [Sec("Verso 1", "  \n ")]), default), SongErrorCodes.InvalidRequest);
        await FailsAsync(() => _repo.SaveAsync(With(sections: [Sec("Verso 1", "uno\n[Coro]\ndos")]), default), SongErrorCodes.InvalidRequest);
        await FailsAsync(() => _repo.SaveAsync(With(order: []), default), SongErrorCodes.InvalidRequest);
        await FailsAsync(() => _repo.SaveAsync(With(order: [0, 3]), default), SongErrorCodes.InvalidRequest);
        await FailsAsync(() => _repo.SaveAsync(With(order: [.. Enumerable.Repeat(0, SongLimits.MaxArrangementItems + 1)]), default), SongErrorCodes.InvalidRequest);
        await FailsAsync(() => _repo.SaveAsync(With(bg: new SongBackground("gif", "C:\\a.gif", null, true)), default), SongErrorCodes.InvalidRequest);
        await FailsAsync(() => _repo.SaveAsync(With(sections: [.. Enumerable.Range(0, SongLimits.MaxSections + 1).Select(i => Sec($"Seccion {i}"))], order: [0]), default), SongErrorCodes.TooManySections);
        await FailsAsync(() => _repo.SaveAsync(With(sections: [Sec("Verso 1", new string('x', SongLimits.MaxSectionChars + 1))]), default), SongErrorCodes.TextTooLarge);
        await FailsAsync(() => _repo.SaveAsync(With(sections: [Sec("Verso 1", new string('x', SongLimits.MaxSectionChars * 5))]), default), SongErrorCodes.TextTooLarge);
        await FailsAsync(() => _repo.SaveAsync(new SongSaveRequest(null, null, "T", null, null, null, null, null, [new(77, "Verso 1", null, s)], [0]), default), SongErrorCodes.InvalidRequest);   // nueva con id de sección

        Assert.Equal(0, await CountAsync("Songs"));
        Assert.Equal(0, await CountAsync("SongSections"));
    }

    // ───────────────────────────── actualizar, revisión y copia previa ─────────────────────────────

    [Fact]
    public async Task Updating_changes_sections_in_place_keeps_their_ids_and_bumps_the_revision()
    {
        var song = await NewSongAsync();
        var (verseId, chorusId) = (song.Sections[0].Id, song.Sections[1].Id);

        var result = await _repo.SaveAsync(Update(song, r => r with
        {
            Title = "Título nuevo",
            Sections = [new(chorusId, "Coro", null, "coro editado"), new(verseId, "Verso 1", null, "verso editado"), new(null, "Puente", null, "puente nuevo")],
            Arrangement = [1, 0, 0, 2, 1],
        }), CancellationToken.None);

        Assert.Equal((2, false), (result.Revision, result.Created));
        var after = (await _repo.GetAsync(song.Id, CancellationToken.None))!;
        Assert.Equal(("Título nuevo", 2), (after.Title, after.Revision));
        Assert.Equal([chorusId, verseId], after.Sections.Take(2).Select(s => s.Id));   // mismas identidades, otro orden de definición
        Assert.Equal(["Coro", "Verso 1", "Puente"], after.Sections.Select(s => s.Label));
        Assert.Equal(["coro editado", "verso editado", "puente nuevo"], after.Sections.Select(s => s.Text));
        Assert.Equal([1, 0, 0, 2, 1], after.Arrangement);
        Assert.Equal(3, await CountAsync("SongSections", $"SongId = {song.Id}"));
        Assert.Equal(1, await CountAsync("SongArrangements", $"SongId = {song.Id}"));
    }

    [Fact]
    public async Task Two_sections_can_swap_labels_without_tripping_the_unique_index()
    {
        var song = await NewSongAsync("Intercambio", ("Verso 1", "uno"), ("Verso 2", "dos"));

        await _repo.SaveAsync(Update(song, r => r with { Sections = [new(song.Sections[0].Id, "Verso 2", null, "uno"), new(song.Sections[1].Id, "Verso 1", null, "dos")] }), CancellationToken.None);

        var after = (await _repo.GetAsync(song.Id, CancellationToken.None))!;
        Assert.Equal([("Verso 2", "uno"), ("Verso 1", "dos")], after.Sections.Select(s => (s.Label, s.Text)));
    }

    [Fact]
    public async Task A_removed_section_is_deleted_and_so_are_its_references()
    {
        var song = await NewSongAsync("Quitar", ("Verso 1", "uno"), ("Coro", "dos"), ("Puente", "tres"));

        await _repo.SaveAsync(Update(song, r => r with { Sections = [r.Sections[0], r.Sections[2]], Arrangement = [0, 1, 0] }), CancellationToken.None);

        var after = (await _repo.GetAsync(song.Id, CancellationToken.None))!;
        Assert.Equal(["Verso 1", "Puente"], after.Sections.Select(s => s.Label));
        Assert.Equal([0, 1, 0], after.Arrangement);
        Assert.Equal(0, await CountAsync("SongSections", "Label = 'Coro'"));
        Assert.Equal(3, await CountAsync("SongArrangementItems"));
    }

    [Fact]
    public async Task A_stale_revision_fails_with_conflict_and_changes_nothing_not_even_a_backup()
    {
        var song = await NewSongAsync();
        await _repo.SaveAsync(Update(song, r => r with { Title = "Primera edición" }), CancellationToken.None);   // sube a la revisión 2
        var revisionsBefore = await CountAsync("SongRevisions");

        await FailsAsync(() => _repo.SaveAsync(Update(song, r => r with { Title = "Edición con la revisión vieja" }), default), SongErrorCodes.Conflict);

        var after = (await _repo.GetAsync(song.Id, CancellationToken.None))!;
        Assert.Equal(("Primera edición", 2), (after.Title, after.Revision));
        Assert.Equal(revisionsBefore, await CountAsync("SongRevisions"));
        Assert.Equal(song.Sections.Select(s => s.Text), after.Sections.Select(s => s.Text));
    }

    [Fact]
    public async Task Updating_requires_the_expected_revision_and_an_existing_song()
    {
        var song = await NewSongAsync();

        await FailsAsync(() => _repo.SaveAsync(Update(song) with { ExpectedRevision = null }, default), SongErrorCodes.InvalidRequest);
        await FailsAsync(() => _repo.SaveAsync(Update(song) with { Id = 9999, ExpectedRevision = 1 }, default), SongErrorCodes.NotFound);
        await FailsAsync(() => _repo.SaveAsync(Update(song, r => r with { Sections = [new(98765, "Verso 1", null, "x")], Arrangement = [0] }), default), SongErrorCodes.InvalidRequest);   // sección ajena
        await FailsAsync(() => _repo.SaveAsync(Update(song, r => r with { Sections = [r.Sections[0], r.Sections[0] with { Label = "Otra" }] , Arrangement = [0, 1] }), default), SongErrorCodes.InvalidRequest);   // mismo id dos veces

        Assert.Equal(1, (await _repo.GetAsync(song.Id, CancellationToken.None))!.Revision);
        Assert.Equal(0, await CountAsync("SongRevisions"));
    }

    [Fact]
    public async Task Concurrent_saves_with_the_same_revision_let_exactly_one_win()
    {
        var song = await NewSongAsync();
        var repo = new SongRepository(_db.Connections, new AlwaysReady(), _log, new SongRepositoryOptions { WriteTimeoutSeconds = 10 });

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 6).Select(async i =>
        {
            try { await repo.SaveAsync(Update(song, r => r with { Title = "Escritor " + i }), default); return "ok"; }
            catch (SongException ex) { return ex.Code; }
        }));

        Assert.Equal(1, outcomes.Count(o => o == "ok"));
        Assert.Equal(5, outcomes.Count(o => o == SongErrorCodes.Conflict));
        Assert.Equal(2, (await _repo.GetAsync(song.Id, CancellationToken.None))!.Revision);
        Assert.Equal(1, await CountAsync("SongRevisions"));
    }

    [Fact]
    public async Task A_failure_in_the_middle_of_a_save_rolls_everything_back()
    {
        var song = await NewSongAsync("Atómica", ("Verso 1", "uno"), ("Coro", "dos"));
        await using (var c = await _db.OpenAsync())
            await c.ExecuteAsync("CREATE TRIGGER Falla BEFORE INSERT ON SongSections WHEN NEW.Label = 'Falla' BEGIN SELECT RAISE(ABORT, 'fallo simulado'); END;");

        await Assert.ThrowsAsync<SqliteException>(() => _repo.SaveAsync(Update(song, r => r with
        {
            Title = "No debe quedar",
            Sections = [r.Sections[0] with { Text = "editado" }, new(null, "Falla", null, "nueva")],
            Arrangement = [0, 1],
        }), default));

        var after = (await _repo.GetAsync(song.Id, CancellationToken.None))!;
        Assert.Equal(("Atómica", 1), (after.Title, after.Revision));
        Assert.Equal(["uno", "dos"], after.Sections.Select(s => s.Text));
        Assert.Equal([0, 1], after.Arrangement);
        Assert.Equal(0, await CountAsync("SongRevisions"));   // ni siquiera la copia previa: todo o nada
    }

    // ───────────────────────────── copias previas y restauración ─────────────────────────────

    [Fact]
    public async Task Every_update_takes_a_previous_copy_and_only_the_last_twenty_are_kept()
    {
        var song = await NewSongAsync("Copias", ("Verso 1", "version 0"));
        for (var i = 1; i <= 25; i++)
        {
            var current = (await _repo.GetAsync(song.Id, CancellationToken.None))!;
            await _repo.SaveAsync(Update(current, r => r with { Sections = [r.Sections[0] with { Text = $"version {i}" }] }), CancellationToken.None);
        }

        var history = await _repo.GetHistoryAsync(song.Id, CancellationToken.None);

        Assert.Equal(SongLimits.MaxRevisionsPerSong, history.Count);
        Assert.All(history, h => Assert.Equal(("save", 1), (h.Reason, h.SectionCount)));
        Assert.Equal(history.Select(h => h.Id).OrderByDescending(x => x), history.Select(h => h.Id));   // la más reciente primero
        // La copia más reciente guarda el estado ANTERIOR al último guardado (versión 24); la más antigua conservada, la 5.
        await using var c = await _db.OpenAsync();
        var snapshots = (await c.QueryAsync<string>("SELECT Snapshot FROM SongRevisions WHERE SongId = @id ORDER BY Id DESC", new { id = song.Id })).ToList();
        Assert.Contains("version 24", snapshots[0]);
        Assert.Contains("version 5", snapshots[^1]);
        Assert.Equal(26, (await _repo.GetAsync(song.Id, CancellationToken.None))!.Revision);
    }

    [Fact]
    public async Task The_previous_copy_reason_follows_the_save_reason()
    {
        var song = await NewSongAsync();

        await _repo.SaveAsync(Update(song, r => r with { Title = "Importada", Reason = SongSaveReason.Import }), CancellationToken.None);

        Assert.Equal(["import"], (await _repo.GetHistoryAsync(song.Id, CancellationToken.None)).Select(h => h.Reason));
    }

    [Fact]
    public async Task Undo_last_save_restores_the_previous_state_and_can_itself_be_undone()
    {
        var song = await NewSongAsync("Original", ("Verso 1", "texto original"), ("Coro", "coro original"));
        await _repo.SaveAsync(Update(song, r => r with { Title = "Editada", Sections = [r.Sections[0] with { Text = "texto editado" }], Arrangement = [0] }), CancellationToken.None);
        var edited = (await _repo.GetAsync(song.Id, CancellationToken.None))!;
        Assert.Equal(("Editada", 2, 1), (edited.Title, edited.Revision, edited.Sections.Count));

        var undo = await _repo.RestoreRevisionAsync(song.Id, null, edited.Revision, CancellationToken.None);

        Assert.Equal(3, undo.Revision);
        var restored = (await _repo.GetAsync(song.Id, CancellationToken.None))!;
        Assert.Equal("Original", restored.Title);
        Assert.Equal([("Verso 1", "texto original"), ("Coro", "coro original")], restored.Sections.Select(s => (s.Label, s.Text)));
        Assert.Equal([0, 1], restored.Arrangement);
        Assert.Equal(song.Sections[0].Id, restored.Sections[0].Id);   // la sección que seguía existiendo conserva su identidad
        var history = await _repo.GetHistoryAsync(song.Id, CancellationToken.None);
        Assert.Equal(["restore", "save"], history.Select(h => h.Reason));   // restaurar tomó una copia del estado «Editada»

        // «Deshacer el deshacer»: la copia más reciente es el estado Editada.
        await _repo.RestoreRevisionAsync(song.Id, null, restored.Revision, CancellationToken.None);
        Assert.Equal("Editada", (await _repo.GetAsync(song.Id, CancellationToken.None))!.Title);
    }

    [Fact]
    public async Task A_chosen_version_can_be_restored_and_a_deleted_section_comes_back()
    {
        var song = await NewSongAsync("Versiones", ("Verso 1", "uno"), ("Coro", "dos"));
        await _repo.SaveAsync(Update(song, r => r with { Sections = [r.Sections[0]], Arrangement = [0] }), CancellationToken.None);   // el coro desaparece
        var current = (await _repo.GetAsync(song.Id, CancellationToken.None))!;
        var oldest = (await _repo.GetHistoryAsync(song.Id, CancellationToken.None)).Last();

        await _repo.RestoreRevisionAsync(song.Id, oldest.Id, current.Revision, CancellationToken.None);

        var restored = (await _repo.GetAsync(song.Id, CancellationToken.None))!;
        Assert.Equal([("Verso 1", "uno"), ("Coro", "dos")], restored.Sections.Select(s => (s.Label, s.Text)));
        Assert.Equal([0, 1], restored.Arrangement);
    }

    [Fact]
    public async Task Restoring_checks_the_revision_and_the_revision_belongs_to_the_song()
    {
        var one = await NewSongAsync("Una");
        var two = await NewSongAsync("Dos");
        await _repo.SaveAsync(Update(one, r => r with { Title = "Una editada" }), CancellationToken.None);
        var oneCopy = (await _repo.GetHistoryAsync(one.Id, CancellationToken.None)).Single();

        await FailsAsync(() => _repo.RestoreRevisionAsync(one.Id, null, 1, default), SongErrorCodes.Conflict);                // revisión caducada
        await FailsAsync(() => _repo.RestoreRevisionAsync(two.Id, null, 1, default), SongErrorCodes.NotFound);                // sin copias
        await FailsAsync(() => _repo.RestoreRevisionAsync(two.Id, oneCopy.Id, 1, default), SongErrorCodes.NotFound);          // la copia es de otra canción
        await FailsAsync(() => _repo.RestoreRevisionAsync(777, null, 1, default), SongErrorCodes.NotFound);
        await FailsAsync(() => _repo.GetHistoryAsync(777, default), SongErrorCodes.NotFound);

        Assert.Equal("Una editada", (await _repo.GetAsync(one.Id, CancellationToken.None))!.Title);
        Assert.Equal("Dos", (await _repo.GetAsync(two.Id, CancellationToken.None))!.Title);
    }

    [Fact]
    public async Task A_corrupt_copy_is_refused_without_changing_the_song()
    {
        var song = await NewSongAsync();
        await _repo.SaveAsync(Update(song, r => r with { Title = "Editada" }), CancellationToken.None);
        await using (var c = await _db.OpenAsync())
            await c.ExecuteAsync("UPDATE SongRevisions SET Snapshot = '{ esto no es json' WHERE SongId = @id", new { id = song.Id });

        await FailsAsync(() => _repo.RestoreRevisionAsync(song.Id, null, 2, default), SongErrorCodes.InvalidRequest);

        Assert.Equal(("Editada", 2), ((await _repo.GetAsync(song.Id, CancellationToken.None))!.Title, (await _repo.GetAsync(song.Id, CancellationToken.None))!.Revision));
    }

    // ───────────────────────────── borrado ─────────────────────────────

    private async Task<long> AddToServiceAsync(long songId, string title, string playlistName)
    {
        await using var c = await _db.OpenAsync();
        var playlist = await c.ExecuteScalarAsync<long>("INSERT INTO Playlists (Name, ServiceDate) VALUES (@n, '2026-11-29'); SELECT last_insert_rowid();", new { n = playlistName });
        await c.ExecuteAsync("INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title, SongId) VALUES (@p, 0, 'SONG', @t, @s)", new { p = playlist, t = title, s = songId });
        return playlist;
    }

    [Fact]
    public async Task Deleting_a_song_used_in_services_reports_how_many_and_leaves_them_marked_missing_with_their_title()
    {
        var song = await NewSongAsync("Usada en servicios");
        var other = await NewSongAsync("Otra");
        var firstService = await AddToServiceAsync(song.Id, "Usada en servicios", "Domingo 1");
        var secondService = await AddToServiceAsync(song.Id, "Usada en servicios", "Domingo 2");
        await AddToServiceAsync(other.Id, "Otra", "Domingo 3");
        await using (var c = await _db.OpenAsync())   // una canción puede repetirse dentro del MISMO servicio: cuenta como un servicio
            await c.ExecuteAsync("INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title, SongId) VALUES (@p, 1, 'SONG', 'Usada en servicios', @s)", new { p = firstService, s = song.Id });
        Assert.Equal(2, (await _repo.GetAsync(song.Id, CancellationToken.None))!.UsedInServices);

        var result = await _repo.DeleteAsync(song.Id, CancellationToken.None);

        Assert.Equal(new SongDeleteResult(song.Id, 2), result);
        Assert.Null(await _repo.GetAsync(song.Id, CancellationToken.None));
        await using var d = await _db.OpenAsync();
        var items = (await d.QueryAsync<(long PlaylistId, string Title, long? SongId, long IsMissing)>(
            "SELECT PlaylistId, Title, SongId, IsMissing FROM PlaylistItems WHERE PlaylistId IN (@a, @b) ORDER BY PlaylistId, SequenceIndex", new { a = firstService, b = secondService })).ToList();
        Assert.Equal(3, items.Count);
        Assert.All(items, i => Assert.Equal(("Usada en servicios", (long?)null, 1L), (i.Title, i.SongId, i.IsMissing)));   // «Falta: …», nunca una referencia rota ni un elemento perdido
        Assert.Empty(await d.QueryAsync("PRAGMA foreign_key_check"));
        foreach (var table in new[] { "SongSections", "SongArrangements", "SongRevisions" })
            Assert.Equal(0L, await d.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {table} WHERE SongId = @id", new { id = song.Id }));
        Assert.Equal(0L, await d.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM SongArrangementItems WHERE SectionId NOT IN (SELECT Id FROM SongSections)"));
        Assert.NotNull(await _repo.GetAsync(other.Id, CancellationToken.None));
        Assert.Single((await _repo.GetAsync(other.Id, CancellationToken.None))!.Sections, s => s.Label == "Coro");   // los datos de otra canción no se tocaron
        Assert.Empty((await _repo.SearchAsync("Usada", 10, CancellationToken.None)).Songs);
    }

    [Fact]
    public async Task Deleting_an_unused_song_reports_zero_and_a_missing_one_is_not_found()
    {
        var song = await NewSongAsync();

        Assert.Equal(0, (await _repo.DeleteAsync(song.Id, CancellationToken.None)).UsedInServices);
        await FailsAsync(() => _repo.DeleteAsync(song.Id, default), SongErrorCodes.NotFound);
    }

    // ───────────────────────────── lista y búsqueda ─────────────────────────────

    [Fact]
    public async Task The_list_of_an_EMPTY_database_is_empty_not_an_internal_error()
    {
        // Primer arranque, sin canciones: la columna calculada sin filas hacía fallar a Dapper («Error interno» al abrir la pestaña).
        var list = await _repo.ListAsync(CancellationToken.None);

        Assert.Empty(list.Songs);
        Assert.False(list.Truncated);
    }
    [Fact]
    public async Task The_list_is_ordered_by_title_ignoring_case_and_accents()
    {
        foreach (var title in new[] { "Zeta", "Él viaja", "ábaco", "Beta", "el", "Ñandú", "Nube" }) await NewSongAsync(title);

        var list = await _repo.ListAsync(CancellationToken.None);

        Assert.False(list.Truncated);
        Assert.Equal(["ábaco", "Beta", "el", "Él viaja", "Ñandú", "Nube", "Zeta"], list.Songs.Select(s => s.Title));   // «Ñ» se ordena como «N» sin tilde
        Assert.All(list.Songs, s => Assert.Null(s.Fragment));
    }

    [Fact]
    public async Task The_list_is_capped_and_says_so()
    {
        await using (var c = await _db.OpenAsync())
        {
            await using var tx = c.BeginTransaction();
            for (var i = 0; i < SongLimits.MaxListed + 5; i++) await c.ExecuteAsync("INSERT INTO Songs (Title) VALUES (@t)", new { t = $"Cancion {i:0000}" }, tx);
            await tx.CommitAsync();
        }

        var list = await _repo.ListAsync(CancellationToken.None);

        Assert.True(list.Truncated);
        Assert.Equal(SongLimits.MaxListed, list.Songs.Count);
    }

    [Fact]
    public async Task Search_finds_title_author_and_lyrics_without_accents_and_by_prefix()
    {
        var a = await _repo.SaveAsync(Create("Himno del señor", "Autora Ficticia", ("Verso 1", "Lumarí tenóko sabíko"), ("Coro", "Ñandú brillante")), default);
        var b = await _repo.SaveAsync(Create("Otra pieza", null, ("Verso 1", "Nada que ver aquí")), default);

        async Task<long[]> Find(string q) => [.. (await _repo.SearchAsync(q, 10, default)).Songs.Select(s => s.Id)];

        Assert.Equal([a.SongId], await Find("senor"));                    // título, sin tilde
        Assert.Equal([a.SongId], await Find("SEÑOR"));
        Assert.Equal([a.SongId], await Find("ficticia"));                 // autor
        Assert.Equal([a.SongId], await Find("lumari"));                   // letra, sin tilde
        Assert.Equal([a.SongId], await Find("nandu"));
        Assert.Equal([a.SongId], await Find("lum"));                      // la última palabra por prefijo
        Assert.Equal([a.SongId], await Find("himno lum"));
        Assert.Equal([a.SongId], await Find("\"tenoko sabiko\""));        // frase exacta
        Assert.Empty(await Find("\"sabiko tenoko\""));                    // el orden de una frase importa entre comillas…
        Assert.Equal([a.SongId], await Find("sabiko tenoko"));            // …y sin ellas basta con que estén todas las palabras
        Assert.Equal([b.SongId], await Find("nada"));
        Assert.Empty(await Find("inexistente"));
    }

    [Fact]
    public async Task Search_returns_a_fragment_with_the_matches_marked()
    {
        await _repo.SaveAsync(Create("Pieza", null, ("Verso 1", "una linea con tenoko adentro y mas palabras de relleno")), default);

        var hit = (await _repo.SearchAsync("tenoko", 10, default)).Songs.Single();

        Assert.Contains("«tenoko»", hit.Fragment);
    }

    [Fact]
    public async Task A_chorus_repeated_in_the_arrangement_is_indexed_once()
    {
        var saved = await _repo.SaveAsync(new SongSaveRequest(null, null, "Con repetición", null, null, null, null, null,
            [new(null, "Verso 1", null, "uno sintetico"), new(null, "Coro", null, "estribillo zurimako")], [0, 1, 0, 1, 1, 1]), default);

        await using var c = await _db.OpenAsync();
        var content = await c.ExecuteScalarAsync<string>("SELECT Content FROM SongsFts WHERE rowid = @id", new { id = saved.SongId });
        Assert.Equal("uno sintetico\nestribillo zurimako", content);
        Assert.Single((await _repo.SearchAsync("zurimako", 10, default)).Songs);
    }

    [Fact]
    public async Task The_search_index_follows_every_write()
    {
        var song = await NewSongAsync("Seguimiento", ("Verso 1", "palabra inicial"));
        Assert.Single((await _repo.SearchAsync("inicial", 5, default)).Songs);

        await _repo.SaveAsync(Update(song, r => r with { Title = "Seguimiento nuevo", Author = "Alguien", Sections = [r.Sections[0] with { Text = "palabra cambiada" }] }), default);
        Assert.Empty((await _repo.SearchAsync("inicial", 5, default)).Songs);
        Assert.Single((await _repo.SearchAsync("cambiada", 5, default)).Songs);
        Assert.Single((await _repo.SearchAsync("alguien", 5, default)).Songs);

        await _repo.RestoreRevisionAsync(song.Id, null, 2, default);
        Assert.Single((await _repo.SearchAsync("inicial", 5, default)).Songs);
        Assert.Empty((await _repo.SearchAsync("cambiada", 5, default)).Songs);

        await _repo.DeleteAsync(song.Id, default);
        Assert.Empty((await _repo.SearchAsync("inicial", 5, default)).Songs);
        await using var c = await _db.OpenAsync();
        await c.ExecuteAsync("INSERT INTO SongsFts (SongsFts) VALUES ('integrity-check')");   // el índice sigue consistente
    }

    [Theory]
    [InlineData("\"")]
    [InlineData("\"\"")]
    [InlineData("\"sin cerrar")]
    [InlineData("*")]
    [InlineData("lum*")]
    [InlineData("NEAR(lumari tenoko)")]
    [InlineData("NEAR(")]
    [InlineData("-lumari")]
    [InlineData("lumari - tenoko")]
    [InlineData("lumari AND tenoko")]
    [InlineData("lumari OR tenoko")]
    [InlineData("NOT lumari")]
    [InlineData("title:lumari")]
    [InlineData("author:\"x\" content:y")]
    [InlineData("{title author}: lumari")]
    [InlineData("^lumari")]
    [InlineData("(((")]
    [InlineData("lumari)")]
    [InlineData("'; DROP TABLE Songs; --")]
    [InlineData("\\")]
    [InlineData("lumari\u0000tenoko")]
    [InlineData("é\u0301")]
    [InlineData("🎵🎵🎵")]
    [InlineData("a b c d e f g h i j k l m n o p q r s t u v w x y z a1 b1 c1 d1 e1 f1 g1 h1 i1 j1 k1 l1")]
    public async Task Hostile_search_input_never_reaches_fts_as_syntax_and_never_throws(string query)
    {
        await _repo.SaveAsync(Create("Pieza de prueba", null, ("Verso 1", "lumari tenoko sabiko")), default);

        // Lo único aceptable: un resultado, o el error claro «escribe al menos una palabra» cuando no queda ninguna palabra. Nunca un error de SQLite/FTS5.
        try { Assert.NotNull(await _repo.SearchAsync(query, 10, default)); }
        catch (SongException ex) { Assert.Equal(SongErrorCodes.InvalidRequest, ex.Code); }
    }

    [Fact]
    public async Task Search_operators_are_only_words_and_the_term_count_is_capped()
    {
        await _repo.SaveAsync(Create("Pieza", null, ("Verso 1", "lumari tenoko")), default);
        await _repo.SaveAsync(Create("Otra", null, ("Verso 1", "lumari sabiko")), default);

        // «OR» y «NEAR» son palabras como las demás (y deben estar todas): ninguna canción las tiene.
        Assert.Empty((await _repo.SearchAsync("lumari OR tenoko", 10, default)).Songs);
        Assert.Empty((await _repo.SearchAsync("NEAR(lumari tenoko)", 10, default)).Songs);
        // Un guion no excluye: «-tenoko» busca «tenoko».
        Assert.Single((await _repo.SearchAsync("lumari -tenoko", 10, default)).Songs);
        // Más de 32 términos: se recorta (las primeras 32 palabras), sin armar una consulta enorme.
        var many = string.Join(' ', Enumerable.Range(0, 200).Select(i => "lumari"));
        Assert.Equal(2, (await _repo.SearchAsync(many, 10, default)).Songs.Count);
        Assert.Equal(FtsQueryBuilder.MaxQueryTerms, FtsQueryBuilder.Build(many)!.Split(' ').Length);
        Assert.True(FtsQueryBuilder.Build(new string('a', 10_000))!.Length <= FtsQueryBuilder.MaxQueryChars + 2);
    }

    [Fact]
    public async Task An_empty_query_is_a_clear_error_and_the_limit_is_honoured()
    {
        await FailsAsync(() => _repo.SearchAsync("   ", 10, default), SongErrorCodes.InvalidRequest);
        await FailsAsync(() => _repo.SearchAsync("\"\" ** --", 10, default), SongErrorCodes.InvalidRequest);
        for (var i = 0; i < 5; i++) await _repo.SaveAsync(Create($"Pieza {i}", null, ("Verso 1", "lumari")), default);

        var result = await _repo.SearchAsync("lumari", 3, default);

        Assert.Equal((3, true), (result.Songs.Count, result.Truncated));
        Assert.Equal(5, (await _repo.SearchAsync("lumari", 0, default)).Songs.Count);
    }

    [Fact]
    public void The_prefix_builder_marks_only_a_single_last_word()
    {
        Assert.Equal("\"himno\" \"lum\"*", FtsQueryBuilder.Build("himno lum", prefixOnLastWord: true));
        Assert.Equal("\"una frase exacta\"", FtsQueryBuilder.Build("\"una frase exacta\"", prefixOnLastWord: true));
        Assert.Equal("\"himno\" \"lum\"", FtsQueryBuilder.Build("himno lum"));
        Assert.Null(FtsQueryBuilder.Build("*** ---", prefixOnLastWord: true));
    }

    // ───────────────────────────── bloqueo de escritura, privacidad ─────────────────────────────

    [Fact]
    public async Task While_another_writer_holds_the_lock_saving_gives_up_with_busy_and_changes_nothing()
    {
        var song = await NewSongAsync();
        await using var blocker = await _db.OpenAsync();
        await blocker.ExecuteAsync("BEGIN IMMEDIATE");   // otro escritor (p. ej. una importación de Biblia) retiene el bloqueo

        var clock = Stopwatch.StartNew();
        await FailsAsync(() => _repo.SaveAsync(Update(song, r => r with { Title = "Bloqueada" }), default), SongErrorCodes.Busy);
        await FailsAsync(() => _repo.SaveAsync(Create("Nueva bloqueada"), default), SongErrorCodes.Busy);
        await FailsAsync(() => _repo.DeleteAsync(song.Id, default), SongErrorCodes.Busy);
        await FailsAsync(() => _repo.RestoreRevisionAsync(song.Id, null, 1, default), SongErrorCodes.Busy);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"esperó {clock.Elapsed}: el tiempo del comando debe acotarlo");

        // Las lecturas no esperan al escritor (WAL).
        Assert.Equal("Canción sintética", (await _repo.GetAsync(song.Id, default))!.Title);
        await blocker.ExecuteAsync("ROLLBACK");

        Assert.Equal(1, (await _repo.GetAsync(song.Id, default))!.Revision);
        Assert.Equal(1, await CountAsync("Songs"));
        Assert.Equal(0, await CountAsync("SongRevisions"));
        await _repo.SaveAsync(Update(song, r => r with { Title = "Ya sí" }), default);   // y al soltarlo, guardar funciona
        Assert.Equal("Ya sí", (await _repo.GetAsync(song.Id, default))!.Title);
    }

    [Fact]
    public async Task Nothing_the_repository_logs_or_throws_contains_the_lyrics_titles_or_labels()
    {
        var secret = SyntheticSong.Sentinel;
        var created = await _repo.SaveAsync(new SongSaveRequest(null, null, "Título " + secret, "Autor " + secret, null, null, "Nota " + secret, null,
            [new(null, "Sección " + secret.Split('-')[0], null, "letra " + secret), new(null, "Coro", null, "coro " + secret)], [0, 1, 0]), default);
        var song = (await _repo.GetAsync(created.SongId, default))!;
        var messages = new List<string>();

        async Task Try(Func<Task> action)
        {
            try { await action(); }
            catch (Exception ex) { messages.Add(ex.ToString()); }
        }
        await Try(() => _repo.SaveAsync(Update(song, r => r with { Title = "Conflicto " + secret }, expected: 99), default));
        await Try(() => _repo.SaveAsync(Update(song, r => r with { Sections = [new(null, "Coro", null, "x " + secret), new(null, "coro", null, secret)], Arrangement = [0, 1] }), default));
        await Try(() => _repo.SaveAsync(Update(song, r => r with { Sections = [new(null, "Parte, mala " + secret, null, "x")], Arrangement = [0] }), default));
        await Try(() => _repo.SaveAsync(Update(song, r => r with { Sections = [new(null, "Verso 1", null, "[Coro " + secret + "]")], Arrangement = [0] }), default));
        await Try(() => _repo.SaveAsync(Update(song, r => r with { Sections = [new(null, "Verso 1", null, new string('z', SongLimits.MaxSectionChars + 1) + secret)], Arrangement = [0] }), default));
        await Try(() => _repo.SaveAsync(Update(song with { Title = "  " }), default));
        await _repo.SaveAsync(Update(song, r => r with { Title = "Editada " + secret }), default);
        await _repo.RestoreRevisionAsync(song.Id, null, 2, default);
        await _repo.SearchAsync(secret, 5, default);
        await _repo.DeleteAsync(song.Id, default);

        Assert.NotEmpty(messages);
        Assert.All(messages, m => Assert.DoesNotContain("Xilofon", m));
        Assert.NotEmpty(_log.Entries);
        Assert.All(_log.Entries, e => Assert.DoesNotContain("Xilofon", e.Message + e.Exception));
        Assert.All(_log.Entries, e => Assert.DoesNotContain("Quimérica", e.Message));
    }

    [Fact]
    public async Task Unicode_titles_and_lyrics_survive_intact()
    {
        var text = "日本語の行 sintético\n🎵 emoji y ñandú — «comillas» ¡señal!";
        var created = await _repo.SaveAsync(Create("Canción 日本 🎵", null, ("Verso 1", text)), default);

        var song = (await _repo.GetAsync(created.SongId, default))!;

        Assert.Equal("Canción 日本 🎵", song.Title);
        Assert.Equal(text, song.Sections[0].Text);
    }
}
