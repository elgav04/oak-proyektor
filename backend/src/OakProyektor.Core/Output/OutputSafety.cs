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

public enum SafetyActionKind { None, Pause, Resume, SetTopmost }

public sealed record OutputSafetyDecision(SafetyActionKind Kind, OutputPauseReason? Reason = null, bool Topmost = false)
{
    public static OutputSafetyDecision None { get; } = new(SafetyActionKind.None);
}

/// <param name="OutputRect">Posición real actual de la ventana de salida (ignorada mientras está en pausa/minimizada).</param>
/// <param name="TargetId">ID (<see cref="DisplayId"/>) del monitor al que se asignó la salida.</param>
public sealed record OutputSafetyInput(
    ScreenRect OutputRect, bool IsTopmost, IReadOnlyList<DisplayInfo> Displays, string TargetId,
    bool IsPaused, OutputPauseReason? PauseReason, bool AutoRestore);

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
            if (target is null) return new(SafetyActionKind.Pause, OutputPauseReason.DisplayLost);
            if (target.HostsPanel) return new(SafetyActionKind.Pause, OutputPauseReason.PanelCrossed);
            if (panel is not null && input.OutputRect.Intersects(ScreenRect.Of(panel)))
                return new(SafetyActionKind.Pause, OutputPauseReason.PanelCrossed);

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
