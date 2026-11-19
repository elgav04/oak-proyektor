using Microsoft.Extensions.Logging.Abstractions;
using OakProyektor.App;
using OakProyektor.Core.Display;
using OakProyektor.Core.Output;

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

    private static DisplayManager Create(FakeProvider provider, out Outbox outbox)
    {
        outbox = new Outbox(NullLogger<Outbox>.Instance);
        return new DisplayManager(provider, new DirectUi(), outbox, NullLogger<DisplayManager>.Instance);
    }

    [Fact]
    public void Marks_the_primary_monitor_and_the_one_hosting_the_panel()
    {
        var p = new FakeProvider();
        var list = Create(p, out _).Refresh();

        Assert.Equal([true, false], list.Select(d => d.IsPrimary));
        Assert.Equal([true, false], list.Select(d => d.HostsPanel));
        Assert.Equal([0, 1], list.Select(d => d.Index));
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
    public void Resolve_prefers_the_requested_index_else_the_first_monitor_without_the_panel()
    {
        var dm = Create(new FakeProvider(), out _);
        dm.Refresh();
        Assert.Equal(0, dm.Resolve(0)!.Index);
        Assert.Equal(1, dm.Resolve(null)!.Index);
        Assert.Equal(1, dm.Resolve(9)!.Index);      // índice inexistente: cae al automático
    }

    [Fact]
    public void With_a_single_monitor_there_is_no_candidate_for_fullscreen()
    {
        var p = new FakeProvider();
        p.Monitors.RemoveAt(1);
        var dm = Create(p, out _);
        dm.Refresh();
        Assert.Null(dm.Resolve(null));
    }

    [Fact]
    public async Task Polling_raises_Polled_every_cycle_and_publishes_display_changed_only_on_real_changes()
    {
        var p = new FakeProvider();
        var dm = Create(p, out var outbox);
        var polls = 0;
        var seenCounts = new List<int>();
        dm.Polled += list => { polls++; seenCounts.Add(list.Count); };

        using var cts = new CancellationTokenSource();
        var task = dm.StartPolling(cts.Token);
        await Task.Delay(1300);                 // ~2 ciclos de 500 ms sin cambios
        var pollsWithoutChanges = polls;
        p.Monitors.RemoveAt(1);                  // se "desconecta" el proyector
        await Task.Delay(1100);
        cts.Cancel();
        await task;
        outbox.Complete();

        Assert.True(pollsWithoutChanges >= 2, $"ciclos sin cambios: {pollsWithoutChanges}");
        Assert.Contains(1, seenCounts);          // llegó a verse la lista con un solo monitor
        var events = new List<string>();
        await foreach (var (_, json) in outbox.ReadAllAsync(CancellationToken.None)) events.Add(json);
        Assert.Single(events);                   // exactamente un display.changed (el de la desconexión)
        Assert.Contains("display.changed", events[0]);
    }
}
