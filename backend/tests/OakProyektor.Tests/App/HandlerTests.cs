using Microsoft.Extensions.Logging.Abstractions;
using OakProyektor.App.Handlers;
using OakProyektor.Core.Display;
using OakProyektor.Core.Output;
using OakProyektor.Ipc;
using OakProyektor.Ipc.Messages;

namespace OakProyektor.Tests.App;

public class HandlerTests
{
    private static string Msg(string kind, string type, string payload = "{}") =>
        $$"""{"v":1,"id":"q1","kind":"{{kind}}","type":"{{type}}","payload":{{payload}}}""";

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
            new SettingsSetAutoRestoreHandler(new InMemorySettings(), output),
        ];
        return (new IpcDispatcher(handlers, NullLogger<IpcDispatcher>.Instance), output);
    }

    [Fact]
    public async Task Every_inbound_message_in_the_catalog_has_a_handler()
    {
        var (d, output) = Create();
        foreach (var m in IpcCatalog.Messages.Where(m => m.Kind is IpcKind.Command or IpcKind.Query))
        {
            var payload = m.Type switch
            {
                IpcTypes.OutputSetMaster => """{"master":"live"}""",
                IpcTypes.SettingsSetAutoRestore => """{"enabled":false}""",
                _ => "{}",
            };
            await d.DispatchAsync(Msg(m.Kind, m.Type, payload), CancellationToken.None);
        }
        // Los comandos llegaron a su handler (si faltara uno, el despachador solo lo registraría y no quedaría la llamada).
        Assert.Equal(["open:auto", "close", "master:Live", "pause:Manual", "settingsChanged"], output.Calls);
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

    [Fact]
    public async Task Open_passes_the_requested_display_index_or_auto()
    {
        var (d, output) = Create();
        await d.DispatchAsync(Msg("command", "output.open", """{"displayIndex":1}"""), CancellationToken.None);
        await d.DispatchAsync(Msg("command", "output.open", """{"displayIndex":null}"""), CancellationToken.None);
        await d.DispatchAsync(Msg("command", "output.open"), CancellationToken.None);
        Assert.Equal(["open:1", "open:auto", "open:auto"], output.Calls);
    }

    [Fact]
    public async Task Display_list_query_replies_with_the_monitors()
    {
        var (d, _) = Create(new DisplayInfo(0, 0, 0, 1920, 1080, 1.25, true, true), new DisplayInfo(1, 1920, 0, 1920, 1080, 1, false, false));

        var reply = await d.DispatchAsync(Msg("query", "display.list"), CancellationToken.None);

        Assert.NotNull(reply);
        Assert.Equal(("q1", "reply"), (reply.Id, reply.Kind));
        Assert.Equal(2, reply.GetPayload<DisplayListPayload>().Displays.Count);
    }

    [Fact]
    public async Task Get_state_query_replies_with_the_snapshot()
    {
        var (d, _) = Create();
        var reply = await d.DispatchAsync(Msg("query", "output.getState"), CancellationToken.None);
        Assert.Equal(MasterMode.Black, reply!.GetPayload<OutputStateChangedPayload>().State.Master);
    }
}




