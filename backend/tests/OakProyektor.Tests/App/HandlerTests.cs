using Microsoft.Extensions.Logging.Abstractions;
using OakProyektor.App;
using OakProyektor.App.Handlers;
using OakProyektor.Core.Display;
using OakProyektor.Core.Output;
using OakProyektor.Ipc;
using OakProyektor.Ipc.Messages;
using OakProyektor.Tests.Core;
using OakProyektor.Tests.Ipc;

namespace OakProyektor.Tests.App;

/// <summary>Servicio de consultas sin Biblias instaladas: todo lo que busca devuelve el error «no está instalada».</summary>
internal sealed class NoBibles : OakProyektor.Data.Scripture.IBibleQueryService
{
    private static OakProyektor.Data.Scripture.BibleException None() => new(OakProyektor.Data.Scripture.BibleErrorCodes.NotFound, "No hay ninguna Biblia instalada.");
    public Task<IReadOnlyList<OakProyektor.Data.Scripture.BibleInfo>> ListBiblesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<OakProyektor.Data.Scripture.BibleInfo>>([]);
    public Task<OakProyektor.Data.Scripture.BiblePassage> LookupAsync(string reference, string? bible, bool recordHistory, CancellationToken ct) => throw None();
    public Task<OakProyektor.Data.Scripture.BiblePassage> GetChapterAsync(string? bible, int bookNumber, int chapter, CancellationToken ct) => throw None();
    public Task<OakProyektor.Data.Scripture.BibleSearchResult> SearchAsync(string query, string? bible, int limit, CancellationToken ct) => throw None();
    public Task<IReadOnlyList<OakProyektor.Data.Scripture.CitationEntry>> GetHistoryAsync(int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<OakProyektor.Data.Scripture.CitationEntry>>([]);
    public Task SetAttributionAsync(string bible, string attribution, CancellationToken ct) => throw None();
}

public class HandlerTests
{
    private static string Msg(string kind, string type, string payload = "{}") =>
        $$"""{"v":7,"id":"q1","kind":"{{kind}}","type":"{{type}}","payload":{{payload}}}""";

    private static (IpcDispatcher Dispatcher, FakeOutputController Output) Create(params DisplayInfo[] displays)
    {
        var output = new FakeOutputController();
        IIpcHandler[] handlers =
        [
            new DisplayListHandler(new FakeDisplayManager(displays)),
            new OutputGetStateHandler(output),
            new OutputOpenHandler(output),
            new OutputCloseHandler(output),
            new OutputSetMasterHandler(output),
            new OutputRecoverHandler(output),
            new OutputSetBackgroundHandler(output, new FakeMediaFileSystem()),
            new OutputShowContentHandler(output),
            new OutputClearContentHandler(output),
            new OutputReportBackgroundFailureHandler(output),
            new SettingsSetAutoRestoreHandler(new InMemorySettings(), output),
            new SettingsSetDisplayLostPolicyHandler(new InMemorySettings(), output),
            new SettingsSetPanelAlertOnOutputHandler(new InMemorySettings(), output),
            new DatabaseGetStatusHandler(new DatabaseStatusReporter(new FakeReadiness(), new Outbox(NullLogger<Outbox>.Instance))),
            // Biblias (v6): consultas contra un servicio que no tiene ninguna Biblia; la importación falla en su validación sin abrir la BD.
            new BibleListHandler(new NoBibles()),
            new BibleLookupHandler(new NoBibles()),
            new BibleChapterHandler(new NoBibles()),
            new BibleSearchHandler(new NoBibles()),
            new BibleHistoryHandler(new NoBibles()),
            new BibleSuggestHandler(),
            new BibleSetAttributionHandler(new NoBibles()),
            new BibleImportHandler(new BibleImportService(
                new OakProyektor.Data.Scripture.BibleImporter(
                    new OakProyektor.Data.SqliteConnectionFactory(new OakProyektor.Data.DatabaseOptions(Path.Combine(Path.GetTempPath(), "oak-handler-test-never-opened.db"))),
                    new FakeReadiness(), NullLogger<OakProyektor.Data.Scripture.BibleImporter>.Instance),
                new Outbox(NullLogger<Outbox>.Instance), NullLogger<BibleImportService>.Instance)),
        ];
        return (new IpcDispatcher(handlers, NullLogger<IpcDispatcher>.Instance), output);
    }

    [Fact]
    public async Task Every_inbound_message_in_the_catalog_has_a_handler()
    {
        var (d, output) = Create();
        var unhandled = new List<string>();
        foreach (var m in IpcCatalog.Messages.Where(m => m.Kind is IpcKind.Command or IpcKind.Query))
        {
            var payload = m.Type switch
            {
                IpcTypes.OutputSetMaster => """{"master":"live"}""",
                IpcTypes.OutputSetBackground => """{"kind":"color","path":null,"color":"#112233","muted":true,"transitionMs":300}""",
                IpcTypes.OutputShowContent => """{"blocks":["Hola"],"theme":{"fontFamily":"Arial","fontSize":72,"color":"#FFFFFF","outlineWidth":0,"outlineColor":"#000000","shadowBlur":0,"shadowOffset":0,"shadowColor":"#000000","align":"center","verticalAlign":"middle","lineHeight":1.2,"marginTop":50,"marginRight":50,"marginBottom":50,"marginLeft":50,"autoFit":true,"minFontSize":24},"transitionMs":400}""",
                IpcTypes.OutputClearContent => """{"transitionMs":400}""",
                IpcTypes.OutputReportBackgroundFailure => """{"mediaUrl":"file:///x","reason":"unsupportedFormat","detail":null}""",
                IpcTypes.SettingsSetAutoRestore => """{"enabled":false}""",
                IpcTypes.SettingsSetDisplayLostPolicy => """{"policy":"moveToBest"}""",
                IpcTypes.SettingsSetPanelAlertOnOutput => """{"enabled":true}""",
                _ => "{}",
            };
            var response = await d.DispatchAsync(Msg(m.Kind, m.Type, payload), CancellationToken.None);
            // Un mensaje sin handler ya no se pierde en silencio: el despachador responde unknown_message. Eso es lo que se vigila aquí.
            var error = response is null ? null
                : response.Type == IpcTypes.CommandFailed ? response.GetPayload<CommandFailedPayload>().Error.Code
                : response.GetPayload<IpcReply>().Ok ? null : response.ReplyError().Code;
            if (error == "unknown_message") unhandled.Add($"{m.Kind} {m.Type}");
        }
        Assert.True(unhandled.Count == 0, "Mensajes del catálogo sin handler: " + string.Join(", ", unhandled));
        // Los comandos llegaron a su handler (si faltara uno, el despachador solo lo registraría y no quedaría la llamada).
        Assert.Equal(["open:auto", "close", "master:Live", "pause:Manual", "background:Color", "showContent:1:400", "clearContent:400", "backgroundFailure:UnsupportedFormat:file:///x", "settingsChanged", "settingsChanged", "settingsChanged"], output.Calls);
    }

    [Fact]
    public async Task Display_lost_policy_is_persisted_then_the_output_is_notified()
    {
        var settings = new InMemorySettings();
        var output = new FakeOutputController();
        var d = new IpcDispatcher([new SettingsSetDisplayLostPolicyHandler(settings, output)], NullLogger<IpcDispatcher>.Instance);
        Assert.Equal(DisplayLostPolicy.WarnAndWait, settings.Current.OnDisplayLost);   // por defecto: avisar y esperar

        await d.DispatchAsync(Msg("command", "settings.setDisplayLostPolicy", """{"policy":"moveToBest"}"""), CancellationToken.None);

        Assert.Equal(DisplayLostPolicy.MoveToBest, settings.Current.OnDisplayLost);
        Assert.Equal(["settingsChanged"], output.Calls);
    }

    [Fact]
    public async Task The_panel_alert_on_output_is_off_by_default_and_the_setting_is_persisted()
    {
        var settings = new InMemorySettings();
        var output = new FakeOutputController();
        var d = new IpcDispatcher([new SettingsSetPanelAlertOnOutputHandler(settings, output)], NullLogger<IpcDispatcher>.Instance);
        Assert.False(settings.Current.ShowPanelAlertOnOutput);            // por defecto, la proyección no muestra nada

        await d.DispatchAsync(Msg("command", "settings.setPanelAlertOnOutput", """{"enabled":true}"""), CancellationToken.None);

        Assert.True(settings.Current.ShowPanelAlertOnOutput);
        Assert.Equal(["settingsChanged"], output.Calls);
    }

    [Fact]
    public async Task An_unknown_display_lost_policy_is_rejected_and_changes_nothing()
    {
        var settings = new InMemorySettings();
        var output = new FakeOutputController();
        var d = new IpcDispatcher([new SettingsSetDisplayLostPolicyHandler(settings, output)], NullLogger<IpcDispatcher>.Instance);

        await d.DispatchAsync(Msg("command", "settings.setDisplayLostPolicy", """{"policy":"explotar"}"""), CancellationToken.None);

        Assert.Equal(DisplayLostPolicy.WarnAndWait, settings.Current.OnDisplayLost);
        Assert.Empty(output.Calls);
    }

    [Fact]
    public async Task Auto_restore_setting_is_persisted_then_the_output_is_notified()
    {
        var settings = new InMemorySettings();
        var output = new FakeOutputController();
        var d = new IpcDispatcher([new SettingsSetAutoRestoreHandler(settings, output)], NullLogger<IpcDispatcher>.Instance);

        await d.DispatchAsync(Msg("command", "settings.setAutoRestore", """{"enabled":false}"""), CancellationToken.None);

        Assert.False(settings.Current.AutoRestoreOutput);
        Assert.Equal(["settingsChanged"], output.Calls);
    }

    [Fact]
    public async Task Recover_command_pauses_the_output_as_manual()
    {
        var (d, output) = Create();
        await d.DispatchAsync(Msg("command", "output.recover"), CancellationToken.None);
        Assert.Equal(["pause:Manual"], output.Calls);
    }

    [Theory]
    [InlineData("""{"reason":"emergency"}""", "pause:Emergency")]   // el atajo de respaldo de la página de salida
    [InlineData("""{"reason":"manual"}""", "pause:Manual")]
    [InlineData("""{"reason":null}""", "pause:Manual")]
    [InlineData("""{"reason":"displayLost"}""", "pause:Manual")]    // un cliente no puede pedir una pausa «automática»
    [InlineData("{}", "pause:Manual")]
    public async Task Recover_maps_its_reason_to_manual_or_emergency_only(string payload, string expected)
    {
        var (d, output) = Create();
        await d.DispatchAsync(Msg("command", "output.recover", payload), CancellationToken.None);
        Assert.Equal([expected], output.Calls);
    }

    [Fact]
    public async Task Open_and_recover_pass_the_arrival_number_through_to_the_controller()
    {
        var output = new FakeOutputController();
        var open = IpcEnvelope.Create(IpcKind.Command, IpcTypes.OutputOpen, new OutputOpenPayload(null)) with { Order = 41 };
        var recover = IpcEnvelope.Create(IpcKind.Command, IpcTypes.OutputRecover, new OutputRecoverPayload(OutputPauseReason.Manual)) with { Order = 42 };

        await new OutputOpenHandler(output).HandleAsync(open, CancellationToken.None);
        await new OutputRecoverHandler(output).HandleAsync(recover, CancellationToken.None);

        Assert.Equal([41L, 42L], output.Orders);
    }

    [Fact]
    public void The_arrival_number_never_travels_in_the_json()
    {
        // Id fijo: con un GUID aleatorio, buscar un número en el JSON fallaba al azar cuando el GUID lo contenía.
        var env = IpcEnvelope.Create(IpcKind.Command, IpcTypes.OutputOpen, new OutputOpenPayload(null), id: "fijo") with { Order = 987654321 };
        using var doc = System.Text.Json.JsonDocument.Parse(IpcSerializer.Serialize(env));
        Assert.Equal(["v", "id", "kind", "type", "payload"], doc.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.DoesNotContain("987654321", doc.RootElement.GetRawText());
    }

    [Fact]
    public async Task Open_passes_the_requested_display_index_or_auto()
    {
        var (d, output) = Create();
        await d.DispatchAsync(Msg("command", "output.open", """{"displayId":"1920,0,1920x1080"}"""), CancellationToken.None);
        await d.DispatchAsync(Msg("command", "output.open", """{"displayId":null}"""), CancellationToken.None);
        await d.DispatchAsync(Msg("command", "output.open"), CancellationToken.None);
        Assert.Equal(["open:1920,0,1920x1080", "open:auto", "open:auto"], output.Calls);
    }

    [Fact]
    public async Task Display_list_query_replies_with_the_monitors()
    {
        var (d, _) = Create(new DisplayInfo(DisplayId.From(0, 0, 1920, 1080), 0, 0, 1920, 1080, 1.25, true, true), new DisplayInfo(DisplayId.From(1920, 0, 1920, 1080), 1920, 0, 1920, 1080, 1, false, false));

        var reply = await d.DispatchAsync(Msg("query", "display.list"), CancellationToken.None);

        Assert.NotNull(reply);
        Assert.Equal(("q1", "reply"), (reply.Id, reply.Kind));
        Assert.True(reply.GetPayload<IpcReply>().Ok);
        Assert.Equal(2, reply.ReplyResult<DisplayListPayload>().Displays.Count);
    }

    [Fact]
    public async Task Get_state_query_replies_with_the_snapshot()
    {
        var (d, _) = Create();
        var reply = await d.DispatchAsync(Msg("query", "output.getState"), CancellationToken.None);
        Assert.Equal(MasterMode.Black, reply!.ReplyResult<OutputStateChangedPayload>().State.Master);
    }
}




