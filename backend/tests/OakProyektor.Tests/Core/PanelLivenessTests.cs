using Microsoft.Extensions.Logging.Abstractions;
using OakProyektor.App;
using OakProyektor.Core.Output;

namespace OakProyektor.Tests.Core;

public class PanelLivenessTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_panel_that_keeps_talking_is_never_flagged()
    {
        var t = new PanelLivenessTracker();
        t.Reset(T0);
        for (var s = 4; s <= 120; s += 4)
        {
            t.Touch(T0.AddSeconds(s));                       // un latido cada 4 s
            Assert.False(t.Evaluate(T0.AddSeconds(s + 1)).Unresponsive);
        }
    }

    [Fact]
    public void Ten_seconds_of_silence_flags_the_panel_once_and_the_next_message_clears_it()
    {
        var t = new PanelLivenessTracker();
        t.Reset(T0);
        t.Touch(T0);
        Assert.False(t.Evaluate(T0.AddSeconds(9.9)).Unresponsive);       // un latido tardío no da falsa alarma

        var down = t.Evaluate(T0.AddSeconds(10.5));
        Assert.Equal((true, true), (down.Unresponsive, down.Changed));
        Assert.True(down.Silence > TimeSpan.FromSeconds(10));

        var still = t.Evaluate(T0.AddSeconds(30));
        Assert.Equal((true, false), (still.Unresponsive, still.Changed)); // sigue igual: no se vuelve a anunciar

        t.Touch(T0.AddSeconds(31));
        var back = t.Evaluate(T0.AddSeconds(31.5));
        Assert.Equal((false, true), (back.Unresponsive, back.Changed));
    }

    [Fact]
    public void A_page_that_never_loads_is_flagged_counting_from_the_reset_baseline()
    {
        var t = new PanelLivenessTracker();
        t.Reset(T0);                                                      // arranque o recarga: aún no ha hablado
        Assert.False(t.Evaluate(T0.AddSeconds(5)).Unresponsive);
        Assert.True(t.Evaluate(T0.AddSeconds(11)).Unresponsive);

        t.Reset(T0.AddSeconds(12));                                       // se recarga: el silencio vuelve a contar desde ahora
        var afterReload = t.Evaluate(T0.AddSeconds(13));
        Assert.Equal((false, true), (afterReload.Unresponsive, afterReload.Changed));   // dejó de estar marcado: espera a que la página nueva hable
    }

    [Fact]
    public void The_timeout_is_configurable()
    {
        var t = new PanelLivenessTracker(TimeSpan.FromSeconds(3));
        t.Reset(T0);
        Assert.True(t.Evaluate(T0.AddSeconds(4)).Unresponsive);
    }

    [Theory]
    [InlineData(MasterMode.Live, MasterMode.Black, MasterMode.Black)]
    [InlineData(MasterMode.Black, MasterMode.Black, MasterMode.Live)]          // interruptor: vuelve a Live
    [InlineData(MasterMode.Logo, MasterMode.Black, MasterMode.Black)]          // de otro modo, salta al pedido
    [InlineData(MasterMode.ClearText, MasterMode.ClearText, MasterMode.Live)]
    [InlineData(MasterMode.Live, MasterMode.Logo, MasterMode.Logo)]
    [InlineData(MasterMode.Freeze, MasterMode.Logo, MasterMode.Logo)]
    public void Master_shortcuts_are_toggles(MasterMode current, MasterMode requested, MasterMode expected)
    {
        Assert.Equal(expected, MasterShortcut.Next(current, requested));
    }

    [Fact]
    public void Pressing_a_master_shortcut_twice_always_returns_to_live_from_any_state()
    {
        foreach (var start in Enum.GetValues<MasterMode>())
            foreach (var key in new[] { MasterMode.Black, MasterMode.ClearText, MasterMode.Logo })
            {
                if (start == key) continue;                  // pulsar el atajo del modo ya activo es justo volver a Live (probado arriba)
                var once = MasterShortcut.Next(start, key);
                var twice = MasterShortcut.Next(once, key);
                Assert.Equal(key, once);
                Assert.Equal(MasterMode.Live, twice);
            }
    }

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = T0;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void The_watchdog_tells_the_output_controller_when_the_panel_goes_silent_and_when_it_comes_back()
    {
        var output = new OakProyektor.Tests.App.FakeOutputController();
        var time = new ManualTime();
        var wd = new PanelWatchdog(new OakProyektor.Tests.App.FakeDisplayManager(), output, time, NullLogger<PanelWatchdog>.Instance);
        wd.Reset();

        time.Now = T0.AddSeconds(4); wd.Touch(); wd.Evaluate();
        Assert.Empty(output.Calls);

        time.Now = T0.AddSeconds(20); wd.Evaluate();                      // 16 s sin mensajes
        Assert.Equal(["panelUnresponsive:True"], output.Calls);

        time.Now = T0.AddSeconds(25); wd.Evaluate();                      // sigue sin respuesta: no se repite el aviso
        Assert.Single(output.Calls);

        time.Now = T0.AddSeconds(26); wd.Touch(); wd.Evaluate();
        Assert.Equal(["panelUnresponsive:True", "panelUnresponsive:False"], output.Calls);
    }
}
