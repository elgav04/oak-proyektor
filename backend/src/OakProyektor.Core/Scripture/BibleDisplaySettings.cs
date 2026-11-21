using System.Text.RegularExpressions;
using OakProyektor.Core.Output;

namespace OakProyektor.Core.Scripture;

/// <summary>Nombre corto con que se proyecta una Biblia («RVR1960» → «RVR60»). Sin entrada, se usa la abreviatura.</summary>
public sealed record BibleShortName(string Bible, string ShortName);

/// <summary>
/// Cómo se proyecta la Biblia (protocolo v8). Se guarda en <c>settings.json</c>, NO en la BD de contenido. La Biblia por defecto sigue en
/// <see cref="AppSettings.DefaultBible"/> (la lee el servicio de consultas). El aviso de derechos NO está aquí: vive en <c>Bibles.Attribution</c>
/// y vacío por defecto (el código no trae ningún texto de aviso).
/// </summary>
/// <param name="ProjectOnNavigate">Proyectar el versículo al moverse con las flechas (sin pulsar Enter).</param>
/// <param name="SuperscriptNumbers">Mostrar el número de cada versículo en superíndice (dígitos Unicode).</param>
/// <param name="ShowAttribution">Mostrar el aviso de derechos de la Biblia (si está vacío no se muestra nada).</param>
/// <param name="TransitionMs">Duración del crossfade al cambiar de versículo (corto: se navega rápido en vivo).</param>
/// <param name="Theme">Tema propio de la Biblia; nulo = <see cref="DefaultTheme"/>.</param>
/// <param name="ShortNames">Nombres cortos por Biblia (abreviatura → nombre).</param>
public sealed partial record BibleDisplaySettings(
    bool ProjectOnNavigate = false,
    bool SuperscriptNumbers = true,
    bool ShowAttribution = false,
    int TransitionMs = BibleDisplaySettings.DefaultTransitionMs,
    TextTheme? Theme = null,
    IReadOnlyList<BibleShortName>? ShortNames = null)
{
    /// <summary>Crossfade corto por defecto (≈ 250–300 ms): al navegar rápido no debe sentirse lento.</summary>
    public const int DefaultTransitionMs = 280;
    public const int MaxShortNameLength = 40;
    public const int MaxShortNames = 100;

    /// <summary>
    /// Tema bíblico de partida: centrado, 64 px, contorno y sombra suaves. Con ajuste automático activado como RED DE SEGURIDAD: la paginación del panel
    /// mide con este mismo tema y hace que el texto quepa al tamaño pedido; si la medición difiriera, la salida reduciría la letra en vez de recortar.
    /// </summary>
    public static TextTheme DefaultTheme { get; } = new(
        "Arial, sans-serif", 64, "#FFFFFF",
        2, "#000000", 8, 3, "#000000CC",
        TextAlign.Center, VerticalAlign.Middle, 1.3,
        70, 120, 70, 120,
        true, 40);

    public TextTheme EffectiveTheme => Theme ?? DefaultTheme;

    public string ShortNameOf(string abbreviation) =>
        ShortNames?.FirstOrDefault(s => s.Bible == abbreviation)?.ShortName is { Length: > 0 } name ? name : abbreviation;

    [GeneratedRegex(@"^[^\p{C}]{1,40}$")]
    private static partial Regex ShortNameRegex();

    /// <summary>Motivo en español si la configuración no es válida; null si lo es. Función pura.</summary>
    public static string? Validate(BibleDisplaySettings s)
    {
        if (ContentValidator.ValidateTransition(s.TransitionMs, "Biblia") is { } t) return t;
        if (ContentValidator.ValidateTheme(s.EffectiveTheme) is { } theme) return theme;
        var names = s.ShortNames ?? [];
        if (names.Count > MaxShortNames) return $"Biblia: demasiados nombres cortos (máximo {MaxShortNames}).";
        foreach (var n in names)
        {
            if (string.IsNullOrWhiteSpace(n.Bible)) return "Biblia: un nombre corto no indica a qué Biblia pertenece.";
            if (string.IsNullOrWhiteSpace(n.ShortName) || !ShortNameRegex().IsMatch(n.ShortName)) return $"Biblia: el nombre corto de «{n.Bible}» debe tener entre 1 y {MaxShortNameLength} caracteres, sin caracteres de control.";
        }
        if (names.Select(n => n.Bible).Distinct().Count() != names.Count) return "Biblia: hay dos nombres cortos para la misma Biblia.";
        return null;
    }
}
