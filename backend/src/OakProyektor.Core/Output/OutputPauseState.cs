namespace OakProyektor.Core.Output;

/// <summary>Qué hay de la ventana de salida: nada, creándose (aún sin handle) o lista.</summary>
public enum WindowPhase { None, Creating, Ready }

/// <param name="Changed">La pausa empezó o su motivo subió de nivel.</param>
/// <param name="Phase">
/// Cuándo se puede minimizar: <see cref="WindowPhase.Ready"/> = ya; <see cref="WindowPhase.Creating"/> = la ventana aún se está creando y
/// <see cref="OutputPauseState.MarkWindowReady"/> avisará de que hay que minimizarla en cuanto exista; <see cref="WindowPhase.None"/> = no hay ventana.
/// </param>
public readonly record struct PauseRequestResult(bool Changed, WindowPhase Phase);

public enum OperatorResumeResult
{
    /// <summary>Estaba en pausa y ahora corre.</summary>
    Resumed,
    /// <summary>No estaba en pausa.</summary>
    AlreadyRunning,
    /// <summary>Una pausa del operador (o el atajo de emergencia) llegó DESPUÉS de esta orden: sigue en pausa.</summary>
    Superseded,
}

/// <summary>
/// Estado de pausa de la salida como máquina de estados pura (sin Photino ni Win32), para poder probar las carreras.
/// Reglas:
/// <list type="bullet">
/// <item>Prioridad de motivos: <c>Emergency</c> &gt; <c>Manual</c> &gt; automáticos (<c>DisplayLost</c>, <c>PanelCrossed</c>). Un motivo
/// nunca se rebaja; entre automáticos se conserva el primero.</item>
/// <item>Solo los motivos automáticos se reanudan solos, y solo si el motivo sigue siendo el MISMO que se evaluó
/// (<see cref="TryAutoResume"/>). Manual y Emergency solo los levanta el operador (<see cref="TryOperatorResume"/>).</item>
/// <item>Las órdenes del operador llevan un número de llegada creciente. Una orden de abrir/reanudar numerada ANTES de la última pausa
/// del operador no la deshace (<see cref="OperatorResumeResult.Superseded"/>): si el operador pulsa «Mover / reabrir» y enseguida
/// «Recuperar control», el resultado final es pausada aunque la pausa (carril de prioridad) se procese primero.</item>
/// <item>Una pausa pedida mientras la ventana aún se crea queda registrada y <see cref="MarkWindowReady"/> la devuelve.</item>
/// </list>
/// NO es seguro entre hilos: el dueño la protege con su candado, de modo que cada comparación-y-escritura sea atómica.
/// </summary>
public sealed class OutputPauseState
{
    private long _operatorPauseOrder;                    // número de la última pausa del operador/atajo; 0 = ninguna
    private OutputPauseReason? _operatorPauseReason;     // el motivo más fuerte entre las pausas del operador

    public bool IsPaused { get; private set; }
    public OutputPauseReason? Reason { get; private set; }
    public WindowPhase Phase { get; private set; } = WindowPhase.None;

    /// <summary>Motivos que se reanudan solos: <c>DisplayLost</c>, <c>PanelCrossed</c> y <c>DuplicateMode</c>. <c>Manual</c>, <c>Emergency</c> y <c>Unstable</c> esperan al operador.</summary>
    public static bool IsAutomatic(OutputPauseReason reason) =>
        reason is OutputPauseReason.DisplayLost or OutputPauseReason.PanelCrossed or OutputPauseReason.DuplicateMode;

    private static int Rank(OutputPauseReason reason) => reason switch
    {
        OutputPauseReason.Emergency => 3,
        OutputPauseReason.Manual or OutputPauseReason.Unstable => 2,
        _ => 1,
    };

    /// <summary>
    /// Pide una pausa. <paramref name="order"/> es el número de llegada de la orden (obligatorio para Manual/Emergency; se ignora en los
    /// automáticos). Se registra aunque no haya ventana: una apertura ya encolada debe respetarla.
    /// </summary>
    public PauseRequestResult RequestPause(OutputPauseReason reason, long order = 0)
    {
        // Unstable la pide el propio sistema (tope de oscilaciones), no el operador: no cuenta como «pausa del operador» para las carreras de órdenes.
        if (!IsAutomatic(reason) && reason != OutputPauseReason.Unstable)
        {
            _operatorPauseOrder = Math.Max(_operatorPauseOrder, order);
            if (_operatorPauseReason is not { } known || Rank(reason) > Rank(known)) _operatorPauseReason = reason;
        }

        bool changed;
        if (!IsPaused)
        {
            IsPaused = true;
            Reason = reason;
            changed = true;
        }
        else if (Rank(reason) > Rank(Reason!.Value) || (reason == OutputPauseReason.DuplicateMode && IsAutomatic(Reason.Value) && Reason != reason))
        {
            Reason = reason;   // subir de nivel (p. ej. DisplayLost → Emergency); nunca bajar. Entre automáticos solo «Duplicar» sustituye (es la causa real)
            changed = true;
        }
        else changed = false;

        return new PauseRequestResult(changed, Phase);
    }

    /// <summary>
    /// Reanudación automática: solo si SIGUE en pausa por el mismo motivo automático que se evaluó. Si mientras tanto llegó una pausa
    /// del operador (el motivo subió) o ya no está en pausa, no hace nada.
    /// </summary>
    public bool TryAutoResume(OutputPauseReason evaluated)
    {
        if (!IsPaused || !IsAutomatic(evaluated) || Reason != evaluated) return false;
        IsPaused = false;
        Reason = null;
        return true;
    }

    /// <summary>¿Hay una pausa del operador posterior a esta orden? (lectura, sin escribir).</summary>
    public bool IsSupersededBy(long order) => _operatorPauseOrder > order;

    /// <summary>Reanudación pedida por el operador (<c>output.open</c> numerado con <paramref name="order"/>).</summary>
    public OperatorResumeResult TryOperatorResume(long order)
    {
        if (IsSupersededBy(order)) return OperatorResumeResult.Superseded;
        if (!IsPaused) return OperatorResumeResult.AlreadyRunning;
        IsPaused = false;
        Reason = null;
        return OperatorResumeResult.Resumed;
    }

    /// <summary>
    /// Empieza a crearse una ventana nueva por una orden numerada <paramref name="order"/>. Nace corriendo, salvo que el operador
    /// haya pedido una pausa DESPUÉS de esa orden: entonces nace ya en pausa. Devuelve si nace en pausa.
    /// </summary>
    public bool BeginWindow(long order)
    {
        Phase = WindowPhase.Creating;
        IsPaused = false;
        Reason = null;
        if (IsSupersededBy(order) && _operatorPauseReason is { } reason)
        {
            IsPaused = true;
            Reason = reason;
        }
        return IsPaused;
    }

    /// <summary>La ventana ya existe (tiene handle). Devuelve true si hay que minimizarla ahora porque se pidió una pausa mientras se creaba.</summary>
    public bool MarkWindowReady()
    {
        Phase = WindowPhase.Ready;
        return IsPaused;
    }

    /// <summary>La ventana se cerró (o falló al crearse). Conserva el número de la última pausa del operador.</summary>
    public void WindowClosed()
    {
        Phase = WindowPhase.None;
        IsPaused = false;
        Reason = null;
    }
}
