using OakProyektor.Core.Output;

namespace OakProyektor.Tests.Alerts;

/// <summary>
/// La carrera del revisor: un disparo ANTICIPADO del temporizador (<see cref="AlertExpiry.RetryIn"/>) que se rearma desde su hilo justo cuando llega un «mostrar» de una alerta nueva.
/// Se reproduce con el motor real, el reloj falso y un «anfitrión» que hace lo mismo que <c>OutputController.Alerts.cs</c> (armar por <see cref="AlertTimerGuard"/>, cancelar al quitar).
/// Sin la regla, el rearmado tardío de la alerta vieja reemplazaba el temporizador de la nueva y esta no caducaba nunca (ni a los 30 min del tope).
/// </summary>
public class AlertTimerGuardTests
{
    /// <summary>El pegamento del controlador, sin hilos ni Photino: un solo «temporizador» (generación + plazo) que se reemplaza al armar.</summary>
    private sealed class Host
    {
        private readonly FakeClock _clock;
        public readonly AlertEngine Engine;
        public readonly AlertTimerGuard Guard = new();
        public OutputState State = OutputState.Initial.WithMaster(MasterMode.Live);
        public (long Generation, TimeSpan Delay)? Timer { get; private set; }

        public Host(FakeClock clock) { _clock = clock; Engine = new AlertEngine(clock); }

        public void Arm(long generation, TimeSpan delay) => Guard.TryArm(generation, () => Timer = (generation, delay));
        public void Cancel(long generation) => Guard.Cancel(generation, () => Timer = null);

        public long Show(string text, int seconds, long order)
        {
            var outcome = Engine.Show(State, text, AlertMode.Band, seconds, AlertTheme.Default, order);
            Assert.True(outcome.Applied);
            State = outcome.State;
            Arm(outcome.Generation, outcome.Duration);
            return outcome.Generation;
        }

        public void Hide(long order)
        {
            State = Engine.Hide(State, order);
            Cancel(Engine.Generation);
        }

        /// <summary>Lo que hace <c>OnAlertTimer</c> hasta decidir el rearmado: devuelve el rearmado PENDIENTE (que el hilo ejecutará después), o null si caducó / no hacía nada.</summary>
        public Action? FireUntilRearm(long generation)
        {
            var result = Engine.Expire(State, generation);
            if (result.State is { } next) State = next;
            return result.RetryIn is { } retry ? () => Arm(generation, retry) : null;
        }
    }

    private readonly FakeClock _clock = new();

    [Fact]
    public void An_early_firing_that_rearms_AFTER_a_new_show_cannot_replace_the_new_alerts_timer_and_the_new_alert_expires_on_time()
    {
        var host = new Host(_clock);
        var g1 = host.Show("AVISO DE PRUEBA 1", seconds: 15, order: 1);
        _clock.Advance(TimeSpan.FromSeconds(14.95));                    // el temporizador de la 1.ª dispara 50 ms antes de tiempo
        var pendingRearm = host.FireUntilRearm(g1);
        Assert.NotNull(pendingRearm);                                   // su hilo todavía no rearmó…

        var g2 = host.Show("AVISO DE PRUEBA 2", seconds: 60, order: 2); // …y llega el «mostrar» de la alerta nueva, que arma SU plazo
        Assert.Equal((g2, TimeSpan.FromSeconds(60)), host.Timer);

        pendingRearm!();                                                // ahora el hilo viejo rearma: debe ignorarse
        Assert.Equal((g2, TimeSpan.FromSeconds(60)), host.Timer);

        _clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Null(host.Engine.Expire(host.State, g2).State);          // no antes de tiempo
        _clock.Advance(TimeSpan.FromSeconds(1));
        var due = host.Engine.Expire(host.State, host.Timer!.Value.Generation);
        Assert.NotNull(due.State);
        Assert.Null(due.State!.Overlays.Alert);                         // la alerta nueva caduca a su hora
    }

    [Fact]
    public void The_same_race_with_until_removed_still_ends_at_the_30_minute_cap()
    {
        var host = new Host(_clock);
        var g1 = host.Show("AVISO DE PRUEBA 1", seconds: 15, order: 1);
        _clock.Advance(TimeSpan.FromSeconds(14.99));
        var pendingRearm = host.FireUntilRearm(g1);
        var g2 = host.Show("AVISO DE PRUEBA 2", seconds: 0, order: 2);  // «hasta quitarla»
        pendingRearm!();

        Assert.Equal((g2, TimeSpan.FromMinutes(30)), host.Timer);       // el plazo armado es el tope, no el del temporizador viejo
        _clock.Advance(TimeSpan.FromMinutes(29));
        Assert.Null(host.Engine.Expire(host.State, g2).State);
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(host.Engine.Expire(host.State, host.Timer!.Value.Generation).State!.Overlays.Alert);
    }

    [Fact]
    public void A_late_rearm_after_the_operator_hid_the_alert_does_not_resurrect_a_timer()
    {
        var host = new Host(_clock);
        var g1 = host.Show("AVISO DE PRUEBA 1", seconds: 15, order: 1);
        _clock.Advance(TimeSpan.FromSeconds(14.99));
        var pendingRearm = host.FireUntilRearm(g1);
        host.Hide(order: 2);                                            // el atajo global quita la alerta mientras el hilo del temporizador está a medias
        Assert.Null(host.Timer);

        pendingRearm!();
        Assert.Null(host.Timer);                                        // sigue sin temporizador: nada que caducar, nada que ignorar después
    }

    [Fact]
    public void Arming_in_order_replaces_the_timer_and_the_guard_remembers_the_last_generation()
    {
        var host = new Host(_clock);
        var g1 = host.Show("AVISO DE PRUEBA 1", seconds: 15, order: 1);
        var g2 = host.Show("AVISO DE PRUEBA 2", seconds: 30, order: 2);
        Assert.True(g2 > g1);
        Assert.Equal(g2, host.Guard.Last);
        Assert.Equal((g2, TimeSpan.FromSeconds(30)), host.Timer);
        host.Arm(g2, TimeSpan.FromSeconds(1));                          // la MISMA generación sí puede rearmar (el reintento de un disparo anticipado)
        Assert.Equal((g2, TimeSpan.FromSeconds(1)), host.Timer);
    }

    [Fact]
    public void Many_threads_arming_generations_out_of_order_never_leave_a_lower_generation_armed_after_a_higher_one()
    {
        var guard = new AlertTimerGuard();
        var armed = new List<long>();
        Parallel.For(1, 2001, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            var generation = (i * 7919L) % 2000 + 1;                    // orden revuelto
            guard.TryArm(generation, () => armed.Add(generation));      // la acción corre bajo el candado: la lista no necesita otro
        });
        Assert.Equal(armed.OrderBy(g => g), armed);                     // lo armado nunca retrocede
        Assert.Equal(armed.Max(), guard.Last);
    }
}
