using Microsoft.Extensions.Logging.Abstractions;
using OakProyektor.App;
using OakProyektor.Core.Output;
using OakProyektor.Ipc;
using OakProyektor.Ipc.Messages;

namespace OakProyektor.Tests.App;

public class InboundProcessorTests
{
    private sealed class Handler(string kind, string type, Func<IpcEnvelope, CancellationToken, Task<object?>> body) : IIpcHandler
    {
        public string Kind => kind;
        public string Type => type;
        public Task<object?> HandleAsync(IpcEnvelope request, CancellationToken ct) => body(request, ct);
    }

    private static string Cmd(string type, string payload = "{}", string id = "x") =>
        $$"""{"v":2,"id":"{{id}}","kind":"command","type":"{{type}}","payload":{{payload}}}""";

    private static (InboundQueue Intake, Outbox Outbox, InboundProcessor Processor) Create(params IIpcHandler[] handlers)
    {
        var intake = new InboundQueue(NullLogger<InboundQueue>.Instance);
        var outbox = new Outbox(NullLogger<Outbox>.Instance);
        var dispatcher = new IpcDispatcher(handlers, NullLogger<IpcDispatcher>.Instance);
        return (intake, outbox, new InboundProcessor(intake, dispatcher, outbox, NullLogger<InboundProcessor>.Instance));
    }

    [Fact]
    public async Task A_slow_output_open_does_not_delay_a_set_master_sent_right_after()
    {
        var openStarted = new TaskCompletionSource();
        var releaseOpen = new TaskCompletionSource();
        var masterHandledAt = new TaskCompletionSource<DateTime>();
        var openFinishedAt = DateTime.MinValue;

        var (intake, _, processor) = Create(
            new Handler(IpcKind.Command, IpcTypes.OutputOpen, async (_, _) =>
            {
                openStarted.SetResult();
                await releaseOpen.Task;                 // «crear la ventana» tarda: no termina hasta que el test lo permita
                openFinishedAt = DateTime.UtcNow;
                return null;
            }),
            new Handler(IpcKind.Command, IpcTypes.OutputSetMaster, (_, _) =>
            {
                masterHandledAt.SetResult(DateTime.UtcNow);
                return Task.FromResult<object?>(null);
            }));
        using var cts = new CancellationTokenSource();
        var tasks = processor.Start(cts.Token);

        intake.Enqueue(WindowKind.Control, Cmd("output.open"));
        await openStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));          // el open ya está ocupando el carril normal
        intake.Enqueue(WindowKind.Control, Cmd("output.setMaster", """{"master":"black"}"""));

        // El setMaster se atiende MIENTRAS el open sigue bloqueado.
        var handledAt = await masterHandledAt.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(DateTime.MinValue, openFinishedAt);
        Assert.False(releaseOpen.Task.IsCompleted);

        releaseOpen.SetResult();
        cts.Cancel();
        intake.Complete();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(handledAt < DateTime.UtcNow);
    }

    [Fact]
    public async Task Emergency_recover_is_also_high_priority()
    {
        var releaseSlow = new TaskCompletionSource();
        var slowStarted = new TaskCompletionSource();
        var recovered = new TaskCompletionSource();
        var (intake, _, processor) = Create(
            new Handler(IpcKind.Command, IpcTypes.OutputClose, async (_, _) => { slowStarted.SetResult(); await releaseSlow.Task; return null; }),
            new Handler(IpcKind.Command, IpcTypes.OutputRecover, (_, _) => { recovered.SetResult(); return Task.FromResult<object?>(null); }));
        using var cts = new CancellationTokenSource();
        var tasks = processor.Start(cts.Token);

        intake.Enqueue(WindowKind.Control, Cmd("output.close"));
        await slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        intake.Enqueue(WindowKind.Output, Cmd("output.recover"));

        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        releaseSlow.SetResult();
        cts.Cancel(); intake.Complete();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Normal_lane_commands_still_run_one_at_a_time_in_arrival_order()
    {
        var order = new List<string>();
        var done = new TaskCompletionSource();
        var (intake, _, processor) = Create(
            new Handler(IpcKind.Command, IpcTypes.OutputOpen, async (_, _) => { await Task.Delay(100); lock (order) order.Add("open"); return null; }),
            new Handler(IpcKind.Command, IpcTypes.OutputClose, (_, _) => { lock (order) order.Add("close"); done.SetResult(); return Task.FromResult<object?>(null); }));
        using var cts = new CancellationTokenSource();
        var tasks = processor.Start(cts.Token);

        intake.Enqueue(WindowKind.Control, Cmd("output.open"));
        intake.Enqueue(WindowKind.Control, Cmd("output.close"));
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["open", "close"], order);   // el close espera al open: FIFO dentro del carril normal
        cts.Cancel(); intake.Complete();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Failures_are_sent_back_to_the_window_that_asked_and_the_lanes_keep_running()
    {
        var (intake, outbox, processor) = Create(
            new Handler(IpcKind.Command, IpcTypes.OutputOpen, (_, _) => throw new IpcException("output.pageMissing", "falta output.html")),
            new Handler(IpcKind.Command, IpcTypes.OutputSetMaster, (_, _) => Task.FromResult<object?>(null)));
        using var cts = new CancellationTokenSource();
        var tasks = processor.Start(cts.Token);

        intake.Enqueue(WindowKind.Control, "esto no es json");                       // basura: se descarta y el procesador sigue
        intake.Enqueue(WindowKind.Control, Cmd("output.open", id: "c-1"));           // falla → command.failed al panel
        intake.Enqueue(WindowKind.Output, Cmd("output.nope", id: "c-2"));            // desconocido → command.failed a la SALIDA

        var got = new List<(Targets Target, IpcEnvelope Env)>();
        await foreach (var (target, json) in outbox.ReadAllAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token))
        {
            Assert.True(IpcSerializer.TryParse(json, out var env, out _));
            got.Add((target, env!));
            if (got.Count == 2) break;
        }

        Assert.Equal((Targets.Control, "command.failed", "output.pageMissing"),
            (got[0].Target, got[0].Env.Type, got[0].Env.GetPayload<CommandFailedPayload>().Error.Code));
        Assert.Equal((Targets.Output, "unknown_message"), (got[1].Target, got[1].Env.GetPayload<CommandFailedPayload>().Error.Code));
        cts.Cancel(); intake.Complete();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Only_a_real_shutdown_stops_the_lanes()
    {
        var calls = 0;
        var second = new TaskCompletionSource();
        var (intake, _, processor) = Create(
            new Handler(IpcKind.Command, IpcTypes.OutputOpen, (_, _) =>
            {
                // Un OperationCanceledException AJENO al apagado (p. ej. un timeout interno) no debe matar el carril (M12).
                if (++calls == 1) throw new OperationCanceledException("timeout interno");
                second.SetResult();
                return Task.FromResult<object?>(null);
            }));
        using var cts = new CancellationTokenSource();
        var tasks = processor.Start(cts.Token);

        intake.Enqueue(WindowKind.Control, Cmd("output.open", id: "a"));
        intake.Enqueue(WindowKind.Control, Cmd("output.open", id: "b"));

        await second.Task.WaitAsync(TimeSpan.FromSeconds(5));   // el segundo mensaje se atendió: el carril sobrevivió
        Assert.All(tasks, t => Assert.False(t.IsCompleted));
        cts.Cancel(); intake.Complete();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
    }
}
