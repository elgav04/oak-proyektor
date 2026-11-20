using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OakProyektor.App;
using OakProyektor.App.Handlers;
using OakProyektor.Core.Scripture;
using OakProyektor.Data.Scripture;
using OakProyektor.Ipc;
using OakProyektor.Ipc.Messages;
using OakProyektor.Tests.Ipc;

namespace OakProyektor.Tests.Scripture;

public sealed class BibleIpcTests : IAsyncLifetime
{
    private BibleEnv _env = null!;
    private IpcDispatcher _dispatcher = null!;
    private Outbox _outbox = null!;
    private BibleImportService _imports = null!;

    public async Task InitializeAsync()
    {
        _env = await BibleEnv.CreateAsync();
        _outbox = new Outbox(NullLogger<Outbox>.Instance);
        _imports = new BibleImportService(_env.Importer, _outbox, NullLogger<BibleImportService>.Instance);
        IIpcHandler[] handlers =
        [
            new BibleListHandler(_env.Query), new BibleLookupHandler(_env.Query), new BibleChapterHandler(_env.Query), new BibleSearchHandler(_env.Query),
            new BibleHistoryHandler(_env.Query), new BibleSuggestHandler(), new BibleSetAttributionHandler(_env.Query), new BibleImportHandler(_imports),
        ];
        _dispatcher = new IpcDispatcher(handlers, NullLogger<IpcDispatcher>.Instance);
    }

    public Task DisposeAsync() { _imports.Dispose(); _env.Dispose(); return Task.CompletedTask; }

    private static string Msg(string kind, string type, object payload) =>
        $$"""{"v":{{IpcCatalog.ProtocolVersion}},"id":"q1","kind":"{{kind}}","type":"{{type}}","payload":{{JsonSerializer.Serialize(payload, IpcSerializer.Options)}}}""";

    private async Task<IpcEnvelope> QueryAsync(string type, object payload) =>
        (await _dispatcher.DispatchAsync(Msg(IpcKind.Query, type, payload), CancellationToken.None))!;

    private async Task ImportSmallAsync(string abbreviation = "SIN")
    {
        var path = SyntheticBible.WriteYouVersion(_env.FilesDirectory, SyntheticBible.Small(), fileName: abbreviation + ".json");
        await _env.Importer.ImportAsync(new OakProyektor.Data.Scripture.BibleImportRequest(path, abbreviation, "Biblia sintética", "es"), null, CancellationToken.None);
    }

    /// <summary>Lee la salida hasta encontrar un evento del tipo pedido (con un tope de tiempo).</summary>
    private async Task<List<IpcEnvelope>> EventsUntilAsync(string finalType)
    {
        var events = new List<IpcEnvelope>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await foreach (var (_, json) in _outbox.ReadAllAsync(cts.Token))
        {
            Assert.True(IpcSerializer.TryParse(json, out var envelope, out var error), error);
            events.Add(envelope!);
            if (envelope!.Type == finalType) break;
        }
        return events;
    }

    // ───────────────────────────── consultas ─────────────────────────────

    [Fact]
    public async Task List_lookup_chapter_search_history_and_suggest_answer_with_typed_replies()
    {
        await ImportSmallAsync();

        var list = (await QueryAsync(IpcTypes.BibleList, new EmptyPayload())).ReplyResult<BibleListPayload>();
        Assert.Equal(("SIN", SyntheticBible.Small().Count), (list.Bibles.Single().Abbreviation, list.Bibles.Single().Verses));

        var lookup = (await QueryAsync(IpcTypes.BibleLookup, new BibleLookupRequest("Mt 5:3-4", "SIN", true))).ReplyResult<BiblePassagePayload>();
        Assert.Equal(("Mt 5:3-4", 2), (lookup.Reference, lookup.Verses.Count));
        Assert.Equal(SyntheticBible.TextOf(40, 5, 3), lookup.Verses[0].Text);

        var chapter = (await QueryAsync(IpcTypes.BibleChapter, new BibleChapterRequest(null, 1, 1))).ReplyResult<BiblePassagePayload>();
        Assert.Equal(5, chapter.Verses.Count);

        var search = (await QueryAsync(IpcTypes.BibleSearch, new BibleSearchRequest(SyntheticBible.TextOf(40, 5, 3).Split(' ')[0], null, 10))).ReplyResult<BibleSearchPayload>();
        Assert.Equal((1, 40), (search.Hits.Count, search.Hits[0].BookNumber));
        Assert.Contains("«", search.Hits[0].Fragment);

        var history = (await QueryAsync(IpcTypes.BibleHistory, new BibleHistoryRequest(0))).ReplyResult<BibleHistoryPayload>();
        Assert.Equal(["Mt 5:3-4"], history.Entries.Select(e => e.Label));

        var suggest = (await QueryAsync(IpcTypes.BibleSuggest, new BibleSuggestRequest("1 c", 5))).ReplyResult<BibleSuggestPayload>();
        Assert.Contains(46, suggest.Suggestions.Select(s => s.BookNumber));
    }

    [Fact]
    public async Task Failures_come_back_as_replies_with_a_stable_code_and_a_readable_message()
    {
        await ImportSmallAsync();

        async Task<IpcError> ErrorOf(string type, object payload)
        {
            var reply = await QueryAsync(type, payload);
            Assert.False(reply.GetPayload<IpcReply>().Ok);
            return reply.ReplyError();
        }

        Assert.Equal(BibleErrorCodes.InvalidReference, (await ErrorOf(IpcTypes.BibleLookup, new BibleLookupRequest("Xyz 1:1", null, false))).Code);
        Assert.Equal(BibleErrorCodes.NotFound, (await ErrorOf(IpcTypes.BibleLookup, new BibleLookupRequest("Mt 5:3", "NOEXISTE", false))).Code);
        Assert.Equal(BibleErrorCodes.PassageNotFound, (await ErrorOf(IpcTypes.BibleLookup, new BibleLookupRequest("Jn 3:16", "SIN", false))).Code);
        Assert.Equal(BibleErrorCodes.EmptyQuery, (await ErrorOf(IpcTypes.BibleSearch, new BibleSearchRequest("  ", null, 10))).Code);
        Assert.Equal(BibleErrorCodes.InvalidRequest, (await ErrorOf(IpcTypes.BibleChapter, new BibleChapterRequest(null, 99, 1))).Code);
        var message = (await ErrorOf(IpcTypes.BibleLookup, new BibleLookupRequest("Jo 1:1", null, false))).Message;
        Assert.Contains("ambiguo", message);
    }

    [Fact]
    public async Task Setting_the_attribution_is_a_command_and_the_list_and_passages_show_it()
    {
        await ImportSmallAsync();

        var response = await _dispatcher.DispatchAsync(Msg(IpcKind.Command, IpcTypes.BibleSetAttribution, new BibleSetAttributionPayload("SIN", "Aviso inventado")), CancellationToken.None);

        Assert.Null(response);                                                                     // un comando exitoso no responde
        Assert.Equal("Aviso inventado", (await QueryAsync(IpcTypes.BibleList, new EmptyPayload())).ReplyResult<BibleListPayload>().Bibles.Single().Attribution);
        Assert.Equal("Aviso inventado", (await QueryAsync(IpcTypes.BibleLookup, new BibleLookupRequest("Mt 5:3", null, false))).ReplyResult<BiblePassagePayload>().Attribution);

        var failed = await _dispatcher.DispatchAsync(Msg(IpcKind.Command, IpcTypes.BibleSetAttribution, new BibleSetAttributionPayload("NOEXISTE", "x")), CancellationToken.None);
        Assert.Equal(IpcTypes.CommandFailed, failed!.Type);
        Assert.Equal(BibleErrorCodes.NotFound, failed.GetPayload<CommandFailedPayload>().Error.Code);
    }

    // ───────────────────────────── importación ─────────────────────────────

    [Fact]
    public async Task Import_returns_at_once_then_publishes_progress_and_a_final_report_with_no_text()
    {
        var path = SyntheticBible.WriteYouVersion(_env.FilesDirectory, SyntheticBible.Small());

        var response = await _dispatcher.DispatchAsync(Msg(IpcKind.Command, IpcTypes.BibleImport,
            new BibleImportPayload(path, "SIN", "Biblia sintética", "es", "merge-ps47-3jn", "Aviso inventado")), CancellationToken.None);
        Assert.Null(response);                                                                     // el comando volvió: el trabajo sigue en otro hilo

        var events = await EventsUntilAsync(IpcTypes.BibleImportFinished);

        var progress = events.Where(e => e.Type == IpcTypes.BibleImportProgress).Select(e => e.GetPayload<BibleImportProgressPayload>()).ToList();
        Assert.Equal(["reading", "cleaning", "saving", "validating"], progress.Select(p => p.Phase).Distinct());
        Assert.All(progress, p => Assert.Equal("SIN", p.Abbreviation));
        var finished = events[^1].GetPayload<BibleImportFinishedPayload>();
        Assert.True(finished.Ok);
        Assert.Null(finished.ErrorCode);
        var report = finished.Report!;
        Assert.Equal((true, "merge-ps47-3jn", SyntheticBible.Small().Count - 2, 5, 7), (report.Created, report.NumberingProfile, report.SourceVerses, report.Books, report.Chapters));
        Assert.True(report.FtsIntegrityOk);

        var raw = string.Join("\n", events.Select(IpcSerializer.Serialize));
        foreach (var banned in new[] { "Sintetico", "lorem", "ipsum", "dolor", "Titulo", "HTML" })
            Assert.DoesNotContain(banned, raw, StringComparison.OrdinalIgnoreCase);                  // ni progreso ni informe llevan texto

        Assert.Equal("Aviso inventado", (await QueryAsync(IpcTypes.BibleList, new EmptyPayload())).ReplyResult<BibleListPayload>().Bibles.Single().Attribution);
    }

    [Fact]
    public async Task A_second_import_while_one_runs_is_refused_with_importBusy()
    {
        var path = SyntheticBible.WriteYouVersion(_env.FilesDirectory, SyntheticBible.Small());
        var payload = new BibleImportPayload(path, "SIN", "N", "es", null, null);

        Assert.Null(await _dispatcher.DispatchAsync(Msg(IpcKind.Command, IpcTypes.BibleImport, payload), CancellationToken.None));
        var busy = await _dispatcher.DispatchAsync(Msg(IpcKind.Command, IpcTypes.BibleImport, payload), CancellationToken.None);

        Assert.Equal(IpcTypes.CommandFailed, busy!.Type);
        Assert.Equal(BibleErrorCodes.ImportBusy, busy.GetPayload<CommandFailedPayload>().Error.Code);
        await EventsUntilAsync(IpcTypes.BibleImportFinished);                                      // la primera termina bien

        // Y después se puede volver a importar.
        Assert.Null(await _dispatcher.DispatchAsync(Msg(IpcKind.Command, IpcTypes.BibleImport, payload), CancellationToken.None));
        var second = await EventsUntilAsync(IpcTypes.BibleImportFinished);
        Assert.True(second[^1].GetPayload<BibleImportFinishedPayload>().Ok);
    }

    [Fact]
    public async Task A_failed_import_is_reported_in_the_finished_event_with_its_code()
    {
        Assert.Null(await _dispatcher.DispatchAsync(Msg(IpcKind.Command, IpcTypes.BibleImport,
            new BibleImportPayload(Path.Combine(_env.FilesDirectory, "no-existe.json"), "SIN", "N", "es", null, null)), CancellationToken.None));

        var finished = (await EventsUntilAsync(IpcTypes.BibleImportFinished))[^1].GetPayload<BibleImportFinishedPayload>();

        Assert.False(finished.Ok);
        Assert.Equal(BibleErrorCodes.FileMissing, finished.ErrorCode);
        Assert.Null(finished.Report);
        Assert.False(string.IsNullOrWhiteSpace(finished.ErrorMessage));
    }
}
