namespace OakProyektor.Core.Display;

public enum DisplayResolutionKind
{
    /// <summary>Se pidió un ID y ese monitor existe: es el elegido.</summary>
    Chosen,
    /// <summary>No se pidió ninguno (<c>null</c>): se eligió el primer monitor que no aloja el panel, o ninguno si no hay (entonces la salida va en ventana normal).</summary>
    Automatic,
    /// <summary>Se pidió un ID explícito que NO existe. Nunca se sustituye por otro monitor: quien llama debe fallar (<c>output.displayNotFound</c>).</summary>
    NotFound,
}

/// <param name="Display">El monitor resuelto. Nulo si es <see cref="DisplayResolutionKind.NotFound"/>, o si es <see cref="DisplayResolutionKind.Automatic"/> y no hay candidato.</param>
public readonly record struct DisplayResolution(DisplayResolutionKind Kind, DisplayInfo? Display);

/// <summary>
/// Elige el monitor de la salida (función pura). Regla de oro (hallazgo I-B): <b>solo <c>null</c> significa «automático»</b>. Un ID explícito
/// que no está conectado es un error, NO se cae al automático: el operador pulsó «Reanudar salida» sobre un monitor concreto y la salida
/// nunca debe aparecer a pantalla completa en otro que nadie eligió.
/// </summary>
public static class DisplayResolver
{
    public static DisplayResolution Resolve(IReadOnlyList<DisplayInfo> displays, string? requestedId)
    {
        if (requestedId is null)
            return new DisplayResolution(DisplayResolutionKind.Automatic, displays.FirstOrDefault(d => !d.HostsPanel));

        return displays.FirstOrDefault(d => d.Id == requestedId) is { } found
            ? new DisplayResolution(DisplayResolutionKind.Chosen, found)
            : new DisplayResolution(DisplayResolutionKind.NotFound, null);
    }
}
