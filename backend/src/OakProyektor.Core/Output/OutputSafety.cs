using OakProyektor.Core.Display;

namespace OakProyektor.Core.Output;

/// <summary>Rectángulo en píxeles físicos del escritorio virtual.</summary>
public readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;

    public bool Intersects(ScreenRect other) => X < other.Right && other.X < Right && Y < other.Bottom && other.Y < Bottom;

    public bool IsInside(ScreenRect other) => X >= other.X && Y >= other.Y && Right <= other.Right && Bottom <= other.Bottom;

    public static ScreenRect Of(DisplayInfo display) => new(display.X, display.Y, display.Width, display.Height);
}

/// <summary>Por qué la salida está en pausa. Se serializa en camelCase: displayLost, panelCrossed, manual, emergency, duplicateMode, unstable.</summary>
public enum OutputPauseReason
{
    /// <summary>El monitor de la salida desapareció (proyector desconectado).</summary>
    DisplayLost,
    /// <summary>La salida se cruza con el monitor del panel, o el panel está en el monitor de la salida.</summary>
    PanelCrossed,
    /// <summary>El operador pulsó «Recuperar control».</summary>
    Manual,
    /// <summary>Atajo de emergencia global.</summary>
    Emergency,
    /// <summary>Windows está en modo «Duplicar» (Win+P): hay un solo monitor lógico y la salida taparía al panel. Se reanuda sola al volver a «Extender».</summary>
    DuplicateMode,
    /// <summary>La salida cambiaba de pausa a reanudación demasiadas veces seguidas (posible fallo de pantalla): se queda en pausa hasta que el operador la reanude.</summary>
    Unstable,
}

/// <summary>Qué hacer cuando el monitor de la salida desaparece. Se serializa en camelCase: warnAndWait, moveToBest.</summary>
public enum DisplayLostPolicy
{
    /// <summary>Pausar, avisar y ofrecer un botón por cada monitor disponible (por defecto).</summary>
    WarnAndWait,
    /// <summary>Mover sola al mejor monitor que no sea el del panel (<see cref="BestDisplayPicker"/>); si no hay ninguno, pausar y avisar.</summary>
    MoveToBest,
}

public enum SafetyActionKind { None, Pause, Resume, SetTopmost, Refit, Move }

/// <param name="Immediate">Para una pausa o movimiento por monitor perdido: true = no esperar el antirrebote (la salida ya cruza el monitor del panel).</param>
/// <param name="Area">Para <see cref="SafetyActionKind.Refit"/> y <see cref="SafetyActionKind.Move"/>: el rectángulo del monitor al que hay que ajustar la ventana.</param>
/// <param name="DisplayId">Para <see cref="SafetyActionKind.Move"/>: el monitor al que pasa la salida (reanuda si estaba en pausa).</param>
public sealed record OutputSafetyDecision(
    SafetyActionKind Kind, OutputPauseReason? Reason = null, bool Topmost = false, bool Immediate = false, ScreenRect? Area = null, string? DisplayId = null)
{
    public static OutputSafetyDecision None { get; } = new(SafetyActionKind.None);
}

/// <param name="OutputRect">Posición real actual de la ventana de salida (ignorada mientras está en pausa/minimizada).</param>
/// <param name="TargetId">ID (<see cref="DisplayId"/>) del monitor al que está asignada la salida ahora.</param>
/// <param name="AppliedArea">El rectángulo del monitor al que se ajustó la ventana por última vez (al abrir, reanudar o reajustar). Si el monitor ya no
/// tiene ese rectángulo (cambió la resolución o la posición del MISMO monitor), hay que reajustar; null = se considera igual al actual.</param>
/// <param name="PreferredId">Monitor preferido del operador (al que la salida siempre intenta volver); null si no hay.</param>
/// <param name="Recent">Monitores usados por la salida, el más reciente primero (para <see cref="BestDisplayPicker"/>).</param>
/// <param name="DuplicateMode">Windows está en modo «Duplicar»: un solo monitor lógico.</param>
public sealed record OutputSafetyInput(
    ScreenRect OutputRect, bool IsTopmost, IReadOnlyList<DisplayInfo> Displays, string TargetId,
    bool IsPaused, OutputPauseReason? PauseReason, bool AutoRestore, ScreenRect? AppliedArea = null,
    string? PreferredId = null, DisplayLostPolicy OnDisplayLost = DisplayLostPolicy.WarnAndWait, IReadOnlyList<string>? Recent = null, bool DuplicateMode = false);

/// <summary>
/// Antirrebote del reajuste (función pura con estado mínimo): un cambio de resolución pasa por rectángulos intermedios; solo se reajusta cuando el
/// MISMO rectángulo nuevo se observa en <paramref name="required"/> sondeos seguidos. Cualquier otro valor reinicia la cuenta.
/// </summary>
public sealed class RefitDebouncer(int required = 2)
{
    private ScreenRect? _candidate;
    private int _count;

    /// <param name="pending">El rectángulo al que habría que reajustar, o null si no hay nada pendiente.</param>
    /// <returns>true cuando ya se puede reajustar.</returns>
    public bool Observe(ScreenRect? pending)
    {
        if (pending is null) { Reset(); return false; }
        if (_candidate != pending) { _candidate = pending; _count = 0; }
        return ++_count >= required;
    }

    public void Reset() { _candidate = null; _count = 0; }
}

/// <summary>
/// Política de seguridad de la salida a pantalla completa (función pura): QUÉ haría falta hacer ahora; el antirrebote y el tope de oscilaciones los pone
/// <see cref="OutputSafetyEngine"/>. Reglas:
/// topmost solo mientras esté TOTALMENTE dentro de un monitor que no es el del panel; si el monitor desaparece o la salida se cruza con el monitor
/// del panel, se pausa (sin cerrar); si el mismo monitor reaparece (mismo ID estable, aunque con otra resolución) o el panel sale de él, se reanuda (si
/// está permitido). La salida NUNCA elige el monitor del panel ni salta a otro cuando el panel se pone encima del suyo: solo cambia de monitor si el
/// suyo DESAPARECE (según <see cref="DisplayLostPolicy"/>) o para volver al preferido.
/// </summary>
public static class OutputSafetyPolicy
{
    public static OutputSafetyDecision Evaluate(OutputSafetyInput input)
    {
        // Una enumeración vacía es un fallo transitorio del sistema, no "se desconectaron todos los monitores".
        if (input.Displays.Count == 0) return OutputSafetyDecision.None;

        var target = input.Displays.FirstOrDefault(d => d.Id == input.TargetId);
        var panel = input.Displays.FirstOrDefault(d => d.HostsPanel);

        // Modo «Duplicar»: un solo monitor lógico, el de la salida y el del panel coinciden. Pausa inmediata con su propio motivo (también si ya estaba en
        // pausa por otro motivo automático, para que el aviso diga la causa real). Al volver a «Extender» sigue la reanudación normal de abajo.
        if (input.DuplicateMode)
        {
            if (!input.IsPaused) return new(SafetyActionKind.Pause, OutputPauseReason.DuplicateMode, Immediate: true);
            return input.PauseReason is { } r && OutputPauseState.IsAutomatic(r) && r != OutputPauseReason.DuplicateMode
                ? new(SafetyActionKind.Pause, OutputPauseReason.DuplicateMode)
                : OutputSafetyDecision.None;
        }

        if (!input.IsPaused)
        {
            var crossesPanel = panel is not null && input.OutputRect.Intersects(ScreenRect.Of(panel));

            // Monitor desaparecido: normalmente se espera el antirrebote (un parpadeo de enumeración no debe dejar la proyección en negro),
            // pero si la ventana YA cruza el monitor del panel (Windows la movió allí) tapa al operador: acción inmediata.
            if (target is null)
            {
                return AlternativeFor(input, targetIsGone: true) is { } alt
                    ? new(SafetyActionKind.Move, DisplayId: alt.Id, Area: ScreenRect.Of(alt), Immediate: crossesPanel)
                    : new(SafetyActionKind.Pause, OutputPauseReason.DisplayLost, Immediate: crossesPanel);
            }
            if (target.HostsPanel) return new(SafetyActionKind.Pause, OutputPauseReason.PanelCrossed);

            // El MISMO monitor cambió de resolución/posición (su ID no depende del rectángulo): se reajusta a pantalla completa, sin pausa ni avisos.
            // Solo se pausa si el rectángulo NUEVO cruza el monitor del panel. Se evalúa ANTES que el rectángulo actual de la ventana, que durante
            // el cambio es el viejo (y no es culpa del operador): la política de «la ventana se cruza con el panel» es para cuando alguien la ARRASTRA.
            var desired = ScreenRect.Of(target);
            if (input.AppliedArea is { } applied && applied != desired)
            {
                if (panel is not null && desired.Intersects(ScreenRect.Of(panel))) return new(SafetyActionKind.Pause, OutputPauseReason.PanelCrossed);
                return new(SafetyActionKind.Refit, Area: desired);
            }

            if (crossesPanel) return new(SafetyActionKind.Pause, OutputPauseReason.PanelCrossed);

            // Corriendo en un monitor de respaldo y el preferido del operador está de vuelta: la salida siempre intenta volver a él.
            if (PreferredAvailable(input) is { } preferred && preferred.Id != target.Id)
                return new(SafetyActionKind.Move, DisplayId: preferred.Id, Area: ScreenRect.Of(preferred));

            var inside = input.OutputRect.IsInside(desired);
            return inside == input.IsTopmost ? OutputSafetyDecision.None : new(SafetyActionKind.SetTopmost, Topmost: inside);
        }

        // Solo las pausas automáticas se reanudan solas; las manuales, de emergencia e inestables esperan al operador.
        if (!input.AutoRestore || input.PauseReason is not { } reason || !OutputPauseState.IsAutomatic(reason)) return OutputSafetyDecision.None;

        if (target is not null && !target.HostsPanel)
        {
            // Mismo monitor (mismo ID estable, la resolución no importa) y libre del panel: se reanuda en SU rectángulo actual. Si había un preferido
            // distinto que ha vuelto, se prefiere ese.
            return PreferredAvailable(input) is { } preferred && preferred.Id != target.Id
                ? new(SafetyActionKind.Move, DisplayId: preferred.Id, Area: ScreenRect.Of(preferred))
                : new(SafetyActionKind.Resume);
        }

        // El monitor de la salida no está (o aloja el panel): ¿hay otro al que ir? Con el panel encima del monitor de la salida NO se salta a un
        // monitor cualquiera (solo se vuelve al preferido); si el monitor DESAPARECIÓ, según la política.
        return AlternativeFor(input, targetIsGone: target is null) is { } other
            ? new(SafetyActionKind.Move, DisplayId: other.Id, Area: ScreenRect.Of(other))
            : OutputSafetyDecision.None;
    }

    /// <summary>El monitor preferido, si está conectado, no es el del panel y es distinto del actual.</summary>
    private static DisplayInfo? PreferredAvailable(OutputSafetyInput input) =>
        input.PreferredId is { } id ? input.Displays.FirstOrDefault(d => d.Id == id && !d.HostsPanel) : null;

    /// <summary>
    /// A qué monitor pasar cuando el actual no sirve: el preferido si está de vuelta (siempre, con cualquier política); si el actual DESAPARECIÓ y la
    /// política es <see cref="DisplayLostPolicy.MoveToBest"/>, el mejor (<see cref="BestDisplayPicker"/>). Nunca el monitor del panel.
    /// </summary>
    private static DisplayInfo? AlternativeFor(OutputSafetyInput input, bool targetIsGone)
    {
        if (PreferredAvailable(input) is { } preferred && preferred.Id != input.TargetId) return preferred;
        if (targetIsGone && input.OnDisplayLost == DisplayLostPolicy.MoveToBest)
            return BestDisplayPicker.Choose(input.Displays, input.Recent ?? [], excludeId: input.TargetId);
        return null;
    }
}
