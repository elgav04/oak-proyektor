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
        $$"""{"v":3,"id":"{{id}}","kind":"command","type":"{{type}}","payload":{{payload}}}""";

    private static (InboundQueue Intake, Outbox Outbox, InboundProcessor Processor) Create(params IIpcHandler[] handlers) =>
        CreateWithCapacity(InboundProcessor.DefaultLaneCapacity, handlers);

    private static (InboundQueue Intake, Outbox Outbox, InboundProcessor Processor) CreateWithCapacity(int laneCapacity, params IIpcHandler[] handlers)
    {
        var intake = new InboundQueue(NullLogger<InboundQueue>.Instance);
        var outbox = new Outbox(NullLogger<Outbox>.Instance);
        var dispatcher = new IpcDispatcher(handlers, NullLogger<IpcDispatcher>.Instance);
        return (intake, outbox, new InboundProcessor(intake, dispatcher, outbox, new OrderClock(), NullLogger<InboundProcessor>.Instance, laneCapacity));
    }

    /// <summary>Lee el siguiente mensaje saliente (con tiempo límite) y lo devuelve ya interpretado.</summary>
    private static async Task<(Targets Target, IpcEnvelope Envelope)> NextOutboundAsync(Outbox outbox)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var (target, json) in outbox.ReadAllAsync(cts.Token))
        {
            Assert.True(IpcSerializer.TryParse(json, out var envelope, out var error), error);
            return (target, envelope!);
        }
        throw new InvalidOperationException("La cola de salida se cerró sin mensajes.");
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
    public async Task A_command_dropped_by_a_full_lane_is_reported_to_the_panel_instead_of_vanishing()
    {
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var handled = new List<string>();
        var (intake, outbox, processor) = CreateWithCapacity(laneCapacity: 2,
            new Handler(IpcKind.Command, IpcTypes.OutputOpen, async (request, _) =>
            {
                lock (handled) handled.Add(request.Id);
                started.TrySetResult();
                await release.Task;                       // un handler atascado: el carril normal no avanza
                return null;
            }));
        using var cts = new CancellationTokenSource();
        var tasks = processor.Start(cts.Token);

        intake.Enqueue(WindowKind.Control, Cmd("output.open", id: "c1"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));          // c1 ya está en el handler (fuera del canal)
        intake.Enqueue(WindowKind.Control, Cmd("output.open", id: "c2"));   // caben 2 en el carril: c2 y c3
        intake.Enqueue(WindowKind.Control, Cmd("output.open", id: "c3"));
        intake.Enqueue(WindowKind.Control, Cmd("output.open", id: "c4"));   // ← no cabe

        var (target, failure) = await NextOutboundAsync(outbox);

        Assert.Equal(Targets.Control, target);                           // al panel que lo mandó
        Assert.Equal(IpcTypes.CommandFailed, failure.Type);
        var payload = failure.GetPayload<CommandFailedPayload>();
        Assert.Equal(("c4", IpcTypes.OutputOpen, "overloaded"), (payload.CommandId, payload.CommandType, payload.Error.Code));
        Assert.Contains("NO se ejecutó", payload.Error.Message);

        // Lo que SÍ cabió se ejecuta cuando el carril avanza: solo se rechazó lo que no cabía, y solo c4.
        release.SetResult();
        await WaitUntilAsync(() => { lock (handled) return handled.Count == 3; });
        lock (handled) Assert.Equal(["c1", "c2", "c3"], handled);
        cts.Cancel(); intake.Complete();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task The_failure_goes_to_the_window_that_sent_the_dropped_message_and_a_dropped_query_gets_an_error_reply()
    {
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var (intake, outbox, processor) = CreateWithCapacity(laneCapacity: 1,
            new Handler(IpcKind.Command, IpcTypes.OutputOpen, async (_, _) => { started.TrySetResult(); await release.Task; return null; }),
            new Handler(IpcKind.Query, IpcTypes.DisplayList, (_, _) => Task.FromResult<object?>(new { })));
        using var cts = new CancellationTokenSource();
        var tasks = processor.Start(cts.Token);

        intake.Enqueue(WindowKind.Control, Cmd("output.open", id: "a"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        intake.Enqueue(WindowKind.Control, Cmd("output.open", id: "b"));            // ocupa el único hueco
        intake.Enqueue(WindowKind.Output, Cmd("output.open", id: "from-output"));   // no cabe: el aviso va a la SALIDA, no al panel
        var (target1, _) = await NextOutboundAsync(outbox);
        Assert.Equal(Targets.Output, target1);

        // Una query descartada recibe un reply con error (no un command.failed), correlacionado por su id: el cliente la rechaza al instante.
        intake.Enqueue(WindowKind.Control, """{"v":3,"id":"q9","kind":"query","type":"display.list","payload":{}}""");
        var (target2, reply) = await NextOutboundAsync(outbox);
        Assert.Equal(Targets.Control, target2);
        Assert.Equal(("reply", IpcTypes.DisplayList, "q9"), (reply.Kind, reply.Type, reply.Id));
        var body = reply.GetPayload<IpcReply>();
        Assert.False(body.Ok);
        Assert.Equal("overloaded", body.Error!.Code);

        release.SetResult();
        cts.Cancel(); intake.Complete();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "la condición no se cumplió a tiempo");
    }

    [Fact]
    public async Task Every_message_gets_an_arrival_number_so_a_recover_processed_first_is_still_known_to_come_after_the_open()
    {
        // Es la carrera de I-A: «Mover / reabrir» y enseguida «Recuperar control». El recover (carril de prioridad) se ATIENDE antes que el open,
        // pero su número de llegada es MAYOR: con eso el controlador sabe que la pausa fue posterior y no la deshace.
        var openStarted = new TaskCompletionSource<long>();
        var releaseOpen = new TaskCompletionSource();
        var recoverOrder = new TaskCompletionSource<long>();
        var (intake, _, processor) = Create(
            new Handler(IpcKind.Command, IpcTypes.OutputOpen, async (request, _) => { openStarted.SetResult(request.Order); await releaseOpen.Task; return null; }),
            new Handler(IpcKind.Command, IpcTypes.OutputRecover, (request, _) => { recoverOrder.SetResult(request.Order); return Task.FromResult<object?>(null); }));
        using var cts = new CancellationTokenSource();
        var tasks = processor.Start(cts.Token);

        intake.Enqueue(WindowKind.Control, Cmd("output.open"));
        intake.Enqueue(WindowKind.Control, Cmd("output.recover"));

        var open = await openStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var recover = await recoverOrder.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(open > 0, "el open debe llevar número de llegada");
        Assert.True(recover > open, $"el recover ({recover}) llegó después del open ({open})");

        releaseOpen.SetResult();
        cts.Cancel(); intake.Complete();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void The_arrival_clock_is_strictly_increasing_and_never_zero()
    {
        var clock = new OrderClock();
        var a = clock.Next(); var b = clock.Next(); var c = clock.Next();
        Assert.True(a > 0 && b > a && c > b);
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
