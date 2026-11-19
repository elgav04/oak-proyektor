using OakProyektor.Core.Output;

namespace OakProyektor.Tests.Core;

/// <summary>
/// La pausa de emergencia es la red de seguridad: ninguna carrera puede deshacerla. Estas pruebas ejercitan la máquina de estados pura
/// (sin Photino) con las intercalaciones de hilos que el controlador real puede producir.
/// </summary>
public class OutputPauseStateTests
{
    private const OutputPauseReason Lost = OutputPauseReason.DisplayLost;
    private const OutputPauseReason Crossed = OutputPauseReason.PanelCrossed;
    private const OutputPauseReason Manual = OutputPauseReason.Manual;
    private const OutputPauseReason Emergency = OutputPauseReason.Emergency;

    private static OutputPauseState WithReadyWindow(long openOrder = 1)
    {
        var s = new OutputPauseState();
        s.BeginWindow(openOrder);
        s.MarkWindowReady();
        return s;
    }

    // ───────────────────────── I-A, escenario 1: «Mover / reabrir» y enseguida «Recuperar control» ─────────────────────────

    [Fact]
    public void Scenario1_existing_window_the_recover_is_handled_before_the_open_and_the_open_does_not_undo_it()
    {
        var s = WithReadyWindow();
        // El operador pulsó «Mover / reabrir» (llegó con el número 5) y enseguida «Recuperar control» (7). El carril de prioridad atiende el 7 primero.
        s.RequestPause(Manual, order: 7);
        Assert.True(s.IsPaused);

        // Después se atiende el open numerado 5, que quiere reanudar: NO puede deshacer una pausa posterior.
        Assert.Equal(OperatorResumeResult.Superseded, s.TryOperatorResume(order: 5));
        Assert.True(s.IsPaused);
        Assert.Equal(Manual, s.Reason);
    }

    [Fact]
    public void Scenario1_new_window_the_open_recreates_the_window_but_it_is_born_paused()
    {
        var s = WithReadyWindow();
        s.RequestPause(Emergency, order: 7);   // pausa sobre la ventana vieja
        s.WindowClosed();                       // el open la cierra para moverla (OnClosed borra la pausa de la ventana vieja...)

        var bornPaused = s.BeginWindow(order: 5);   // ...pero la ventana nueva se crea por una orden ANTERIOR a esa pausa

        Assert.True(bornPaused);
        Assert.True(s.IsPaused);
        Assert.Equal(Emergency, s.Reason);
    }

    [Fact]
    public void Scenario1_with_no_window_at_all_the_pause_is_still_remembered_by_the_queued_open()
    {
        var s = new OutputPauseState();
        var request = s.RequestPause(Manual, order: 7);   // no hay ventana (p. ej. se está cerrando para moverla)
        Assert.Equal(WindowPhase.None, request.Phase);

        Assert.True(s.BeginWindow(order: 5));
        Assert.Equal(Manual, s.Reason);
    }

    [Fact]
    public void A_later_open_by_the_operator_does_resume_on_purpose()
    {
        var s = WithReadyWindow();
        s.RequestPause(Manual, order: 7);

        Assert.Equal(OperatorResumeResult.Resumed, s.TryOperatorResume(order: 9));   // «Reanudar salida» pulsado DESPUÉS
        Assert.False(s.IsPaused);
        Assert.Null(s.Reason);
    }

    [Fact]
    public void A_later_open_creates_a_running_window_even_after_an_earlier_pause()
    {
        var s = WithReadyWindow();
        s.RequestPause(Emergency, order: 7);
        s.WindowClosed();

        Assert.False(s.BeginWindow(order: 9));
        Assert.False(s.IsPaused);
    }

    // ─────────────────────── I-A, escenario 2: pausa con la ventana a medio crear (sin handle) ───────────────────────

    [Fact]
    public void Scenario2_a_pause_while_the_window_is_being_created_changes_the_state_immediately_and_is_applied_when_it_exists()
    {
        var s = new OutputPauseState();
        s.BeginWindow(order: 5);                                   // la ventana se está creando: _hwnd == 0

        var request = s.RequestPause(Emergency, order: 7);

        Assert.True(request.Changed);                              // el estado (y el aviso al panel) cambian YA
        Assert.Equal(WindowPhase.Creating, request.Phase);         // ...pero aún no hay nada que minimizar
        Assert.True(s.IsPaused);
        Assert.Equal(Emergency, s.Reason);

        Assert.True(s.MarkWindowReady());                          // en cuanto la ventana existe, se debe minimizar
        Assert.Equal(WindowPhase.Ready, s.Phase);
    }

    [Fact]
    public void A_window_created_without_a_pending_pause_is_not_minimized()
    {
        var s = new OutputPauseState();
        s.BeginWindow(order: 5);
        Assert.False(s.MarkWindowReady());
        Assert.False(s.IsPaused);
    }

    [Fact]
    public void A_pause_on_a_ready_window_says_to_minimize_it_now()
    {
        var s = WithReadyWindow();
        Assert.Equal(WindowPhase.Ready, s.RequestPause(Emergency, order: 3).Phase);
    }

    [Fact]
    public void A_pause_requested_again_on_an_already_paused_window_reports_no_change_but_still_says_ready_so_it_can_be_minimized_again()
    {
        var s = WithReadyWindow();
        s.RequestPause(Emergency, order: 3);
        var again = s.RequestPause(Emergency, order: 4);   // p. ej. el operador restauró la ventana y pulsó el atajo otra vez
        Assert.False(again.Changed);
        Assert.Equal(WindowPhase.Ready, again.Phase);
    }

    // ───────────────────── I-A, escenario 3: la reanudación/pausa automática evaluada ANTES de que llegue el atajo ─────────────────────

    [Fact]
    public void Scenario3a_an_auto_resume_evaluated_before_the_emergency_arrived_is_cancelled()
    {
        var s = WithReadyWindow();
        s.RequestPause(Lost);                       // el proyector se desconectó
        // EvaluateLocked lee «pausada por DisplayLost» y decide Resume (el monitor volvió)... y justo entonces llega el atajo:
        s.RequestPause(Emergency, order: 7);

        Assert.False(s.TryAutoResume(Lost));        // la reanudación automática compara antes de escribir: ya no es DisplayLost
        Assert.True(s.IsPaused);
        Assert.Equal(Emergency, s.Reason);
    }

    [Fact]
    public void Scenario3b_an_automatic_pause_never_downgrades_an_emergency_and_so_cannot_make_it_auto_resume()
    {
        var s = WithReadyWindow();
        s.RequestPause(Emergency, order: 7);

        var request = s.RequestPause(Lost);          // el monitor desaparece mientras está en emergencia
        Assert.False(request.Changed);
        Assert.Equal(Emergency, s.Reason);           // NO pasa a DisplayLost

        Assert.False(s.TryAutoResume(Lost));         // y cuando el monitor vuelve, no se reanuda sola
        Assert.False(s.TryAutoResume(Crossed));
        Assert.True(s.IsPaused);
    }

    // ───────────────────────────────────── prioridad de motivos ─────────────────────────────────────

    [Theory]
    [InlineData(Manual, Lost)]
    [InlineData(Manual, Crossed)]
    [InlineData(Emergency, Lost)]
    [InlineData(Emergency, Crossed)]
    [InlineData(Emergency, Manual)]
    public void A_stronger_reason_is_never_downgraded(OutputPauseReason current, OutputPauseReason incoming)
    {
        var s = WithReadyWindow();
        s.RequestPause(current, order: 3);

        Assert.False(s.RequestPause(incoming, order: 4).Changed);
        Assert.Equal(current, s.Reason);
    }

    [Theory]
    [InlineData(Lost, Manual)]
    [InlineData(Lost, Emergency)]
    [InlineData(Crossed, Manual)]
    [InlineData(Crossed, Emergency)]
    [InlineData(Manual, Emergency)]
    public void A_stronger_reason_raises_the_level(OutputPauseReason current, OutputPauseReason incoming)
    {
        var s = WithReadyWindow();
        s.RequestPause(current, order: 3);

        Assert.True(s.RequestPause(incoming, order: 4).Changed);
        Assert.Equal(incoming, s.Reason);
    }

    [Fact]
    public void Between_automatic_reasons_the_first_one_stays()
    {
        var s = WithReadyWindow();
        s.RequestPause(Lost);
        Assert.False(s.RequestPause(Crossed).Changed);
        Assert.Equal(Lost, s.Reason);
    }

    // ─────────────────────────────── reanudación no automática tras Manual o Emergency ───────────────────────────────

    [Theory]
    [InlineData(Manual)]
    [InlineData(Emergency)]
    public void Manual_and_emergency_pauses_are_never_resumed_automatically(OutputPauseReason reason)
    {
        var s = WithReadyWindow();
        s.RequestPause(reason, order: 3);

        // Da igual qué motivo «evalúe» la política: el monitor volvió, el panel se fue... solo el operador reanuda.
        foreach (var evaluated in Enum.GetValues<OutputPauseReason>())
            Assert.False(s.TryAutoResume(evaluated), $"no debe reanudarse sola con {evaluated}");
        Assert.True(s.IsPaused);
        Assert.Equal(reason, s.Reason);
    }

    [Theory]
    [InlineData(Lost)]
    [InlineData(Crossed)]
    public void Automatic_pauses_do_resume_automatically_when_the_same_reason_was_evaluated(OutputPauseReason reason)
    {
        var s = WithReadyWindow();
        s.RequestPause(reason);

        Assert.True(s.TryAutoResume(reason));
        Assert.False(s.IsPaused);
    }

    [Fact]
    public void An_automatic_resume_for_a_different_reason_than_the_current_one_does_nothing()
    {
        var s = WithReadyWindow();
        s.RequestPause(Crossed);
        Assert.False(s.TryAutoResume(Lost));
        Assert.True(s.IsPaused);
    }

    [Fact]
    public void An_automatic_resume_when_not_paused_does_nothing()
    {
        Assert.False(WithReadyWindow().TryAutoResume(Lost));
    }

    [Fact]
    public void An_automatic_reason_cannot_be_used_to_resume_by_operator_order_numbers()
    {
        // Las pausas automáticas no llevan número: no pueden «adelantarse» a una orden del operador.
        var s = WithReadyWindow();
        s.RequestPause(Lost, order: 999);   // el número se ignora para los automáticos
        Assert.Equal(OperatorResumeResult.Resumed, s.TryOperatorResume(order: 2));
    }

    // ───────────────────────────────────── ciclo de vida de la ventana ─────────────────────────────────────

    [Fact]
    public void Closing_the_window_clears_the_pause_but_remembers_the_last_operator_pause_number()
    {
        var s = WithReadyWindow();
        s.RequestPause(Manual, order: 7);
        s.WindowClosed();

        Assert.False(s.IsPaused);
        Assert.Equal(WindowPhase.None, s.Phase);
        Assert.True(s.IsSupersededBy(order: 6));    // una apertura numerada antes de esa pausa seguiría respetándola
        Assert.False(s.IsSupersededBy(order: 8));
    }

    [Fact]
    public void Resuming_when_not_paused_reports_already_running_unless_a_later_pause_exists()
    {
        var s = WithReadyWindow();
        Assert.Equal(OperatorResumeResult.AlreadyRunning, s.TryOperatorResume(order: 5));
    }

    [Fact]
    public void The_strongest_operator_reason_is_remembered_for_a_window_born_later()
    {
        var s = new OutputPauseState();
        s.RequestPause(Emergency, order: 7);
        s.RequestPause(Manual, order: 8);   // un Manual posterior no rebaja el Emergency

        Assert.True(s.BeginWindow(order: 3));
        Assert.Equal(Emergency, s.Reason);
    }
}
