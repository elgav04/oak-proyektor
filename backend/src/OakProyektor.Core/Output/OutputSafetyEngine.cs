namespace OakProyektor.Core.Output;

/// <summary>
/// Cuenta transiciones (pausa, reanudación, movimiento) automáticas en una ventana de tiempo deslizante. Si hay <paramref name="max"/> o más en
/// <paramref name="window"/>, la salida está oscilando. Función pura con un reloj que se pasa (no usa la hora del sistema).
/// </summary>
public sealed class TransitionLimiter(int max = 6, TimeSpan? window = null)
{
    private readonly TimeSpan _window = window ?? TimeSpan.FromSeconds(10);
    private readonly Queue<DateTimeOffset> _times = new();

    /// <summary>Registra una transición en <paramref name="now"/>. Devuelve true si con ella se alcanza el tope (oscilación).</summary>
    public bool Record(DateTimeOffset now)
    {
        _times.Enqueue(now);
        while (_times.Count > 0 && now - _times.Peek() > _window) _times.Dequeue();
        return _times.Count >= max;
    }

    public void Clear() => _times.Clear();
}

/// <summary>
/// Deja pasar una decisión solo cuando es la MISMA (igualdad de valor) en <paramref name="required"/> sondeos seguidos (≈ 500 ms estables con el sondeo
/// de 500 ms y 2 sondeos). Cualquier otra cosa (otra decisión o ninguna) reinicia la cuenta.
/// </summary>
public sealed class DecisionStabilizer(int required = 2)
{
    private OutputSafetyDecision? _candidate;
    private int _count;

    public bool Observe(OutputSafetyDecision? pending)
    {
        if (pending is null) { Reset(); return false; }
        if (!pending.Equals(_candidate)) { _candidate = pending; _count = 0; }
        return ++_count >= required;
    }

    public void Reset() { _candidate = null; _count = 0; }
}

/// <summary>
/// La política de seguridad COMPLETA de la salida, función pura con estado mínimo y sin Photino ni Win32: la decisión instantánea de
/// <see cref="OutputSafetyPolicy"/> más el antirrebote y el tope de oscilaciones. <c>OutputController</c> solo llama a <see cref="Step"/> en cada
/// sondeo y aplica lo que devuelve; los tests la ejecutan con listas de monitores simuladas.
/// <list type="bullet">
/// <item>Pausa por monitor perdido sin cruzar el panel: espera <c>lostTicks</c> sondeos (un parpadeo de enumeración no debe dejar la proyección en negro).</item>
/// <item>Reajuste: el mismo rectángulo nuevo en 2 sondeos seguidos.</item>
/// <item>Reanudar y mover: la misma decisión en 2 sondeos seguidos (≈ 500 ms estables), salvo un movimiento inmediato por monitor perdido bajo el panel.</item>
/// <item>Pausas por cruce con el panel o por Duplicar: inmediatas (la salida está tapando al operador).</item>
/// <item><b>Anti-oscilación:</b> si se aplican <c>maxTransitions</c> pausas/reanudaciones/movimientos en la ventana de tiempo, devuelve <c>Pause(Unstable)</c>:
/// la salida se queda en pausa con aviso hasta que el operador la reanude (que llama a <see cref="Reset"/>).</item>
/// </list>
/// </summary>
public sealed class OutputSafetyEngine(int lostTicksBeforePause = 2, int stableTicks = 2, int maxTransitions = 6, TimeSpan? transitionWindow = null)
{
    private readonly RefitDebouncer _refit = new(stableTicks);
    private readonly DecisionStabilizer _stable = new(stableTicks);
    private readonly TransitionLimiter _limiter = new(maxTransitions, transitionWindow);
    private int _lostTicks;

    /// <summary>Quita todo el estado de antirrebote y de conteo: ventana nueva, cerrada, o reanudación pedida por el operador.</summary>
    public void Reset()
    {
        _refit.Reset();
        _stable.Reset();
        _limiter.Clear();
        _lostTicks = 0;
    }

    /// <summary>La decisión final de este sondeo. <paramref name="now"/> es la hora del sondeo (se pasa para poder simular el tiempo).</summary>
    public OutputSafetyDecision Step(OutputSafetyInput input, DateTimeOffset now)
    {
        var decision = OutputSafetyPolicy.Evaluate(input);

        var refitReady = _refit.Observe(decision.Kind == SafetyActionKind.Refit ? decision.Area : null);
        if (decision.Kind == SafetyActionKind.Refit && !refitReady) decision = OutputSafetyDecision.None;

        // Antirrebote de «monitor desaparecido» a secas: si la ventana YA cruza el monitor del panel (Immediate) tapa al operador y no se espera.
        if (decision is { Kind: SafetyActionKind.Pause, Reason: OutputPauseReason.DisplayLost, Immediate: false })
        {
            if (++_lostTicks < lostTicksBeforePause) decision = OutputSafetyDecision.None;
        }
        else _lostTicks = 0;

        // Reanudar y mover: estables ~500 ms. Un movimiento inmediato (la ventana ya tapa al panel) no espera.
        var needsStable = decision.Kind == SafetyActionKind.Resume || (decision.Kind == SafetyActionKind.Move && !decision.Immediate);
        if (!_stable.Observe(needsStable ? decision : null) && needsStable) decision = OutputSafetyDecision.None;

        if (decision.Kind is SafetyActionKind.Pause or SafetyActionKind.Resume or SafetyActionKind.Move && _limiter.Record(now))
        {
            _limiter.Clear();   // tras el tope se empieza de cero cuando el operador reanude
            return new(SafetyActionKind.Pause, OutputPauseReason.Unstable);
        }
        return decision;
    }
}
