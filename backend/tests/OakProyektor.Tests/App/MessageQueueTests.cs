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
        box.Publish(IpcEnvelope.Create(IpcKind.Event, IpcTypes.OutputStateChanged, new OutputStateChangedPayload(OutputState.Initial, new OutputWindowStatus(false, null, false))), Targets.All);
        box.Publish(IpcEnvelope.Create(IpcKind.Event, IpcTypes.DisplayChanged, new DisplayListPayload([])), Targets.Control);
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
        box.Publish(IpcEnvelope.Create(IpcKind.Event, IpcTypes.DisplayChanged, new DisplayListPayload([])), Targets.Control);
    }
}
