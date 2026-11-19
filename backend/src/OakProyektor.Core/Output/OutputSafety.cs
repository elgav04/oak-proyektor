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

/// <summary>Por qué la salida está en pausa. Se serializa en camelCase: displayLost, panelCrossed, manual, emergency.</summary>
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
}

public enum SafetyActionKind { None, Pause, Resume, SetTopmost, Refit }

/// <param name="Immediate">Para una pausa <c>DisplayLost</c>: true = no esperar el antirrebote (la salida ya cruza el monitor del panel).</param>
/// <param name="Area">Para <see cref="SafetyActionKind.Refit"/>: el rectángulo nuevo del monitor, al que hay que reajustar la ventana.</param>
public sealed record OutputSafetyDecision(SafetyActionKind Kind, OutputPauseReason? Reason = null, bool Topmost = false, bool Immediate = false, ScreenRect? Area = null)
{
    public static OutputSafetyDecision None { get; } = new(SafetyActionKind.None);
}

/// <param name="OutputRect">Posición real actual de la ventana de salida (ignorada mientras está en pausa/minimizada).</param>
/// <param name="TargetId">ID (<see cref="DisplayId"/>) del monitor al que se asignó la salida.</param>
/// <param name="AppliedArea">El rectángulo del monitor al que se ajustó la ventana por última vez (al abrir, reanudar o reajustar). Si el monitor ya no
/// tiene ese rectángulo (cambió la resolución o la posición del MISMO monitor), hay que reajustar; null = se considera igual al actual.</param>
public sealed record OutputSafetyInput(
    ScreenRect OutputRect, bool IsTopmost, IReadOnlyList<DisplayInfo> Displays, string TargetId,
    bool IsPaused, OutputPauseReason? PauseReason, bool AutoRestore, ScreenRect? AppliedArea = null);

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
/// Política de seguridad de la salida a pantalla completa (función pura):
/// topmost solo mientras esté TOTALMENTE dentro de un monitor que no es el del panel; si el monitor desaparece o la salida
/// se cruza con el monitor del panel, se pausa (sin cerrar); si el mismo monitor reaparece, se reanuda (si está permitido). El monitor se identifica por su ID, no por su posición en la lista.
/// </summary>
public static class OutputSafetyPolicy
{
    public static OutputSafetyDecision Evaluate(OutputSafetyInput input)
    {
        // Una enumeración vacía es un fallo transitorio del sistema, no "se desconectaron todos los monitores".
        if (input.Displays.Count == 0) return OutputSafetyDecision.None;

        var target = input.Displays.FirstOrDefault(d => d.Id == input.TargetId);
        var panel = input.Displays.FirstOrDefault(d => d.HostsPanel);

        if (!input.IsPaused)
        {
            var crossesPanel = panel is not null && input.OutputRect.Intersects(ScreenRect.Of(panel));

            // Monitor desaparecido: normalmente se espera el antirrebote (un parpadeo de enumeración no debe dejar la proyección en negro),
            // pero si la ventana YA cruza el monitor del panel (Windows la movió allí) tapa al operador: pausa inmediata.
            if (target is null) return new(SafetyActionKind.Pause, OutputPauseReason.DisplayLost, Immediate: crossesPanel);
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

            var inside = input.OutputRect.IsInside(ScreenRect.Of(target));
            return inside == input.IsTopmost ? OutputSafetyDecision.None : new(SafetyActionKind.SetTopmost, Topmost: inside);
        }

        // Solo las pausas automáticas se reanudan solas; las manuales/de emergencia esperan al operador.
        if (input.AutoRestore
            && input.PauseReason is OutputPauseReason.DisplayLost or OutputPauseReason.PanelCrossed
            && target is not null && !target.HostsPanel)
            return new(SafetyActionKind.Resume);

        return OutputSafetyDecision.None;
    }
}
