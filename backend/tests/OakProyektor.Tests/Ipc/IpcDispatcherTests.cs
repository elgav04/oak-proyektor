using System.Text.Json;
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
        $$"""{"v":1,"id":"{{id}}","kind":"{{kind}}","type":"{{type}}","payload":{{payload}}}""";

    private static (IpcDispatcher Dispatcher, ListLogger<IpcDispatcher> Log) Create(params IIpcHandler[] handlers)
    {
        var log = new ListLogger<IpcDispatcher>();
        return (new IpcDispatcher(handlers, log), log);
    }

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
    public async Task Query_gets_a_reply_with_the_same_id_and_kind_reply()
    {
        var (d, _) = Create(new FakeHandler(IpcKind.Query, IpcTypes.DisplayList, (_, _) =>
            Task.FromResult<object?>(new DisplayListPayload([]))));

        var reply = await d.DispatchAsync(Msg("query", "display.list", id: "corr-42"), CancellationToken.None);

        Assert.NotNull(reply);
        Assert.Equal(("corr-42", "reply", "display.list"), (reply.Id, reply.Kind, reply.Type));
        Assert.Empty(reply.GetPayload<DisplayListPayload>().Displays);
    }

    [Fact]
    public async Task Unknown_messages_are_logged_and_ignored()
    {
        var (d, log) = Create();

        Assert.Null(await d.DispatchAsync(Msg("command", "output.nope"), CancellationToken.None));
        Assert.Null(await d.DispatchAsync(Msg("event", "output.stateChanged"), CancellationToken.None));   // los eventos no entran
        Assert.Null(await d.DispatchAsync(Msg("reply", "display.list"), CancellationToken.None));

        Assert.Equal(3, log.Entries.Count(e => e.Level == LogLevel.Warning));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("""{"v":99,"id":"1","kind":"command","type":"output.close","payload":{}}""")]
    public async Task Invalid_messages_are_logged_and_ignored(string raw)
    {
        var called = false;
        var (d, log) = Create(new FakeHandler(IpcKind.Command, IpcTypes.OutputClose, (_, _) => { called = true; return Task.FromResult<object?>(null); }));

        Assert.Null(await d.DispatchAsync(raw, CancellationToken.None));

        Assert.False(called);
        Assert.Single(log.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task A_failing_handler_does_not_throw_and_the_next_message_still_works()
    {
        var calls = 0;
        var (d, log) = Create(new FakeHandler(IpcKind.Command, IpcTypes.OutputOpen, (req, _) =>
        {
            calls++;
            if (calls == 1) throw new InvalidOperationException("boom");
            return Task.FromResult<object?>(null);
        }));

        Assert.Null(await d.DispatchAsync(Msg("command", "output.open"), CancellationToken.None));   // falla: se registra
        Assert.Null(await d.DispatchAsync(Msg("command", "output.open"), CancellationToken.None));   // el dispatcher sigue vivo

        Assert.Equal(2, calls);
        Assert.Single(log.Entries, e => e.Level == LogLevel.Error && e.Exception is InvalidOperationException);
    }

    [Fact]
    public async Task A_bad_payload_is_a_handled_failure_not_a_crash()
    {
        var (d, log) = Create(new FakeHandler(IpcKind.Command, IpcTypes.OutputSetMaster, (req, _) =>
        {
            req.GetPayload<OutputSetMasterPayload>();
            return Task.FromResult<object?>(null);
        }));

        Assert.Null(await d.DispatchAsync(Msg("command", "output.setMaster", payload: """{"master":"bogus"}"""), CancellationToken.None));

        Assert.Single(log.Entries, e => e.Level == LogLevel.Error && e.Exception is JsonException);
    }

    [Fact]
    public async Task A_query_without_an_answer_is_logged_and_gives_no_reply()
    {
        var (d, log) = Create(new FakeHandler(IpcKind.Query, IpcTypes.DisplayList, (_, _) => Task.FromResult<object?>(null)));

        Assert.Null(await d.DispatchAsync(Msg("query", "display.list"), CancellationToken.None));
        Assert.Single(log.Entries, e => e.Level == LogLevel.Warning);
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
}
