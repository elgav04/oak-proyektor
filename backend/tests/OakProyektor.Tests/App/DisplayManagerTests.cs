using Microsoft.Extensions.Logging.Abstractions;
using OakProyektor.App;
using OakProyektor.Core.Display;
using OakProyektor.Core.Output;
using OakProyektor.Ipc;
using OakProyektor.Ipc.Messages;

namespace OakProyektor.Tests.App;

public class DisplayManagerTests
{
    private sealed class DirectUi : IUiDispatcher
    {
        public void Invoke(Action action) => action();
    }

    private sealed class FakeProvider : IMonitorProvider
    {
        public List<MonitorArea> Monitors { get; } = [new(new ScreenRect(0, 0, 1920, 1080), 1.25), new(new ScreenRect(1920, 0, 1920, 1080), 1)];
        public (int X, int Y) PanelCenter { get; set; } = (960, 540);
        public MonitorSnapshot Read() => new([.. Monitors], PanelCenter.X, PanelCenter.Y);
    }

    private const string PanelId = "0,0,1920x1080";
    private const string ProjectorId = "1920,0,1920x1080";

    private static DisplayManager Create(FakeProvider provider, out Outbox outbox)
    {
        outbox = new Outbox(NullLogger<Outbox>.Instance);
        return new DisplayManager(provider, new DirectUi(), outbox, NullLogger<DisplayManager>.Instance);
    }

    [Fact]
    public void Monitors_get_a_stable_id_derived_from_their_area_and_are_marked_primary_and_panel()
    {
        var list = Create(new FakeProvider(), out _).Refresh();

        Assert.Equal([PanelId, ProjectorId], list.Select(d => d.Id));
        Assert.Equal([true, false], list.Select(d => d.IsPrimary));
        Assert.Equal([true, false], list.Select(d => d.HostsPanel));
    }

    [Fact]
    public void The_id_does_not_depend_on_enumeration_order()
    {
        var p = new FakeProvider();
        p.Monitors.Reverse();   // Windows enumera en otro orden tras reconectar
        var dm = Create(p, out _);
        dm.Refresh();

        Assert.Equal(ProjectorId, dm.Resolve(ProjectorId).Display!.Id);
        Assert.Equal(PanelId, dm.Resolve(PanelId).Display!.Id);
    }

    [Fact]
    public void Panel_dragged_to_the_other_monitor_changes_who_hosts_it()
    {
        var p = new FakeProvider { PanelCenter = (2880, 540) };
        Assert.Equal([false, true], Create(p, out _).Refresh().Select(d => d.HostsPanel));
    }

    [Fact]
    public void Minimized_panel_off_screen_hosts_nothing()
    {
        var p = new FakeProvider { PanelCenter = (-31000, -31000) };
        Assert.DoesNotContain(Create(p, out _).Refresh(), d => d.HostsPanel);
    }

    [Fact]
    public void Resolve_uses_the_requested_id_and_only_null_means_automatic()
    {
        var dm = Create(new FakeProvider(), out _);
        dm.Refresh();
        Assert.Equal(PanelId, dm.Resolve(PanelId).Display!.Id);
        Assert.Equal(ProjectorId, dm.Resolve(null).Display!.Id);

        // I-B: un ID desconocido YA NO cae al automático (antes abría la salida a pantalla completa en un monitor que nadie eligió).
        var unknown = dm.Resolve("9,9,9x9");
        Assert.Equal(DisplayResolutionKind.NotFound, unknown.Kind);
        Assert.Null(unknown.Display);
    }

    [Fact]
    public void With_a_single_monitor_there_is_no_candidate_for_fullscreen()
    {
        var p = new FakeProvider();
        p.Monitors.RemoveAt(1);
        var dm = Create(p, out _);
        dm.Refresh();
        Assert.Null(dm.Resolve(null).Display);
    }

    [Fact]
    public async Task Seq_grows_only_when_the_monitor_list_changes_and_the_snapshot_reply_carries_it()
    {
        var p = new FakeProvider();
        var dm = Create(p, out var outbox);

        dm.Refresh();
        var first = dm.Snapshot().Seq;
        dm.Refresh();
        dm.Refresh();
        Assert.Equal(first, dm.Snapshot().Seq);                 // sin cambios no hay secuencia nueva

        p.Monitors.RemoveAt(1);
        dm.Refresh();
        var second = dm.Snapshot();
        Assert.True(second.Seq > first);
        Assert.Single(second.Displays);

        outbox.Complete();
        var seqs = new List<long>();
        await foreach (var (_, json) in outbox.ReadAllAsync(CancellationToken.None))
        {
            Assert.True(IpcSerializer.TryParse(json, out var env, out _));
            seqs.Add(env!.GetPayload<DisplayListPayload>().Seq);
        }
        Assert.Equal([first, second.Seq], seqs);                // un evento por cambio real, en orden
    }

    [Fact]
    public async Task Polling_raises_Polled_every_cycle()
    {
        var p = new FakeProvider();
        var dm = Create(p, out _);
        var polls = 0;
        var seenCounts = new List<int>();
        dm.Polled += list => { polls++; seenCounts.Add(list.Count); };

        using var cts = new CancellationTokenSource();
        var task = dm.StartPolling(cts.Token);
        await Task.Delay(1300);
        var pollsWithoutChanges = polls;
        p.Monitors.RemoveAt(1);
        await Task.Delay(1100);
        cts.Cancel();
        await task;

        Assert.True(pollsWithoutChanges >= 2, $"ciclos sin cambios: {pollsWithoutChanges}");
        Assert.Contains(1, seenCounts);
    }
}
