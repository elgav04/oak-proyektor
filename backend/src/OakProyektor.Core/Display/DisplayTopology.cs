namespace OakProyektor.Core.Display;

/// <summary>
/// Detección del modo «Duplicar» de Windows (Win+P), función pura sobre lo que lee Win32. En «Duplicar» varias salidas físicas muestran la MISMA fuente
/// de escritorio: Windows expone un solo monitor lógico (el del panel), así que la salida no tiene dónde proyectar sin tapar al operador.
/// </summary>
public static class DisplayTopology
{
    /// <param name="activeSourceKeys">Un identificador por cada camino de pantalla ACTIVO (adaptador + id de fuente). Dos caminos con la misma fuente = duplicado.</param>
    public static bool IsDuplicate(IReadOnlyCollection<string> activeSourceKeys) => activeSourceKeys.Count != activeSourceKeys.Distinct().Count();
}
