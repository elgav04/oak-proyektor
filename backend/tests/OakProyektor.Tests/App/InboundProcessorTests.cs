using Microsoft.Extensions.Logging.Abstractions;
using OakProyektor.App;
using OakProyektor.App.Handlers;
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
        $$"""{"v":16,"id":"{{id}}","kind":"command","type":"{{type}}","payload":{{payload}}}""";

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

    // ───── H6: tres carriles ─────

    [Theory]
    [InlineData("command", "output.setMaster", IpcLane.Priority)]
    [InlineData("command", "output.recover", IpcLane.Priority)]
    [InlineData("command", "output.showContent", IpcLane.Live)]
    [InlineData("command", "output.clearContent", IpcLane.Live)]
    [InlineData("command", "output.reportBackgroundFailure", IpcLane.Live)]
    [InlineData("command", "bible.setSettings", IpcLane.Live)]
    [InlineData("command", "settings.setAutoRestore", IpcLane.Live)]
    [InlineData("query", "output.getState", IpcLane.Live)]
    [InlineData("query", "display.list", IpcLane.Live)]
    [InlineData("query", "database.getStatus", IpcLane.Live)]
    [InlineData("query", "bible.lookup", IpcLane.Live)]
    [InlineData("query", "bible.chapter", IpcLane.Live)]
    [InlineData("query", "bible.suggest", IpcLane.Live)]
    [InlineData("query", "bible.history", IpcLane.Live)]
    [InlineData("query", "bible.getSettings", IpcLane.Live)]
    [InlineData("command", "output.nope", IpcLane.Live)]
    [InlineData("command", "output.setBackground", IpcLane.Slow)]
    [InlineData("command", "output.retryBackground", IpcLane.Slow)]
    [InlineData("command", "output.open", IpcLane.Slow)]
    [InlineData("command", "output.close", IpcLane.Slow)]
    [InlineData("command", "bible.import", IpcLane.Slow)]
    [InlineData("query", "bible.search", IpcLane.Slow)]
    // Canciones: lo que solo lee memoria o consultas cortas va en vivo; TODA escritura (puede esperar el bloqueo de SQLite) y la búsqueda de texto van por el carril lento.
    [InlineData("query", "song.list", IpcLane.Live)]
    [InlineData("query", "song.get", IpcLane.Live)]
    [InlineData("query", "song.parseText", IpcLane.Live)]
    [InlineData("query", "song.history", IpcLane.Live)]
    [InlineData("query", "song.getSettings", IpcLane.Live)]
    [InlineData("command", "song.setSettings", IpcLane.Live)]
    [InlineData("query", "song.search", IpcLane.Slow)]
    [InlineData("command", "song.save", IpcLane.Slow)]
    [InlineData("command", "song.restoreRevision", IpcLane.Slow)]
    [InlineData("command", "song.delete", IpcLane.Slow)]
    public void Every_message_belongs_to_the_lane_its_urgency_asks_for(string kind, string type, IpcLane lane)
    {
        Assert.True(IpcSerializer.TryParse($$$"""{"v":16,"id":"x","kind":"{{{kind}}}","type":"{{{type}}}","payload":{}}""", out var env, out var err), err);
        Assert.Equal(lane, IpcPriority.LaneOf(env!));
    }

    [Fact]
    public void Every_catalog_message_has_a_deliberate_lane()
    {
        // Lo que NO es Live se enumera a mano: si alguien añade un comando que toca disco o ventanas y lo olvida, queda en Live y este test lo obliga a decidir.
        var slow = IpcCatalog.Messages.Where(m => IpcPriority.LaneOf(new IpcEnvelope(IpcCatalog.ProtocolVersion, "x", m.Kind, m.Type, default)) == IpcLane.Slow).Select(m => m.Type).Distinct().Order().ToArray();
        Assert.Equal(["alert.delete", "alert.save", "bible.import", "bible.search", "output.close", "output.open", "output.retryBackground", "output.setBackground",
                      "playlist.addItem", "playlist.create", "playlist.delete", "playlist.duplicate", "playlist.moveItem", "playlist.removeItem", "playlist.update", "playlist.updateItem",
                      "song.delete", "song.restoreRevision", "song.save", "song.search"], slow);
    }

    [Fact]
    public async Task A_show_content_sent_right_after_a_background_whose_validation_takes_5_seconds_is_handled_without_waiting()
    {
        // Simulación del requisito: setBackground con una validación de 5 s (disco atascado). showContent, enviado justo después, debe salir al instante.
        var bgStarted = new TaskCompletionSource();
        var releaseBg = new TaskCompletionSource();
        var shown = new TaskCompletionSource<TimeSpan>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var (intake, _, processor) = Create(
            new Handler(IpcKind.Command, IpcTypes.OutputSetBackground, async (_, _) => { bgStarted.SetResult(); await releaseBg.Task; return null; }),   // «5 s»: no termina hasta que el test lo permita
            new Handler(IpcKind.Command, IpcTypes.OutputShowContent, (_, _) => { shown.SetResult(clock.Elapsed); return Task.FromResult<object?>(null); }));
        using var cts = new CancellationTokenSource();
        var tasks = processor.Start(cts.Token);

        intake.Enqueue(WindowKind.Control, Cmd("output.setBackground"));
        await bgStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var sentAt = clock.Elapsed;
        intake.Enqueue(WindowKind.Control, Cmd("output.showContent"));

        var shownAt = await shown.Task.WaitAsync(TimeSpan.FromSeconds(2));   // con el fondo AÚN bloqueado
        Assert.False(releaseBg.Task.IsCompleted);
        Assert.True(shownAt - sentAt < TimeSpan.FromMilliseconds(500), $"showContent tardó {(shownAt - sentAt).TotalMilliseconds:0} ms");

        releaseBg.SetResult();
        cts.Cancel(); intake.Complete();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task With_the_real_handlers_a_slow_disk_check_does_not_hold_back_show_clear_or_the_master_modes()
    {
        var gate = new ManualResetEventSlim();
        var output = new FakeOutputController();
        var timeout = TimeSpan.FromSeconds(5);   // el tope real de la validación
        var (intake, _, processor) = Create(
            new OutputSetBackgroundHandler(output, new SlowDisk(gate), timeout),
            new OutputShowContentHandler(output),
            new OutputClearContentHandler(output),
            new OutputSetMasterHandler(output));
        using var cts = new CancellationTokenSource();
        var tasks = processor.Start(cts.Token);
        const string Theme = """{"fontFamily":"Arial","fontSize":72,"color":"#FFFFFF","outlineWidth":0,"outlineColor":"#000000","shadowBlur":0,"shadowOffset":0,"shadowColor":"#000000","align":"center","verticalAlign":"middle","lineHeight":1.2,"marginTop":50,"marginRight":50,"marginBottom":50,"marginLeft":50,"autoFit":true,"minFontSize":24}""";
        try
        {
            intake.Enqueue(WindowKind.Control, Cmd("output.setBackground", """{"kind":"video","path":"C:\\a.mp4","color":"#102030","muted":true,"transitionMs":300}"""));
            await Task.Delay(100);   // la validación ya está colgada en el disco
            var clock = System.Diagnostics.Stopwatch.StartNew();
            intake.Enqueue(WindowKind.Control, Cmd("output.showContent", $$"""{"blocks":["Uno"],"theme":{{Theme}},"transitionMs":280}"""));
            intake.Enqueue(WindowKind.Control, Cmd("output.clearContent", """{"transitionMs":280}"""));
            intake.Enqueue(WindowKind.Control, Cmd("output.setMaster", """{"master":"black"}"""));
            await WaitUntilAsync(() => { lock (output.Calls) return output.Calls.Count >= 3; });
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1.5), $"tardó {clock.Elapsed.TotalMilliseconds:0} ms con el fondo colgado");
            lock (output.Calls)
            {
                Assert.DoesNotContain(output.Calls, c => c.StartsWith("background:"));   // el fondo sigue sin aplicarse
                Assert.Equal(["showContent:1:280", "clearContent:280"], output.Calls.Where(c => c.StartsWith("showContent") || c.StartsWith("clearContent")));   // orden FIFO dentro del carril
            }
        }
        finally { gate.Set(); }
        // Al responder el disco, el fondo se aplica por su carril.
        await WaitUntilAsync(() => { lock (output.Calls) return output.Calls.Contains("background:Video"); });
        cts.Cancel(); intake.Complete();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Inside_each_lane_the_order_is_kept_even_when_the_lanes_interleave()
    {
        var live = new List<string>();
        var slow = new List<string>();
        var allDone = new CountdownEvent(8);
        var (intake, _, processor) = Create(
            new Handler(IpcKind.Command, IpcTypes.OutputShowContent, async (r, _) => { await Task.Delay(r.Id.EndsWith('1') ? 30 : 1); lock (live) live.Add(r.Id); allDone.Signal(); return null; }),
            new Handler(IpcKind.Command, IpcTypes.OutputSetBackground, async (r, _) => { await Task.Delay(r.Id.EndsWith('1') ? 30 : 1); lock (slow) slow.Add(r.Id); allDone.Signal(); return null; }));
        using var cts = new CancellationTokenSource();
        var tasks = processor.Start(cts.Token);
        for (var i = 0; i < 4; i++)
        {
            intake.Enqueue(WindowKind.Control, Cmd("output.showContent", id: $"live{i}"));
            intake.Enqueue(WindowKind.Control, Cmd("output.setBackground", id: $"slow{i}"));
        }
        Assert.True(allDone.Wait(TimeSpan.FromSeconds(5)));
        lock (live) Assert.Equal(["live0", "live1", "live2", "live3"], live);
        lock (slow) Assert.Equal(["slow0", "slow1", "slow2", "slow3"], slow);
        cts.Cancel(); intake.Complete();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_full_slow_lane_does_not_reject_live_messages()
    {
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var shown = new TaskCompletionSource();
        var (intake, outbox, processor) = CreateWithCapacity(laneCapacity: 1,
            new Handler(IpcKind.Command, IpcTypes.OutputSetBackground, async (_, _) => { started.TrySetResult(); await release.Task; return null; }),
            new Handler(IpcKind.Command, IpcTypes.OutputShowContent, (_, _) => { shown.TrySetResult(); return Task.FromResult<object?>(null); }));
        using var cts = new CancellationTokenSource();
        var tasks = processor.Start(cts.Token);

        intake.Enqueue(WindowKind.Control, Cmd("output.setBackground", id: "b1"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        intake.Enqueue(WindowKind.Control, Cmd("output.setBackground", id: "b2"));   // llena el carril lento
        intake.Enqueue(WindowKind.Control, Cmd("output.setBackground", id: "b3"));   // se rechaza (overloaded)
        var (_, failure) = await NextOutboundAsync(outbox);
        Assert.Equal("b3", failure.GetPayload<CommandFailedPayload>().CommandId);

        intake.Enqueue(WindowKind.Control, Cmd("output.showContent", id: "v1"));     // el carril en vivo sigue libre
        await shown.Task.WaitAsync(TimeSpan.FromSeconds(2));
        release.SetResult();
        cts.Cancel(); intake.Complete();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class SlowDisk(ManualResetEventSlim gate) : OakProyektor.Core.Output.IMediaFileSystem
    {
        public DriveType DriveTypeOf(string root) => DriveType.Fixed;
        public OakProyektor.Core.Output.FileProbe Probe(string path) { gate.Wait(TimeSpan.FromSeconds(30)); return new OakProyektor.Core.Output.FileProbe(true, 1, null); }
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
            new Handler(IpcKind.Query, IpcTypes.BibleSearch, (_, _) => Task.FromResult<object?>(new { })));   // una consulta del carril LENTO (el que está lleno)
        using var cts = new CancellationTokenSource();
        var tasks = processor.Start(cts.Token);

        intake.Enqueue(WindowKind.Control, Cmd("output.open", id: "a"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        intake.Enqueue(WindowKind.Control, Cmd("output.open", id: "b"));            // ocupa el único hueco
        intake.Enqueue(WindowKind.Output, Cmd("output.open", id: "from-output"));   // no cabe: el aviso va a la SALIDA, no al panel
        var (target1, _) = await NextOutboundAsync(outbox);
        Assert.Equal(Targets.Output, target1);

        // Una query descartada recibe un reply con error (no un command.failed), correlacionado por su id: el cliente la rechaza al instante.
        intake.Enqueue(WindowKind.Control, """{"v":16,"id":"q9","kind":"query","type":"bible.search","payload":{}}""");
        var (target2, reply) = await NextOutboundAsync(outbox);
        Assert.Equal(Targets.Control, target2);
        Assert.Equal(("reply", IpcTypes.BibleSearch, "q9"), (reply.Kind, reply.Type, reply.Id));
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

        // Van por carriles distintos (open = lento, desconocido = en vivo): el orden de llegada de las respuestas NO está garantizado, así que se buscan por destino.
        var control = got.Single(g => g.Target == Targets.Control);
        var output = got.Single(g => g.Target == Targets.Output);
        Assert.Equal(("command.failed", "output.pageMissing"), (control.Env.Type, control.Env.GetPayload<CommandFailedPayload>().Error.Code));
        Assert.Equal("unknown_message", output.Env.GetPayload<CommandFailedPayload>().Error.Code);
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
