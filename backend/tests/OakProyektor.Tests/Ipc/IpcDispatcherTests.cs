using Microsoft.Extensions.Logging;
using OakProyektor.Core.Output;
using OakProyektor.Ipc;
using OakProyektor.Ipc.Messages;

namespace OakProyektor.Tests.Ipc;

public class IpcDispatcherTests
{
    private sealed class FakeHandler(string kind, string type, Func<IpcEnvelope, CancellationToken, Task<object?>> body) : IIpcHandler
    {
        public string Kind => kind;
        public string Type => type;
        public Task<object?> HandleAsync(IpcEnvelope request, CancellationToken ct) => body(request, ct);
    }

    private static string Msg(string kind, string type, string id = "id-1", string payload = "{}") =>
        $$"""{"v":5,"id":"{{id}}","kind":"{{kind}}","type":"{{type}}","payload":{{payload}}}""";

    private static (IpcDispatcher Dispatcher, ListLogger<IpcDispatcher> Log) Create(params IIpcHandler[] handlers)
    {
        var log = new ListLogger<IpcDispatcher>();
        return (new IpcDispatcher(handlers, log), log);
    }

    private static FakeHandler Throwing(string kind, string type, Exception ex) =>
        new(kind, type, (_, _) => throw ex);

    [Fact]
    public async Task Command_is_routed_to_its_handler_with_the_payload_and_gives_no_reply()
    {
        MasterMode? received = null;
        var (d, _) = Create(new FakeHandler(IpcKind.Command, IpcTypes.OutputSetMaster, (req, _) =>
        {
            received = req.GetPayload<OutputSetMasterPayload>().Master;
            return Task.FromResult<object?>(null);
        }));

        var reply = await d.DispatchAsync(Msg("command", "output.setMaster", payload: """{"master":"logo"}"""), CancellationToken.None);

        Assert.Null(reply);
        Assert.Equal(MasterMode.Logo, received);
    }

    [Fact]
    public async Task Query_gets_an_ok_reply_with_the_same_id()
    {
        var (d, _) = Create(new FakeHandler(IpcKind.Query, IpcTypes.DisplayList, (_, _) =>
            Task.FromResult<object?>(new DisplayListPayload(3, []))));

        var reply = await d.DispatchAsync(Msg("query", "display.list", id: "corr-42"), CancellationToken.None);

        Assert.NotNull(reply);
        Assert.Equal(("corr-42", "reply", "display.list"), (reply.Id, reply.Kind, reply.Type));
        Assert.True(reply.GetPayload<IpcReply>().Ok);
        Assert.Null(reply.GetPayload<IpcReply>().Error);
        Assert.Equal(3, reply.ReplyResult<DisplayListPayload>().Seq);
    }

    [Fact]
    public async Task Unknown_query_gets_an_error_reply_instead_of_silence_so_the_client_does_not_wait_for_the_timeout()
    {
        var (d, log) = Create();

        var reply = await d.DispatchAsync(Msg("query", "no.existe", id: "q-9"), CancellationToken.None);

        Assert.NotNull(reply);
        Assert.Equal(("q-9", "reply", "no.existe"), (reply.Id, reply.Kind, reply.Type));
        Assert.False(reply.GetPayload<IpcReply>().Ok);
        Assert.Equal("unknown_message", reply.ReplyError().Code);
        Assert.Single(log.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task Unknown_command_notifies_through_command_failed()
    {
        var (d, _) = Create();

        var failed = await d.DispatchAsync(Msg("command", "output.nope", id: "c-1"), CancellationToken.None);

        Assert.NotNull(failed);
        Assert.Equal(("event", "command.failed"), (failed.Kind, failed.Type));
        var p = failed.GetPayload<CommandFailedPayload>();
        Assert.Equal(("c-1", "output.nope", "unknown_message"), (p.CommandId, p.CommandType, p.Error.Code));
    }

    [Theory]
    [InlineData("event", "output.stateChanged")]
    [InlineData("reply", "display.list")]
    public async Task Inbound_events_and_replies_are_ignored_with_a_warning(string kind, string type)
    {
        var (d, log) = Create();
        Assert.Null(await d.DispatchAsync(Msg(kind, type), CancellationToken.None));
        Assert.Single(log.Entries, e => e.Level == LogLevel.Warning);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("""{"v":99,"id":"1","kind":"command","type":"output.close","payload":{}}""")]
    [InlineData("""{"v":1,"id":"1","kind":"command","type":"output.close","payload":{}}""")]
    public async Task Uncorrelatable_messages_are_logged_and_ignored(string raw)
    {
        var called = false;
        var (d, log) = Create(new FakeHandler(IpcKind.Command, IpcTypes.OutputClose, (_, _) => { called = true; return Task.FromResult<object?>(null); }));

        Assert.Null(await d.DispatchAsync(raw, CancellationToken.None));

        Assert.False(called);
        Assert.Single(log.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task A_failing_command_notifies_with_a_generic_internal_error_and_never_leaks_the_exception_message()
    {
        var (d, log) = Create(Throwing(IpcKind.Command, IpcTypes.OutputOpen, new InvalidOperationException("detalle-secreto-interno")));

        var failed = await d.DispatchAsync(Msg("command", "output.open", id: "c-7"), CancellationToken.None);

        Assert.Equal("command.failed", failed!.Type);
        var p = failed.GetPayload<CommandFailedPayload>();
        Assert.Equal(("c-7", "internal"), (p.CommandId, p.Error.Code));
        Assert.DoesNotContain("detalle-secreto-interno", p.Error.Message);
        Assert.Single(log.Entries, e => e.Level == LogLevel.Error && e.Exception is InvalidOperationException);   // el detalle va al log
    }

    [Fact]
    public async Task An_IpcException_passes_its_code_and_message_to_the_panel()
    {
        var (d, log) = Create(Throwing(IpcKind.Command, IpcTypes.OutputOpen, new IpcException("output.pageMissing", "No se encontró output.html")));

        var failed = await d.DispatchAsync(Msg("command", "output.open", id: "c-8"), CancellationToken.None);

        var p = failed!.GetPayload<CommandFailedPayload>();
        Assert.Equal(("output.pageMissing", "No se encontró output.html"), (p.Error.Code, p.Error.Message));
        Assert.Single(log.Entries, e => e.Level == LogLevel.Warning);   // esperado, no es un error del programa
    }

    [Fact]
    public async Task A_failing_query_replies_with_ok_false_and_the_real_error()
    {
        var (d, _) = Create(Throwing(IpcKind.Query, IpcTypes.OutputGetState, new IpcException("output.broken", "Sin estado")));

        var reply = await d.DispatchAsync(Msg("query", "output.getState", id: "q-5"), CancellationToken.None);

        Assert.Equal("q-5", reply!.Id);
        Assert.False(reply.GetPayload<IpcReply>().Ok);
        Assert.Equal(("output.broken", "Sin estado"), (reply.ReplyError().Code, reply.ReplyError().Message));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, reply.Payload.GetProperty("result").ValueKind);
    }

    [Fact]
    public async Task A_bad_payload_is_reported_as_invalid_payload()
    {
        var (d, _) = Create(new FakeHandler(IpcKind.Command, IpcTypes.OutputSetMaster, (req, _) =>
        {
            req.GetPayload<OutputSetMasterPayload>();
            return Task.FromResult<object?>(null);
        }));

        var failed = await d.DispatchAsync(Msg("command", "output.setMaster", payload: """{"master":"bogus"}"""), CancellationToken.None);

        Assert.Equal("invalid_payload", failed!.GetPayload<CommandFailedPayload>().Error.Code);
    }

    [Fact]
    public async Task The_dispatcher_survives_failures_and_keeps_serving()
    {
        var calls = 0;
        var (d, _) = Create(new FakeHandler(IpcKind.Command, IpcTypes.OutputOpen, (_, _) =>
        {
            calls++;
            if (calls == 1) throw new InvalidOperationException("boom");
            return Task.FromResult<object?>(null);
        }));

        Assert.NotNull(await d.DispatchAsync(Msg("command", "output.open"), CancellationToken.None));
        Assert.Null(await d.DispatchAsync(Msg("command", "output.open"), CancellationToken.None));

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task A_query_without_an_answer_gets_a_no_result_error()
    {
        var (d, _) = Create(new FakeHandler(IpcKind.Query, IpcTypes.DisplayList, (_, _) => Task.FromResult<object?>(null)));

        var reply = await d.DispatchAsync(Msg("query", "display.list"), CancellationToken.None);

        Assert.Equal("no_result", reply!.ReplyError().Code);
    }

    [Fact]
    public async Task Cancellation_is_propagated()
    {
        using var cts = new CancellationTokenSource();
        var (d, _) = Create(new FakeHandler(IpcKind.Command, IpcTypes.OutputOpen, (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.FromResult<object?>(null); }));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => d.DispatchAsync(Msg("command", "output.open"), cts.Token));
    }

    [Fact]
    public void Duplicate_or_wrongly_typed_handlers_fail_fast_at_startup()
    {
        var a = new FakeHandler(IpcKind.Command, IpcTypes.OutputOpen, (_, _) => Task.FromResult<object?>(null));
        var b = new FakeHandler(IpcKind.Command, IpcTypes.OutputOpen, (_, _) => Task.FromResult<object?>(null));
        var ev = new FakeHandler(IpcKind.Event, IpcTypes.DisplayChanged, (_, _) => Task.FromResult<object?>(null));

        Assert.Throws<ArgumentException>(() => new IpcDispatcher([a, b], new ListLogger<IpcDispatcher>()));
        Assert.Throws<ArgumentException>(() => new IpcDispatcher([ev], new ListLogger<IpcDispatcher>()));
    }

    [Theory]
    [InlineData("command", "output.setMaster", true)]
    [InlineData("command", "output.recover", true)]
    [InlineData("command", "output.open", false)]
    [InlineData("command", "output.close", false)]
    [InlineData("command", "settings.setAutoRestore", false)]
    [InlineData("query", "output.getState", false)]
    public void Only_the_master_controls_and_the_emergency_recovery_are_high_priority(string kind, string type, bool expected)
    {
        Assert.True(IpcSerializer.TryParse(Msg(kind, type), out var env, out _));
        Assert.Equal(expected, IpcPriority.IsHigh(env!));
    }
}
