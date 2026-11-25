using OakProyektor.Core.Output;

namespace OakProyektor.Core.Songs;

/// <summary>Qué pasa al pulsar «siguiente» en la última diapositiva de una canción.</summary>
public enum SongAtEnd
{
    /// <summary>Se queda en la última diapositiva (una pulsación de más no vacía la pantalla en medio de un culto). Por defecto.</summary>
    Stay,
    /// <summary>Quita lo proyectado.</summary>
    Clear,
}

/// <summary>
/// Cómo se proyectan las canciones (protocolo v12). Se guarda en <c>settings.json</c>, NO en la BD de contenido. Autor, derechos y CCLI se guardan en la ficha de la canción
/// pero NO se proyectan (<c>OutputState</c> no tiene dónde mostrarlos).
/// </summary>
/// <param name="Theme">Tema propio de las canciones; nulo = <see cref="DefaultTheme"/>.</param>
/// <param name="TransitionMs">Duración del crossfade entre diapositivas.</param>
public sealed record SongDisplaySettings(
    TextTheme? Theme = null,
    int TransitionMs = SongDisplaySettings.DefaultTransitionMs,
    SongAtEnd AtEnd = SongAtEnd.Stay)
{
    public const int DefaultTransitionMs = 400;

    /// <summary>Tema de partida: centrado, 60 px, contorno y sombra suaves; con ajuste automático como RED DE SEGURIDAD (la paginación del panel mide con este mismo tema).</summary>
    public static TextTheme DefaultTheme { get; } = new(
        "Arial, sans-serif", 60, "#FFFFFF",
        2, "#000000", 8, 3, "#000000CC",
        TextAlign.Center, VerticalAlign.Middle, 1.25,
        70, 120, 70, 120,
        true, 36);

    public TextTheme EffectiveTheme => Theme ?? DefaultTheme;

    /// <summary>Motivo en español si la configuración no es válida; null si lo es. Función pura.</summary>
    public static string? Validate(SongDisplaySettings s)
    {
        if (ContentValidator.ValidateTransition(s.TransitionMs, "Canciones") is { } t) return t;
        if (ContentValidator.ValidateTheme(s.EffectiveTheme) is { } theme) return theme;
        if (!Enum.IsDefined(s.AtEnd)) return "Canciones: «al final de la canción» tiene un valor desconocido.";
        return null;
    }
}
