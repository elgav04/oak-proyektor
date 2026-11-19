using Microsoft.Extensions.Logging.Abstractions;
using OakProyektor.App;
using OakProyektor.Core.Output;
using OakProyektor.Ipc;
using OakProyektor.Ipc.Messages;

namespace OakProyektor.Tests.App;

public class MessageQueueTests
{
    [Fact]
    public async Task Inbound_preserves_arrival_order_across_windows()
    {
        var q = new InboundQueue(NullLogger<InboundQueue>.Instance);
        for (var i = 0; i < 100; i++) Assert.True(q.Enqueue(i % 2 == 0 ? WindowKind.Control : WindowKind.Output, $"m{i}"));
        q.Complete();

        var seen = new List<string>();
        await foreach (var (_, raw) in q.ReadAllAsync(CancellationToken.None)) seen.Add(raw);

        Assert.Equal(Enumerable.Range(0, 100).Select(i => $"m{i}"), seen);
    }

    [Fact]
    public void Inbound_never_blocks_or_throws_when_full_or_closed()
    {
        var q = new InboundQueue(NullLogger<InboundQueue>.Instance);
        for (var i = 0; i < 512; i++) Assert.True(q.Enqueue(WindowKind.Control, "x"));
        Assert.False(q.Enqueue(WindowKind.Control, "overflow"));   // lleno: se descarta y se registra

        q.Complete();
        Assert.False(q.Enqueue(WindowKind.Control, "after-close"));
    }

    [Fact]
    public async Task Outbox_is_fifo_and_keeps_targets()
    {
        var box = new Outbox(NullLogger<Outbox>.Instance);
        box.Publish(IpcEnvelope.Create(IpcKind.Event, IpcTypes.OutputStateChanged, new OutputStateChangedPayload(1, OutputState.Initial, new OutputWindowStatus(false, null, false, false, null, true, null, null, DisplayLostPolicy.WarnAndWait, false, false, []))), Targets.All);
        box.Publish(IpcEnvelope.Create(IpcKind.Event, IpcTypes.DisplayChanged, new DisplayListPayload(1, [])), Targets.Control);
        box.Complete();

        var items = new List<(Targets Target, string Json)>();
        await foreach (var item in box.ReadAllAsync(CancellationToken.None)) items.Add(item);

        Assert.Equal([Targets.All, Targets.Control], items.Select(i => i.Target));
        Assert.Contains("output.stateChanged", items[0].Json);
        Assert.Contains("display.changed", items[1].Json);
    }

    [Fact]
    public void Outbox_publish_after_close_does_not_throw()
    {
        var box = new Outbox(NullLogger<Outbox>.Instance);
        box.Complete();
        box.Publish(IpcEnvelope.Create(IpcKind.Event, IpcTypes.DisplayChanged, new DisplayListPayload(1, [])), Targets.Control);
    }
}


public class OutboxCoalescingTests
{
    private static IpcEnvelope State(long seq) =>
        IpcEnvelope.Create(IpcKind.Event, IpcTypes.OutputStateChanged, new OutputStateChangedPayload(seq, OutputState.Initial, new OutputWindowStatus(false, null, false, false, null, true, null, null, DisplayLostPolicy.WarnAndWait, false, false, [])));

    private static IpcEnvelope Displays(long seq) => IpcEnvelope.Create(IpcKind.Event, IpcTypes.DisplayChanged, new DisplayListPayload(seq, []));
    private static IpcEnvelope Reply(string id) => IpcEnvelope.Create(IpcKind.Reply, IpcTypes.DisplayList, new IpcReply(true, new DisplayListPayload(1, []), null), id);

    private static async Task<List<IpcEnvelope>> Drain(Outbox outbox)
    {
        outbox.Complete();
        var list = new List<IpcEnvelope>();
        await foreach (var (_, json) in outbox.ReadAllAsync(CancellationToken.None))
        {
            Assert.True(IpcSerializer.TryParse(json, out var env, out _));
            list.Add(env!);
        }
        return list;
    }

    [Fact]
    public async Task When_full_only_the_latest_snapshot_of_each_type_is_kept_and_other_messages_are_untouched()
    {
        var outbox = new Outbox(NullLogger<Outbox>.Instance, capacity: 10);
        outbox.Publish(Reply("r-1"), Targets.Control);                          // los replies NO se compactan
        for (var i = 1; i <= 4; i++) outbox.Publish(State(i), Targets.All);
        for (var i = 1; i <= 4; i++) outbox.Publish(Displays(i), Targets.Control);
        outbox.Publish(Reply("r-2"), Targets.Control);                          // 10 mensajes: lleno
        outbox.Publish(State(5), Targets.All);                                  // dispara la compactación

        var got = await Drain(outbox);

        Assert.Equal(["r-1", "r-2"], got.Where(e => e.Kind == IpcKind.Reply).Select(e => e.Id));
        Assert.Equal([5L], got.Where(e => e.Type == IpcTypes.OutputStateChanged).Select(e => e.GetPayload<OutputStateChangedPayload>().Seq));
        Assert.Equal([4L], got.Where(e => e.Type == IpcTypes.DisplayChanged).Select(e => e.GetPayload<DisplayListPayload>().Seq));
    }

    [Fact]
    public async Task Order_is_preserved_after_compaction()
    {
        var outbox = new Outbox(NullLogger<Outbox>.Instance, capacity: 4);
        outbox.Publish(Reply("a"), Targets.Control);
        outbox.Publish(State(1), Targets.All);
        outbox.Publish(State(2), Targets.All);
        outbox.Publish(Reply("b"), Targets.Control);
        outbox.Publish(State(3), Targets.All);   // lleno → compacta

        var got = await Drain(outbox);

        Assert.Equal(["reply:a", "reply:b", "output.stateChanged"], got.Select(e => e.Kind == IpcKind.Reply ? $"reply:{e.Id}" : e.Type));   // el snapshot nuevo sustituye a los viejos y queda al final
        Assert.Equal(3L, got[^1].GetPayload<OutputStateChangedPayload>().Seq);
    }

    [Fact]
    public async Task Snapshots_for_different_targets_are_not_merged()
    {
        var outbox = new Outbox(NullLogger<Outbox>.Instance, capacity: 3);
        outbox.Publish(State(1), Targets.Control);
        outbox.Publish(State(2), Targets.Output);
        outbox.Publish(State(3), Targets.Control);
        outbox.Publish(State(4), Targets.Output);   // lleno → compacta por (tipo, destino)

        var got = await Drain(outbox);

        Assert.Equal([3L, 4L], got.Select(e => e.GetPayload<OutputStateChangedPayload>().Seq));
    }

    [Fact]
    public async Task If_nothing_can_be_compacted_the_oldest_message_is_dropped()
    {
        var outbox = new Outbox(NullLogger<Outbox>.Instance, capacity: 3);
        foreach (var id in new[] { "a", "b", "c", "d" }) outbox.Publish(Reply(id), Targets.Control);

        Assert.Equal(["b", "c", "d"], (await Drain(outbox)).Select(e => e.Id));
    }

    [Fact]
    public async Task A_reader_waiting_on_an_empty_queue_wakes_up_for_new_messages_and_for_completion()
    {
        var outbox = new Outbox(NullLogger<Outbox>.Instance);
        var received = new List<string>();
        var reader = Task.Run(async () =>
        {
            await foreach (var (_, json) in outbox.ReadAllAsync(CancellationToken.None)) received.Add(json);
        });

        await Task.Delay(100);
        outbox.Publish(Reply("x"), Targets.Control);
        await Task.Delay(100);
        outbox.Complete();

        await reader.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(received);
    }
}
