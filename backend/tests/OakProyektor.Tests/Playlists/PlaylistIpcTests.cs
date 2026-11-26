using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OakProyektor.App;
using OakProyektor.App.Handlers;
using OakProyektor.Core.Output;
using OakProyektor.Core.Playlists;
using OakProyektor.Core.Songs;
using OakProyektor.Data;
using OakProyektor.Data.Playlists;
using OakProyektor.Data.Songs;
using OakProyektor.Ipc;
using OakProyektor.Ipc.Messages;
using OakProyektor.Tests.App;
using OakProyektor.Tests.Data;
using OakProyektor.Tests.Ipc;
using OakProyektor.Tests.Songs;

namespace OakProyektor.Tests.Playlists;

/// <summary>
/// Los mensajes playlist.* de extremo a extremo (despachador + handlers reales + BD real) y el origen de los slides: respuestas tipadas, errores con código, eventos que llevan solo
/// ids y conteos, y que proyectar con un origen NO depende de la base de datos. Todo con datos sintéticos.
/// </summary>
public sealed class PlaylistIpcTests : IAsyncLifetime
{
    private readonly TestDatabase _db = new();
    private readonly ListLogger<IpcDispatcher> _dispatcherLog = new();
    private readonly Outbox _outbox = new(NullLogger<Outbox>.Instance);
    private readonly InMemorySettings _settings = new();
    private readonly FakeOutputController _output = new();
    private IpcDispatcher _dispatcher = null!;
    private SongRepository _songs = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        var playlists = new PlaylistRepository(_db.Connections, new AlwaysReady(), null, new PlaylistRepositoryOptions { WriteTimeoutSeconds = 1 });
        _songs = new SongRepository(_db.Connections, new AlwaysReady(), null, new SongRepositoryOptions { WriteTimeoutSeconds = 1 });
        IIpcHandler[] handlers =
        [
            new PlaylistListHandler(playlists), new PlaylistGetHandler(playlists, _settings), new PlaylistGetSettingsHandler(_settings), new PlaylistSetSettingsHandler(_settings),
            new PlaylistCreateHandler(playlists, _outbox), new PlaylistUpdateHandler(playlists, _outbox), new PlaylistAddItemHandler(playlists, _outbox),
            new PlaylistMoveItemHandler(playlists, _outbox), new PlaylistRemoveItemHandler(playlists, _outbox), new PlaylistUpdateItemHandler(playlists, _outbox),
            new PlaylistDuplicateHandler(playlists, _outbox), new PlaylistDeleteHandler(playlists, _outbox),
            new SongGetHandler(_songs), new SongDeleteHandler(_songs, _outbox),
            new OutputShowContentHandler(_output),
        ];
        _dispatcher = new IpcDispatcher(handlers, _dispatcherLog);
    }

    public Task DisposeAsync() { _db.Dispose(); return Task.CompletedTask; }

    private static string Msg(string kind, string type, object payload, string id) =>
        $$"""{"v":{{IpcCatalog.ProtocolVersion}},"id":"{{id}}","kind":"{{kind}}","type":"{{type}}","payload":{{JsonSerializer.Serialize(payload, IpcSerializer.Options)}}}""";

    private async Task<IpcEnvelope> QueryAsync(string type, object payload) => (await _dispatcher.DispatchAsync(Msg(IpcKind.Query, type, payload, "q1"), CancellationToken.None))!;
    private Task<IpcEnvelope?> CommandAsync(string type, object payload, string id = "c1") => _dispatcher.DispatchAsync(Msg(IpcKind.Command, type, payload, id), CancellationToken.None);

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

    private async Task<PlaylistChangedPayload> ChangedAsync() => Assert.Single(await PublishedAsync(), e => e.Type == IpcTypes.PlaylistChanged).GetPayload<PlaylistChangedPayload>();

    private static PlaylistNewItemDto SongItem(long id) => new("song", id, null, null, null, null, null, null, null, false);
    private static PlaylistNewItemDto NoteItem(string title, string? note = null, bool clear = false) => new("note", null, null, null, null, null, null, title, note, clear);
    private static PlaylistNewItemDto PassageItem() => new("passage", null, 43, 3, 16, 17, "TST", null, null, false);

    private async Task<long> CreateListAsync(string name = "Domingo sintético", string? date = "2026-11-29")
    {
        Assert.Null(await CommandAsync(IpcTypes.PlaylistCreate, new PlaylistCreatePayload(name, date, null)));
        return (await ChangedAsync()).PlaylistId;
    }

    private async Task<long> AddAsync(long list, PlaylistNewItemDto item, int? position = null)
    {
        Assert.Null(await CommandAsync(IpcTypes.PlaylistAddItem, new PlaylistAddItemPayload(list, item, position)));
        return (await ChangedAsync()).ItemId!.Value;
    }

    private async Task<PlaylistPayload> GetAsync(long id) => (await QueryAsync(IpcTypes.PlaylistGet, new PlaylistGetRequest(id))).ReplyResult<PlaylistPayload>();

    private async Task<long> NewSongAsync(string title) =>
        (await _songs.SaveAsync(new SongSaveRequest(null, null, title, null, null, null, null, null, [new(null, "Verso 1", null, SyntheticSong.Text(1))], [0]), default)).SongId;

    // ───────────────────────────── comandos, eventos y respuestas ─────────────────────────────

    [Fact]
    public async Task Creating_publishes_playlist_changed_with_the_command_id_and_only_ids_and_counts()
    {
        Assert.Null(await CommandAsync(IpcTypes.PlaylistCreate, new PlaylistCreatePayload("Domingo " + SyntheticSong.Sentinel, "2026-11-29", "notas " + SyntheticSong.Sentinel), id: "cmd-1"));

        var events = await PublishedAsync();
        var raw = JsonSerializer.Serialize(events, IpcSerializer.Options);
        Assert.DoesNotContain("Xilofon", raw);                                          // el evento no repite nombre ni notas
        var changed = Assert.Single(events, e => e.Type == IpcTypes.PlaylistChanged).GetPayload<PlaylistChangedPayload>();
        Assert.Equal(("cmd-1", "created", 1, (long?)null, (long?)null, 0, 0), (changed.CommandId, changed.Kind, changed.Revision, changed.ItemId, changed.SourceId, changed.Copied, changed.Skipped));

        var list = await GetAsync(changed.PlaylistId);
        Assert.Equal(("Domingo " + SyntheticSong.Sentinel, "2026-11-29", 1), (list.Name, list.ServiceDate, list.Revision));
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", list.CreatedAt);
        var summaries = (await QueryAsync(IpcTypes.PlaylistList, new { })).ReplyResult<PlaylistListPayload>();
        Assert.Equal((false, 1), (summaries.Truncated, summaries.Playlists.Count));
        Assert.Equal((changed.PlaylistId, 0, 0), (summaries.Playlists[0].Id, summaries.Playlists[0].ItemCount, summaries.Playlists[0].MissingCount));
    }

    [Fact]
    public async Task Items_of_every_kind_come_back_typed_in_order_and_each_change_raises_the_revision()
    {
        var song = await NewSongAsync("Canción sintética");
        var list = await CreateListAsync();
        var s = await AddAsync(list, SongItem(song));
        var p = await AddAsync(list, PassageItem());
        var n = await AddAsync(list, NoteItem("Ofrendas", "Pasan los diáconos", clear: true), position: 1);

        var detail = await GetAsync(list);
        Assert.Equal([s, n, p], detail.Items.Select(i => i.Id));
        Assert.Equal(["song", "note", "passage"], detail.Items.Select(i => i.ItemType));
        Assert.Equal(4, detail.Revision);
        var note = detail.Items[1];
        Assert.Equal(("Ofrendas", "Pasan los diáconos", true, false, (string?)null, false), (note.Title, note.Note, note.ClearOnActivate, note.IsMissing, note.Resolution, note.Projectable));
        var passage = detail.Items[2];
        Assert.Equal((43, 3, 16, 17, "TST", PlaylistResolutions.BibleNotInstalled, false), (passage.BookNumber!.Value, passage.Chapter!.Value, passage.VerseStart!.Value, passage.VerseEnd!.Value, passage.BibleAbbreviation, passage.Resolution, passage.Projectable));
        Assert.Equal(("Canción sintética", true), (detail.Items[0].Title, detail.Items[0].Projectable));

        Assert.Null(await CommandAsync(IpcTypes.PlaylistMoveItem, new PlaylistMoveItemPayload(list, p, 0, detail.Revision)));
        Assert.Equal(("itemMoved", p, 5), ((await ChangedAsync()) is { } m ? (m.Kind, m.ItemId!.Value, m.Revision) : default));
        Assert.Null(await CommandAsync(IpcTypes.PlaylistUpdateItem, new PlaylistUpdateItemPayload(list, n, "Ofrenda", "", false, null, 5)));
        Assert.Equal(("itemUpdated", 6), ((await ChangedAsync()) is { } u ? (u.Kind, u.Revision) : default));
        Assert.Null(await CommandAsync(IpcTypes.PlaylistRemoveItem, new PlaylistRemoveItemPayload(list, s, 6)));
        Assert.Equal(("itemRemoved", 7), ((await ChangedAsync()) is { } r ? (r.Kind, r.Revision) : default));
        Assert.Equal([p, n], (await GetAsync(list)).Items.Select(i => i.Id));
    }

    [Fact]
    public async Task The_same_song_twice_over_ipc_gives_two_item_ids_and_removing_one_leaves_the_other()
    {
        var song = await NewSongAsync("Canción sintética repetida");
        var list = await CreateListAsync();
        var first = await AddAsync(list, SongItem(song));
        var second = await AddAsync(list, SongItem(song));
        Assert.NotEqual(first, second);

        var detail = await GetAsync(list);
        Assert.Equal([first, second], detail.Items.Select(i => i.Id));
        Assert.All(detail.Items, i => Assert.Equal(song, i.SongId));

        Assert.Null(await CommandAsync(IpcTypes.PlaylistRemoveItem, new PlaylistRemoveItemPayload(list, first, detail.Revision)));
        Assert.Equal([second], (await GetAsync(list)).Items.Select(i => i.Id));
    }

    [Fact]
    public async Task Failures_come_back_as_command_failed_with_a_code_and_publish_no_event()
    {
        var list = await CreateListAsync();
        var item = await AddAsync(list, NoteItem("A"));
        var revision = (await GetAsync(list)).Revision;
        await AddAsync(list, NoteItem("B"));                                              // la revisión sube: la de arriba queda caducada

        async Task<(string Code, string Type)> FailureOf(string type, object payload)
        {
            var reply = await CommandAsync(type, payload, id: "bad");
            var failed = reply!.GetPayload<CommandFailedPayload>();
            Assert.Equal(("bad", type), (failed.CommandId, failed.CommandType));
            return (failed.Error.Code, failed.CommandType);
        }

        Assert.Equal(PlaylistErrorCodes.Conflict, (await FailureOf(IpcTypes.PlaylistRemoveItem, new PlaylistRemoveItemPayload(list, item, revision))).Code);
        Assert.Equal(PlaylistErrorCodes.Conflict, (await FailureOf(IpcTypes.PlaylistMoveItem, new PlaylistMoveItemPayload(list, item, 1, revision))).Code);
        Assert.Equal(PlaylistErrorCodes.Conflict, (await FailureOf(IpcTypes.PlaylistUpdate, new PlaylistUpdatePayload(list, "Otro", null, null, revision))).Code);
        Assert.Equal(PlaylistErrorCodes.NotFound, (await FailureOf(IpcTypes.PlaylistAddItem, new PlaylistAddItemPayload(9999, NoteItem("x"), null))).Code);
        Assert.Equal(PlaylistErrorCodes.NotFound, (await FailureOf(IpcTypes.PlaylistDelete, new PlaylistDeletePayload(9999, 1))).Code);
        Assert.Equal(PlaylistErrorCodes.NotFound, (await FailureOf(IpcTypes.PlaylistDuplicate, new PlaylistDuplicatePayload(9999, "x", null))).Code);
        Assert.Equal(PlaylistErrorCodes.InvalidRequest, (await FailureOf(IpcTypes.PlaylistCreate, new PlaylistCreatePayload("  ", null, null))).Code);
        Assert.Equal(PlaylistErrorCodes.InvalidRequest, (await FailureOf(IpcTypes.PlaylistCreate, new PlaylistCreatePayload("ok", "31/12/2026", null))).Code);
        Assert.Equal(PlaylistErrorCodes.InvalidRequest, (await FailureOf(IpcTypes.PlaylistAddItem, new PlaylistAddItemPayload(list, new("cosa", null, null, null, null, null, null, null, null, false), null))).Code);
        Assert.Equal(PlaylistErrorCodes.InvalidRequest, (await FailureOf(IpcTypes.PlaylistAddItem, new PlaylistAddItemPayload(list, new("presentation", null, null, null, null, null, null, "x", null, false), null))).Code);
        Assert.Equal(PlaylistErrorCodes.InvalidRequest, (await FailureOf(IpcTypes.PlaylistAddItem, new PlaylistAddItemPayload(list, SongItem(99999), null))).Code);   // canción que no existe
        Assert.Equal(PlaylistErrorCodes.ItemNotFound, (await FailureOf(IpcTypes.PlaylistRemoveItem, new PlaylistRemoveItemPayload(list, 99999, (await GetAsync(list)).Revision))).Code);

        Assert.Empty(await PublishedAsync());                                              // un fallo no publica playlist.changed
        Assert.Equal(2, (await GetAsync(list)).Items.Count);                               // y no cambió nada

        var missing = await QueryAsync(IpcTypes.PlaylistGet, new PlaylistGetRequest(9999));
        Assert.Equal(PlaylistErrorCodes.NotFound, missing.ReplyError().Code);
    }

    [Fact]
    public async Task Duplicate_reports_how_many_were_copied_and_skipped_and_delete_reports_zero_revision()
    {
        var kept = await NewSongAsync("Canción sintética A");
        var doomed = await NewSongAsync("Canción sintética B");
        var source = await CreateListAsync("Domingo pasado", "2026-11-22");
        await AddAsync(source, SongItem(kept));
        await AddAsync(source, SongItem(doomed));
        await AddAsync(source, NoteItem("Anuncios"));
        await _songs.DeleteAsync(doomed, default);

        Assert.Null(await CommandAsync(IpcTypes.PlaylistDuplicate, new PlaylistDuplicatePayload(source, "Domingo 29", "2026-11-29"), id: "dup"));
        var dup = await ChangedAsync();
        Assert.Equal(("dup", "duplicated", source, 2, 1, 1), (dup.CommandId, dup.Kind, dup.SourceId!.Value, dup.Copied, dup.Skipped, dup.Revision));
        Assert.NotEqual(source, dup.PlaylistId);
        Assert.Equal(2, (await GetAsync(dup.PlaylistId)).Items.Count);
        Assert.Equal(3, (await GetAsync(source)).Items.Count);                              // el origen conserva su faltante

        Assert.Null(await CommandAsync(IpcTypes.PlaylistDelete, new PlaylistDeletePayload(dup.PlaylistId, dup.Revision), id: "del"));
        var del = await ChangedAsync();
        Assert.Equal(("del", "deleted", 0), (del.CommandId, del.Kind, del.Revision));
    }

    // ───────────────────────────── borrado de canciones: el aviso nombra las listas ─────────────────────────────

    [Fact]
    public async Task Deleting_a_song_names_the_lists_in_the_event_and_song_get_names_them_before()
    {
        var song = await NewSongAsync("Canción sintética");
        var sunday = await CreateListAsync("Domingo sintético", "2026-11-29");
        var wednesday = await CreateListAsync("Miércoles sintético", "2026-11-25");
        await AddAsync(sunday, SongItem(song));
        await AddAsync(wednesday, SongItem(song));

        var before = (await QueryAsync(IpcTypes.SongGet, new SongGetRequest(song))).ReplyResult<SongPayload>();
        Assert.Equal(2, before.UsedInServices);
        Assert.Equal(["Domingo sintético", "Miércoles sintético"], before.UsedInLists.Select(l => l.Name));
        Assert.Equal([sunday, wednesday], before.UsedInLists.Select(l => l.Id));
        Assert.Equal(["2026-11-29", "2026-11-25"], before.UsedInLists.Select(l => l.ServiceDate));

        Assert.Null(await CommandAsync(IpcTypes.SongDelete, new SongDeletePayload(song), id: "del-song"));
        var changed = Assert.Single(await PublishedAsync(), e => e.Type == IpcTypes.SongChanged).GetPayload<SongChangedPayload>();
        Assert.Equal(("del-song", "deleted", 0, 2), (changed.CommandId, changed.Kind, changed.Revision, changed.UsedInServices));
        Assert.Equal(["Domingo sintético", "Miércoles sintético"], changed.UsedInLists.Select(l => l.Name));
        Assert.Equal([sunday, wednesday], changed.UsedInLists.Select(l => l.Id));

        // Los elementos siguen en las listas, como «Falta: …».
        var detail = await GetAsync(sunday);
        var gone = Assert.Single(detail.Items);
        Assert.Equal(("Canción sintética", true, false), (gone.Title, gone.IsMissing, gone.Projectable));
        Assert.Equal(1, detail.MissingCount);
    }

    // ───────────────────────────── ajustes ─────────────────────────────

    [Fact]
    public async Task The_end_of_item_setting_defaults_to_stay_and_an_invalid_value_changes_nothing()
    {
        Assert.Equal(PlaylistOnItemEnd.Stay, (await QueryAsync(IpcTypes.PlaylistGetSettings, new { })).ReplyResult<PlaylistSettingsPayload>().OnItemEnd);

        Assert.Null(await CommandAsync(IpcTypes.PlaylistSetSettings, new PlaylistSettingsPayload(PlaylistOnItemEnd.Next)));
        Assert.Equal(PlaylistOnItemEnd.Next, (await QueryAsync(IpcTypes.PlaylistGetSettings, new { })).ReplyResult<PlaylistSettingsPayload>().OnItemEnd);
        Assert.Equal(PlaylistOnItemEnd.Next, _settings.Current.Playlists.OnItemEnd);

        // Un valor desconocido o numérico ni siquiera se interpreta; lo guardado sigue igual.
        var bad = await _dispatcher.DispatchAsync("""{"v":15,"id":"s","kind":"command","type":"playlist.setSettings","payload":{"onItemEnd":"explotar"}}""", CancellationToken.None);
        Assert.Equal(IpcTypes.CommandFailed, bad!.Type);
        var numeric = await _dispatcher.DispatchAsync("""{"v":15,"id":"s2","kind":"command","type":"playlist.setSettings","payload":{"onItemEnd":99}}""", CancellationToken.None);
        Assert.Equal(IpcTypes.CommandFailed, numeric!.Type);
        Assert.Equal(PlaylistOnItemEnd.Next, _settings.Current.Playlists.OnItemEnd);
    }

    // ───────────────────────────── el origen del slide ─────────────────────────────

    private const string Theme = """{"fontFamily":"Arial","fontSize":72,"color":"#FFFFFF","outlineWidth":0,"outlineColor":"#000000","shadowBlur":0,"shadowOffset":0,"shadowColor":"#000000","align":"center","verticalAlign":"middle","lineHeight":1.2,"marginTop":50,"marginRight":50,"marginBottom":50,"marginLeft":50,"autoFit":true,"minFontSize":24}""";

    private Task<IpcEnvelope?> ShowAsync(string? originJson, string id = "show") =>
        _dispatcher.DispatchAsync("{\"v\":15,\"id\":\"" + id + "\",\"kind\":\"command\",\"type\":\"output.showContent\",\"payload\":{\"blocks\":[\"texto sintético\"],\"theme\":" + Theme + ",\"transitionMs\":400"
            + (originJson is null ? "" : ",\"origin\":" + originJson) + "}}", CancellationToken.None);

    [Fact]
    public async Task A_slide_from_a_list_carries_its_origin_to_the_output_and_one_that_is_not_carries_null()
    {
        Assert.Null(await ShowAsync("""{"playlistId":3,"itemId":9,"itemType":"song","position":2}"""));
        Assert.Equal(new SlideOrigin(3, 9, "song", 2), _output.LastOrigin);

        Assert.Null(await ShowAsync(null));                                  // proyección suelta (Biblia o Canciones): sin origen
        Assert.Null(_output.LastOrigin);
        Assert.Null(await ShowAsync("null"));
        Assert.Null(_output.LastOrigin);
        Assert.Equal(3, _output.Calls.Count);
    }

    [Fact]
    public async Task A_malformed_origin_is_rejected_as_invalid_content_and_never_reaches_the_output()
    {
        foreach (var bad in new[]
        {
            """{"playlistId":0,"itemId":9,"itemType":"song","position":0}""",
            """{"playlistId":3,"itemId":-1,"itemType":"song","position":0}""",
            """{"playlistId":3,"itemId":9,"itemType":"note","position":0}""",
            """{"playlistId":3,"itemId":9,"itemType":"song","position":-4}""",
            """{"playlistId":3,"itemId":9,"itemType":"song","position":100000}""",
        })
        {
            var reply = await ShowAsync(bad, id: "bad");
            Assert.Equal("output.invalidContent", reply!.GetPayload<CommandFailedPayload>().Error.Code);
        }
        Assert.Empty(_output.Calls);
    }

    [Fact]
    public async Task An_origin_pointing_at_a_list_or_item_that_does_not_exist_is_still_projected_because_projecting_never_depends_on_the_database()
    {
        // La lista 777 y el elemento 888 no existen en la BD: el handler de showContent no consulta nada (el origen son solo ids para que el panel derive el elemento activo).
        Assert.Null(await ShowAsync("""{"playlistId":777,"itemId":888,"itemType":"passage","position":0}"""));
        Assert.Equal(new SlideOrigin(777, 888, "passage", 0), _output.LastOrigin);
        Assert.Equal(["showContent:1:400"], _output.Calls);

        // La lista, en cambio, no existe para quien la pida: no hay nada con lo que el panel pueda casar ese origen.
        Assert.Equal(PlaylistErrorCodes.NotFound, (await QueryAsync(IpcTypes.PlaylistGet, new PlaylistGetRequest(777))).ReplyError().Code);
        var list = await CreateListAsync();
        Assert.DoesNotContain(777L, (await GetAsync(list)).Items.Select(i => i.Id));
        Assert.DoesNotContain(888L, (await GetAsync(list)).Items.Select(i => i.Id));
    }

    [Fact]
    public async Task A_content_payload_from_an_older_panel_without_origin_still_works_and_the_state_reports_schema_3()
    {
        // Un v13 sin «origin» (p. ej. los scripts de tools/) no es un error: el campo es opcional.
        Assert.Null(await ShowAsync(null));
        Assert.Null(_output.LastOrigin);
        Assert.Equal(3, OutputState.CurrentSchemaVersion);   // 2 desde la v13 (origin); 3 desde la v15 (overlays = { alert })
    }
}
