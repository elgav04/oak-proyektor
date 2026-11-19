using Microsoft.Extensions.Logging.Abstractions;
using OakProyektor.App;
using OakProyektor.Ipc;
using OakProyektor.Ipc.Messages;

namespace OakProyektor.Tests.App;

public class SequencedSnapshotPublisherTests
{
    private sealed record Snap(int Value);

    private static (SequencedSnapshotPublisher<Snap> Publisher, Outbox Outbox) Create()
    {
        var outbox = new Outbox(NullLogger<Outbox>.Instance);
        // Se reutiliza el payload de monitores como contenedor de (seq, valor) solo para poder leerlo de vuelta.
        var publisher = new SequencedSnapshotPublisher<Snap>(outbox, IpcTypes.DisplayChanged, Targets.Control,
            (seq, s) => new TestPayload(seq, s.Value));
        return (publisher, outbox);
    }

    private sealed record TestPayload(long Seq, int Value);

    private static async Task<List<(long Seq, int Value)>> Drain(Outbox outbox)
    {
        outbox.Complete();
        var list = new List<(long, int)>();
        await foreach (var (_, json) in outbox.ReadAllAsync(CancellationToken.None))
        {
            Assert.True(IpcSerializer.TryParse(json, out var env, out _));
            var p = env!.GetPayload<TestPayload>();
            list.Add((p.Seq, p.Value));
        }
        return list;
    }

    [Fact]
    public async Task Publishes_only_when_something_changed_and_the_sequence_grows_by_one_each_time()
    {
        var (pub, outbox) = Create();

        Assert.True(pub.Publish(() => new Snap(1)));
        Assert.False(pub.Publish(() => new Snap(1)));     // igual: nada
        Assert.True(pub.Publish(() => new Snap(2)));
        Assert.False(pub.Publish(() => new Snap(2)));

        Assert.Equal([(1L, 1), (2L, 2)], await Drain(outbox));
    }

    [Fact]
    public async Task A_reply_read_publishes_a_pending_change_first_so_reply_and_event_never_disagree()
    {
        var (pub, outbox) = Create();
        pub.Publish(() => new Snap(1));

        var reply = pub.Read(() => new Snap(2), (seq, s) => (seq, s.Value));   // cambio aún no publicado

        Assert.Equal((2L, 2), reply);                                          // el reply ya lleva la secuencia nueva…
        Assert.Equal([(1L, 1), (2L, 2)], await Drain(outbox));                 // …y su evento ya está en la cola
    }

    [Fact]
    public async Task Concurrent_publishers_never_put_an_older_snapshot_behind_a_newer_one()
    {
        var (pub, outbox) = Create();
        var current = 0;   // el "estado real", que muchos hilos cambian y publican

        var threads = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
            {
                Interlocked.Increment(ref current);
                pub.Publish(() => new Snap(Volatile.Read(ref current)));
            }
        })).ToArray();
        await Task.WhenAll(threads);

        var events = await Drain(outbox);

        Assert.NotEmpty(events);
        Assert.Equal(events.Select(e => e.Seq).Order(), events.Select(e => e.Seq));        // secuencias estrictamente crecientes en la cola
        Assert.Equal(events.Select(e => e.Seq).Distinct().Count(), events.Count);
        Assert.Equal(events.Select(e => e.Value).Order(), events.Select(e => e.Value));    // y el CONTENIDO nunca retrocede
        Assert.Equal(8 * 500, events[^1].Value);                                           // el último evento refleja el estado final
    }
}
