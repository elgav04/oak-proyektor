using OakProyektor.Core.Output;

namespace OakProyektor.Tests.Alerts;

/// <summary>Reloj controlable: la marca monótona y la hora UTC avanzan solo cuando el test lo dice.</summary>
public sealed class FakeClock : TimeProvider
{
    private long _ticks;
    private DateTimeOffset _utc = new(2026, 11, 29, 10, 0, 0, TimeSpan.Zero);
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => _ticks;
    public override DateTimeOffset GetUtcNow() => _utc;
    public void Advance(TimeSpan by) { _ticks += by.Ticks; _utc += by; }
    /// <summary>Cambio de hora de Windows: la hora UTC salta, la marca monótona NO.</summary>
    public void JumpWallClock(TimeSpan by) => _utc += by;
}

/// <summary>Reglas de transición y de reloj de la alerta (<see cref="AlertEngine"/>), con un reloj falso. Todo con textos sintéticos.</summary>
public class AlertEngineTests
{
    private static readonly AlertTheme Theme = AlertTheme.Default;
    private readonly FakeClock _clock = new();
    private readonly AlertEngine _engine;
    private OutputState _state = OutputState.Initial.WithMaster(MasterMode.Live);

    public AlertEngineTests() => _engine = new AlertEngine(_clock);

    private AlertShowOutcome Show(string text = "AVISO DE PRUEBA 1", int seconds = 30, long order = 1, AlertMode mode = AlertMode.Band)
    {
        var outcome = _engine.Show(_state, text, mode, seconds, Theme, order);
        if (outcome.Applied) _state = outcome.State;
        return outcome;
    }

    [Fact]
    public void Showing_puts_the_alert_in_the_overlays_and_touches_nothing_else()
    {
        var loaded = OutputState.Initial.WithMaster(MasterMode.Live)
            .WithBackground(new BackgroundLayer(BackgroundKind.Video, "#112233", "file:///C:/v/a.mp4", "a.mp4", true, 600, null))
            .WithSlide(new Slide("s1", [new TextBlock("texto")], TextTheme.Default), 400);
        var outcome = _engine.Show(loaded, "AVISO DE PRUEBA 1", AlertMode.Band, 30, Theme, 1);

        Assert.True(outcome.Applied);
        var alert = outcome.State.Overlays.Alert!;
        Assert.Equal(("AVISO DE PRUEBA 1", AlertMode.Band, false), (alert.Text, alert.Mode, alert.Sticky));
        Assert.Equal("2026-11-29T10:00:30.000Z", alert.ExpiresAt);
        Assert.Same(loaded.Content, outcome.State.Content);          // el slide (y su crossfade, que se dispara por su id) ni se toca
        Assert.Same(loaded.Background, outcome.State.Background);    // el fondo (y su video) tampoco
        Assert.Equal((loaded.Master, loaded.Revision), (outcome.State.Master, outcome.State.Revision));
        Assert.Equal(TimeSpan.FromSeconds(30), outcome.Duration);
    }

    [Fact]
    public void A_different_alert_replaces_the_current_one_with_a_new_id_and_the_same_one_keeps_its_id()
    {
        var first = Show("AVISO DE PRUEBA 1").State.Overlays.Alert!;
        _clock.Advance(TimeSpan.FromSeconds(10));
        var again = Show("AVISO DE PRUEBA 1").State.Overlays.Alert!;
        var other = Show("AVISO DE PRUEBA 2").State.Overlays.Alert!;
        var changedMode = Show("AVISO DE PRUEBA 2", mode: AlertMode.Ticker).State.Overlays.Alert!;

        Assert.Equal(first.Id, again.Id);                // mismo texto, modo y tema: la salida no parpadea ni reinicia el desplazamiento
        Assert.NotEqual(again.ExpiresAt, first.ExpiresAt); // pero el plazo se renueva
        Assert.NotEqual(first.Id, other.Id);
        Assert.NotEqual(other.Id, changedMode.Id);
        Assert.Equal("AVISO DE PRUEBA 2", _state.Overlays.Alert!.Text);   // una sola alerta activa: reemplazo
    }

    [Fact]
    public void The_clock_removes_the_alert_exactly_when_the_duration_is_over_and_never_before()
    {
        var outcome = Show(seconds: 30);
        _clock.Advance(TimeSpan.FromSeconds(29.9));
        var early = _engine.Expire(_state, outcome.Generation);
        Assert.Null(early.State);
        Assert.InRange(early.RetryIn!.Value.TotalMilliseconds, 99, 101);   // un disparo anticipado se rearma; no se quita antes de tiempo
        Assert.NotNull(_state.Overlays.Alert);

        _clock.Advance(TimeSpan.FromMilliseconds(100));
        var due = _engine.Expire(_state, outcome.Generation);
        Assert.NotNull(due.State);
        Assert.Null(due.State!.Overlays.Alert);
        Assert.Null(due.RetryIn);
    }

    [Fact]
    public void A_stale_timer_of_a_replaced_alert_cannot_remove_the_new_one()
    {
        var first = Show("AVISO DE PRUEBA 1", seconds: 15);
        _clock.Advance(TimeSpan.FromSeconds(14));
        var second = Show("AVISO DE PRUEBA 2", seconds: 60);
        _clock.Advance(TimeSpan.FromSeconds(2));   // el plazo del primero venció, el del segundo no

        Assert.NotEqual(first.Generation, second.Generation);
        var stale = _engine.Expire(_state, first.Generation);
        Assert.Null(stale.State);
        Assert.Null(stale.RetryIn);
        Assert.Equal("AVISO DE PRUEBA 2", _state.Overlays.Alert!.Text);
    }

    [Fact]
    public void A_timer_firing_after_the_operator_hid_the_alert_does_nothing_and_a_new_show_starts_a_fresh_deadline()
    {
        var first = Show(seconds: 15);
        _state = _engine.Hide(_state, 2);
        Assert.Null(_state.Overlays.Alert);
        _clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Null(_engine.Expire(_state, first.Generation).State);

        var second = Show("AVISO DE PRUEBA 2", seconds: 15, order: 3);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(_engine.Expire(_state, first.Generation).State);       // el temporizador viejo, otra vez: nada
        Assert.NotNull(_state.Overlays.Alert);
        _clock.Advance(TimeSpan.FromSeconds(15));
        Assert.NotNull(_engine.Expire(_state, second.Generation).State);
    }

    [Fact]
    public void Until_removed_is_capped_at_30_minutes()
    {
        var outcome = Show(seconds: 0);
        Assert.True(outcome.State.Overlays.Alert!.Sticky);
        Assert.Equal(TimeSpan.FromMinutes(30), outcome.Duration);
        _clock.Advance(TimeSpan.FromMinutes(29));
        Assert.Null(_engine.Expire(_state, outcome.Generation).State);
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(_engine.Expire(_state, outcome.Generation).State!.Overlays.Alert);
    }

    [Fact]
    public void A_wall_clock_jump_does_not_lengthen_or_shorten_the_alert()
    {
        var outcome = Show(seconds: 30);
        _clock.JumpWallClock(TimeSpan.FromHours(3));      // Windows cambia la hora
        Assert.Null(_engine.Expire(_state, outcome.Generation).State);   // sigue sin vencer: manda el reloj monótono
        _clock.Advance(TimeSpan.FromSeconds(30));
        Assert.NotNull(_engine.Expire(_state, outcome.Generation).State);
        _clock.JumpWallClock(TimeSpan.FromHours(-5));
    }

    [Fact]
    public void Hiding_is_idempotent_and_keeps_the_rest_of_the_state()
    {
        Show();
        var hidden = _engine.Hide(_state, 5);
        Assert.Null(hidden.Overlays.Alert);
        Assert.Same(hidden, _engine.Hide(hidden, 6));
        Assert.Equal(_state.Master, hidden.Master);
    }

    [Fact]
    public void A_show_that_arrived_before_the_hide_is_ignored_even_if_processed_after_it()
    {
        // El operador pulsa el atajo global (orden 10) y un «mostrar» ya encolado (orden 7) llega después: no debe revivir la alerta.
        Show(order: 3);
        _state = _engine.Hide(_state, 10);
        var late = _engine.Show(_state, "AVISO DE PRUEBA 2", AlertMode.Band, 30, Theme, 7);
        Assert.False(late.Applied);
        Assert.Null(late.State.Overlays.Alert);

        var later = _engine.Show(_state, "AVISO DE PRUEBA 2", AlertMode.Band, 30, Theme, 11);   // pedido DESPUÉS de quitarla: sí se muestra
        Assert.True(later.Applied);
    }

    [Fact]
    public void Dropping_for_a_pause_removes_the_alert_but_does_not_block_a_later_show()
    {
        var shown = Show(order: 4);
        _state = _engine.Drop(_state);
        Assert.Null(_state.Overlays.Alert);
        Assert.False(_engine.IsArmed);
        Assert.Null(_engine.Expire(_state, shown.Generation).State);
        Assert.True(Show("AVISO DE PRUEBA 2", order: 2).Applied);   // la pausa no recuerda ningún orden
    }

    [Fact]
    public void WithAlert_with_an_identical_alert_changes_nothing()
    {
        var s = Show().State;
        Assert.Same(s, s.WithAlert(s.Overlays.Alert));
        Assert.Same(OutputState.Initial, OutputState.Initial.WithAlert(null));
    }
}
