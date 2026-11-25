using System.Text.Json;
using OakProyektor.App;
using OakProyektor.App.Handlers;
using OakProyektor.Core.Songs;
using OakProyektor.Data;
using OakProyektor.Data.Songs;
using OakProyektor.Ipc;
using OakProyektor.Ipc.Messages;
using OakProyektor.Tests.App;
using OakProyektor.Tests.Data;
using OakProyektor.Tests.Ipc;
using Microsoft.Extensions.Logging.Abstractions;

namespace OakProyektor.Tests.Songs;

/// <summary>Los mensajes song.* de extremo a extremo (despachador + handlers reales + BD real): respuestas tipadas, errores con código, eventos y PRIVACIDAD de los registros.</summary>
public sealed class SongIpcTests : IAsyncLifetime
{
    private readonly TestDatabase _db = new();
    private readonly ListLogger<IpcDispatcher> _dispatcherLog = new();
    private readonly ListLogger<SongRepository> _repositoryLog = new();
    private readonly Outbox _outbox = new(NullLogger<Outbox>.Instance);
    private readonly InMemorySettings _settings = new();
    private IpcDispatcher _dispatcher = null!;
    private SongRepository _songs = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _songs = new SongRepository(_db.Connections, new AlwaysReady(), _repositoryLog, new SongRepositoryOptions { WriteTimeoutSeconds = 1 });
        IIpcHandler[] handlers =
        [
            new SongSearchHandler(_songs), new SongListHandler(_songs), new SongGetHandler(_songs), new SongParseTextHandler(), new SongHistoryHandler(_songs),
            new SongGetSettingsHandler(_settings), new SongSetSettingsHandler(_settings), new SongSaveHandler(_songs, _outbox), new SongRestoreRevisionHandler(_songs, _outbox),
            new SongDeleteHandler(_songs, _outbox),
        ];
        _dispatcher = new IpcDispatcher(handlers, _dispatcherLog);
    }

    public Task DisposeAsync() { _db.Dispose(); return Task.CompletedTask; }

    private static string Msg(string kind, string type, object payload, string id = "c1") =>
        $$"""{"v":{{IpcCatalog.ProtocolVersion}},"id":"{{id}}","kind":"{{kind}}","type":"{{type}}","payload":{{JsonSerializer.Serialize(payload, IpcSerializer.Options)}}}""";

    private static string RawSave(string payloadJson) =>
        "{\"v\":" + IpcCatalog.ProtocolVersion + ",\"id\":\"x\",\"kind\":\"command\",\"type\":\"song.save\",\"payload\":" + payloadJson + "}";

    private async Task<IpcEnvelope> QueryAsync(string type, object payload) => (await _dispatcher.DispatchAsync(Msg(IpcKind.Query, type, payload, "q1"), CancellationToken.None))!;

    private Task<IpcEnvelope?> CommandAsync(string type, object payload, string id = "c1") => _dispatcher.DispatchAsync(Msg(IpcKind.Command, type, payload, id), CancellationToken.None);

    /// <summary>Todo lo que el backend publicó hacia el panel hasta ahora (sin esperar).</summary>
    private async Task<List<IpcEnvelope>> PublishedAsync()
    {
        var list = new List<IpcEnvelope>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        try
        {
            await foreach (var (_, json) in _outbox.ReadAllAsync(cts.Token))
            {
                Assert.True(IpcSerializer.TryParse(json, out var envelope, out var error), error);
                list.Add(envelope!);
            }
        }
        catch (OperationCanceledException) { /* se vació la cola */ }
        return list;
    }

    private static SongSavePayload NewSong(string title = "Canción sintética", string? text = null) =>
        new(null, null, title, "Autora Ficticia", null, null, null, null,
            [new(null, "x", "Verso 1", text ?? SyntheticSong.Text(1)), new(null, "x", "Coro", SyntheticSong.Text(2))], [0, 1, 0], null);

    private async Task<SongPayload> SaveAndGetAsync(SongSavePayload payload)
    {
        Assert.Null(await CommandAsync(IpcTypes.SongSave, payload));
        var changed = (await PublishedAsync()).Single(e => e.Type == IpcTypes.SongChanged).GetPayload<SongChangedPayload>();
        return (await QueryAsync(IpcTypes.SongGet, new SongGetRequest(changed.SongId))).ReplyResult<SongPayload>();
    }

    // ───────────────────────────── consultas ─────────────────────────────

    [Fact]
    public async Task Saving_publishes_song_changed_with_the_command_id_and_get_returns_the_song_with_its_text()
    {
        Assert.Null(await CommandAsync(IpcTypes.SongSave, NewSong(), id: "cmd-77"));   // un comando exitoso no responde

        var events = await PublishedAsync();
        var changed = Assert.Single(events, e => e.Type == IpcTypes.SongChanged).GetPayload<SongChangedPayload>();
        Assert.Equal(("cmd-77", "created", 1, 0), (changed.CommandId, changed.Kind, changed.Revision, changed.UsedInServices));

        var song = (await QueryAsync(IpcTypes.SongGet, new SongGetRequest(changed.SongId))).ReplyResult<SongPayload>();
        Assert.Equal(("Canción sintética", "Autora Ficticia", 1), (song.Title, song.Author, song.Revision));
        Assert.Equal(["Verso 1", "Coro"], song.Sections.Select(s => s.Label));
        Assert.Equal(["verse", "chorus"], song.Sections.Select(s => s.Kind));   // el panel mandó «x»: el tipo se derivó de la etiqueta
        Assert.Equal([0, 1, 0], song.Arrangement);
        Assert.Equal($"[Verso 1]\n{SyntheticSong.Text(1)}\n\n[Coro]\n{SyntheticSong.Text(2)}\n\nOrden: Verso 1, Coro, Verso 1", song.Text);
        Assert.All(song.Sections, s => Assert.NotNull(s.Id));
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", song.UpdatedAt);
    }

    [Fact]
    public async Task Update_and_restore_flow_through_events_and_a_stale_revision_comes_back_as_command_failed()
    {
        var song = await SaveAndGetAsync(NewSong());

        Assert.Null(await CommandAsync(IpcTypes.SongSave, new SongSavePayload(song.Id, 1, "Editada", null, null, null, null, null, song.Sections, song.Arrangement, "import"), id: "upd"));
        var updated = Assert.Single(await PublishedAsync(), e => e.Type == IpcTypes.SongChanged).GetPayload<SongChangedPayload>();
        Assert.Equal(("upd", "updated", 2), (updated.CommandId, updated.Kind, updated.Revision));

        var stale = await CommandAsync(IpcTypes.SongSave, new SongSavePayload(song.Id, 1, "Con la revisión vieja", null, null, null, null, null, song.Sections, song.Arrangement, null), id: "old");
        var failed = stale!.GetPayload<CommandFailedPayload>();
        Assert.Equal(("old", IpcTypes.SongSave, SongErrorCodes.Conflict), (failed.CommandId, failed.CommandType, failed.Error.Code));
        Assert.Empty(await PublishedAsync());   // un fallo no publica song.changed

        var history = (await QueryAsync(IpcTypes.SongHistory, new SongHistoryRequest(song.Id))).ReplyResult<SongHistoryPayload>();
        var copy = Assert.Single(history.Revisions);
        Assert.Equal(("import", 2), (copy.Reason, copy.SectionCount));

        Assert.Null(await CommandAsync(IpcTypes.SongRestoreRevision, new SongRestoreRevisionPayload(song.Id, null, 2), id: "undo"));
        var restored = Assert.Single(await PublishedAsync(), e => e.Type == IpcTypes.SongChanged).GetPayload<SongChangedPayload>();
        Assert.Equal(("undo", "restored", 3), (restored.CommandId, restored.Kind, restored.Revision));
        Assert.Equal("Canción sintética", (await QueryAsync(IpcTypes.SongGet, new SongGetRequest(song.Id))).ReplyResult<SongPayload>().Title);
    }

    [Fact]
    public async Task Deleting_publishes_how_many_services_lost_their_song()
    {
        var song = await SaveAndGetAsync(NewSong());
        await using (var c = await _db.OpenAsync())
        {
            await Dapper.SqlMapper.ExecuteAsync(c, "INSERT INTO Playlists (Name) VALUES ('Domingo'), ('Jueves')");
            await Dapper.SqlMapper.ExecuteAsync(c, "INSERT INTO PlaylistItems (PlaylistId, SequenceIndex, ItemType, Title, SongId) VALUES (1, 0, 'SONG', 'T', @s), (2, 0, 'SONG', 'T', @s)", new { s = song.Id });
        }
        Assert.Equal(2, (await QueryAsync(IpcTypes.SongGet, new SongGetRequest(song.Id))).ReplyResult<SongPayload>().UsedInServices);

        Assert.Null(await CommandAsync(IpcTypes.SongDelete, new SongDeletePayload(song.Id), id: "del"));

        var deleted = Assert.Single(await PublishedAsync(), e => e.Type == IpcTypes.SongChanged).GetPayload<SongChangedPayload>();
        Assert.Equal(("del", "deleted", 0, 2), (deleted.CommandId, deleted.Kind, deleted.Revision, deleted.UsedInServices));
        Assert.Equal(SongErrorCodes.NotFound, (await QueryAsync(IpcTypes.SongGet, new SongGetRequest(song.Id))).ReplyError().Code);
        Assert.Equal(SongErrorCodes.NotFound, (await CommandAsync(IpcTypes.SongDelete, new SongDeletePayload(song.Id)))!.GetPayload<CommandFailedPayload>().Error.Code);
    }

    [Fact]
    public async Task List_search_and_history_answer_with_typed_replies_and_errors_have_stable_codes()
    {
        await SaveAndGetAsync(NewSong("Himno uno", "lumari zurimako sintetico"));
        await SaveAndGetAsync(NewSong("Himno dos", "otra cosa distinta"));

        var list = (await QueryAsync(IpcTypes.SongList, new EmptyPayload())).ReplyResult<SongListPayload>();
        Assert.False(list.Truncated);
        Assert.Equal(["Himno dos", "Himno uno"], list.Songs.Select(s => s.Title));

        var search = (await QueryAsync(IpcTypes.SongSearch, new SongSearchRequest("zurim", 10))).ReplyResult<SongSearchPayload>();
        var hit = Assert.Single(search.Songs);
        Assert.Equal("Himno uno", hit.Title);
        Assert.Contains("«", hit.Fragment);

        Assert.Equal(SongErrorCodes.InvalidRequest, (await QueryAsync(IpcTypes.SongSearch, new SongSearchRequest("  ", 10))).ReplyError().Code);
        Assert.Equal(SongErrorCodes.NotFound, (await QueryAsync(IpcTypes.SongGet, new SongGetRequest(4040))).ReplyError().Code);
        Assert.Equal(SongErrorCodes.NotFound, (await QueryAsync(IpcTypes.SongHistory, new SongHistoryRequest(4040))).ReplyError().Code);
        var noCopies = await QueryAsync(IpcTypes.SongHistory, new SongHistoryRequest(hit.Id));
        Assert.True(noCopies.GetPayload<IpcReply>().Ok, noCopies.Payload.GetRawText());
        Assert.Empty(noCopies.ReplyResult<SongHistoryPayload>().Revisions);
    }

    [Fact]
    public async Task Invalid_saves_fail_with_a_readable_code_and_message_and_publish_nothing()
    {
        async Task<IpcError> Fail(SongSavePayload payload) => (await CommandAsync(IpcTypes.SongSave, payload))!.GetPayload<CommandFailedPayload>().Error;

        var noTitle = await Fail(NewSong(title: "  "));
        Assert.Equal(SongErrorCodes.InvalidRequest, noTitle.Code);
        Assert.Contains("título", noTitle.Message);
        Assert.Equal(SongErrorCodes.InvalidRequest, (await Fail(NewSong() with { Arrangement = [0, 9] })).Code);
        Assert.Equal(SongErrorCodes.InvalidRequest, (await Fail(NewSong() with { Reason = "restore" })).Code);
        Assert.Equal(SongErrorCodes.TooManySections, (await Fail(NewSong() with { Sections = [.. Enumerable.Range(0, 61).Select(i => new SongSectionDto(null, "x", $"Parte {i}", "uno"))], Arrangement = [0] })).Code);
        Assert.Equal(SongErrorCodes.TextTooLarge, (await Fail(NewSong(text: new string('x', 4001)))).Code);
        Assert.Equal(SongErrorCodes.InvalidRequest, (await Fail(NewSong() with { Id = 5 })).Code);   // actualizar sin revisión esperada

        Assert.Empty(await PublishedAsync());
    }

    [Fact]
    public async Task Parse_text_is_a_pure_query_with_sections_order_warnings_errors_and_the_detected_format()
    {
        var ok = (await QueryAsync(IpcTypes.SongParseText, new SongParseTextRequest("[verso 1]\nuno\n\n[Coro]\ndos\n\n[Coro]\n\nOrden: V1 C C", true))).ReplyResult<SongParseTextPayload>();
        Assert.Equal("brackets", ok.Detected);
        Assert.Equal([("Verso 1", "verse"), ("Coro", "chorus")], ok.Sections.Select(s => (s.Label, s.Kind)));
        Assert.All(ok.Sections, s => Assert.Null(s.Id));
        Assert.Equal([0, 1, 1], ok.Arrangement);
        Assert.Empty(ok.Errors);

        var blocks = (await QueryAsync(IpcTypes.SongParseText, new SongParseTextRequest("uno\n\ndos\n\nuno", false))).ReplyResult<SongParseTextPayload>();
        Assert.Equal(("blocks", 3), (blocks.Detected, blocks.Sections.Count));

        var broken = (await QueryAsync(IpcTypes.SongParseText, new SongParseTextRequest("[Verso 1]\nuno\n\nOrden:", true))).ReplyResult<SongParseTextPayload>();
        Assert.Contains(broken.Errors, e => e.Contains("orden está vacío"));

        Assert.Equal("empty", (await QueryAsync(IpcTypes.SongParseText, new SongParseTextRequest("", true))).ReplyResult<SongParseTextPayload>().Detected);
    }

    [Fact]
    public async Task Parse_text_survives_hostile_input_over_the_wire()
    {
        var text = "[Coro]\n" + string.Join('\n', Enumerable.Repeat("[Coro]", 5000)) + "\u0000\uD800";

        var reply = (await QueryAsync(IpcTypes.SongParseText, new SongParseTextRequest(text, true))).ReplyResult<SongParseTextPayload>();

        Assert.NotEmpty(reply.Errors.Concat(reply.Warnings));
    }

    // ───────────────────────────── configuración ─────────────────────────────

    [Fact]
    public async Task Settings_default_to_the_song_theme_a_gentle_crossfade_and_staying_on_the_last_slide()
    {
        var s = (await QueryAsync(IpcTypes.SongGetSettings, new EmptyPayload())).ReplyResult<SongSettingsPayload>();

        Assert.Equal((SongDisplaySettings.DefaultTheme, SongDisplaySettings.DefaultTransitionMs, SongAtEnd.Stay), (s.Theme, s.TransitionMs, s.AtEnd));
    }

    [Fact]
    public async Task Valid_settings_are_saved_and_invalid_ones_change_nothing()
    {
        var theme = SongDisplaySettings.DefaultTheme with { FontSize = 80 };

        Assert.Null(await CommandAsync(IpcTypes.SongSetSettings, new SongSettingsPayload(theme, 250, SongAtEnd.Clear)));
        var saved = (await QueryAsync(IpcTypes.SongGetSettings, new EmptyPayload())).ReplyResult<SongSettingsPayload>();
        Assert.Equal((80, 250, SongAtEnd.Clear), (saved.Theme.FontSize, saved.TransitionMs, saved.AtEnd));

        async Task<string> Fail(SongSettingsPayload p) => (await CommandAsync(IpcTypes.SongSetSettings, p))!.GetPayload<CommandFailedPayload>().Error.Code;
        Assert.Equal(SongErrorCodes.InvalidSettings, await Fail(new SongSettingsPayload(theme with { Color = "rojo" }, 250, SongAtEnd.Stay)));
        Assert.Equal(SongErrorCodes.InvalidSettings, await Fail(new SongSettingsPayload(theme, -5, SongAtEnd.Stay)));
        Assert.Equal(SongErrorCodes.InvalidSettings, await Fail(new SongSettingsPayload(theme, 99_999, SongAtEnd.Stay)));

        var after = (await QueryAsync(IpcTypes.SongGetSettings, new EmptyPayload())).ReplyResult<SongSettingsPayload>();
        Assert.Equal((80, 250, SongAtEnd.Clear), (after.Theme.FontSize, after.TransitionMs, after.AtEnd));   // lo guardado antes sigue igual
    }

    // ───────────────────────────── privacidad ─────────────────────────────

    [Fact]
    public async Task No_log_event_or_error_contains_the_lyrics_even_with_broken_messages()
    {
        var secret = SyntheticSong.Sentinel;
        var song = await SaveAndGetAsync(NewSong("Título " + secret, "letra " + secret));
        _ = await PublishedAsync();

        // Mensajes rotos que llevan la letra: JSON inválido, tipos equivocados, payloads inválidos, errores del backend.
        await _dispatcher.DispatchAsync(RawSave("{\"title\":\"" + secret + "\", oops " + secret + " }"), CancellationToken.None);
        await _dispatcher.DispatchAsync(RawSave(secret), CancellationToken.None);
        var wrongTypes = await _dispatcher.DispatchAsync(RawSave("{\"id\":\"" + secret + "\",\"title\":\"" + secret + "\",\"sections\":\"" + secret + "\"}"), CancellationToken.None);
        var invalidSave = await CommandAsync(IpcTypes.SongSave, new SongSavePayload(song.Id, 99, "T " + secret, null, null, null, null, null, [new(null, "x", "A, " + secret, secret)], [0, 0, 7], null));
        var conflict = await CommandAsync(IpcTypes.SongSave, new SongSavePayload(song.Id, 99, "T", null, null, null, null, null, song.Sections, song.Arrangement, null));
        var parse = await QueryAsync(IpcTypes.SongParseText, new SongParseTextRequest($"[Coro]\n{secret}\n\n[coro]\nOtra {secret}\n\nOrden: {secret}", true));
        await QueryAsync(IpcTypes.SongSearch, new SongSearchRequest(secret, 5));
        await CommandAsync(IpcTypes.SongSave, song.ToUpdate(secret));
        await CommandAsync(IpcTypes.SongDelete, new SongDeletePayload(song.Id));

        var everything = new List<string>();
        everything.AddRange(_dispatcherLog.Entries.Select(e => e.Message + e.Exception));
        everything.AddRange(_repositoryLog.Entries.Select(e => e.Message + e.Exception));
        foreach (var response in new[] { wrongTypes, invalidSave, conflict }) everything.Add(response!.Payload.GetRawText());   // command.failed
        everything.AddRange((await PublishedAsync()).Select(e => e.Payload.GetRawText()));                                       // eventos song.changed
        everything.Add(string.Join(" ", parse.ReplyResult<SongParseTextPayload>().Warnings.Concat(parse.ReplyResult<SongParseTextPayload>().Errors)));   // avisos del parser

        Assert.NotEmpty(_dispatcherLog.Entries);
        Assert.All(everything, text => Assert.DoesNotContain("Xilofon", text));
        Assert.All(everything, text => Assert.DoesNotContain("Quimérica", text));
    }
}

internal static class SongPayloadExtensions
{
    /// <summary>Un guardado que actualiza la canción tal cual está, cambiando el título.</summary>
    public static SongSavePayload ToUpdate(this SongPayload song, string title) =>
        new(song.Id, song.Revision, "Otro " + title, song.Author, song.Copyright, song.CcliNumber, song.Notes, song.Background, song.Sections, song.Arrangement, null);
}
