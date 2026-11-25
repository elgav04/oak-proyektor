using System.Text.RegularExpressions;

namespace OakProyektor.Core.Output;

/// <summary>
/// Validación de lo que el operador manda a la salida (slide, tema, colores, duraciones). Función pura: devuelve el motivo en español
/// o null si es válido. El backend es quien valida; la salida solo pinta.
/// </summary>
public static partial class ContentValidator
{
    public const int MaxBlocks = 30;
    public const int MaxBlockChars = 4000;
    public const int MaxTotalChars = 20000;
    public const int MaxTransitionMs = 5000;

    [GeneratedRegex(@"^#[0-9a-fA-F]{6}([0-9a-fA-F]{2})?$")]
    private static partial Regex ColorRegex();

    [GeneratedRegex(@"^[\p{L}\p{N} ,\-_'.]{1,100}$")]
    private static partial Regex FontFamilyRegex();

    public static bool IsColor(string? value) => value is not null && ColorRegex().IsMatch(value);

    public static string? ValidateColor(string? value, string what) =>
        IsColor(value) ? null : $"{what}: el color debe tener la forma #RRGGBB o #RRGGBBAA.";

    public static string? ValidateTransition(int ms, string what) =>
        ms is >= 0 and <= MaxTransitionMs ? null : $"{what}: la duración debe estar entre 0 y {MaxTransitionMs} ms.";

    public static string? ValidateTheme(TextTheme t)
    {
        if (t.FontFamily is null || !FontFamilyRegex().IsMatch(t.FontFamily)) return "Tema: la familia de fuente solo admite letras, números, espacios, comas, guiones y apóstrofos (máx. 100).";
        if (t.FontSize is < 8 or > 400) return "Tema: el tamaño de fuente debe estar entre 8 y 400 (píxeles del lienzo de 1920×1080).";
        if (t.MinFontSize < 8 || t.MinFontSize > t.FontSize) return "Tema: el tamaño mínimo debe estar entre 8 y el tamaño de fuente.";
        if (ValidateColor(t.Color, "Tema (texto)") is { } c1) return c1;
        if (ValidateColor(t.OutlineColor, "Tema (contorno)") is { } c2) return c2;
        if (ValidateColor(t.ShadowColor, "Tema (sombra)") is { } c3) return c3;
        if (t.OutlineWidth is < 0 or > 40) return "Tema: el grosor del contorno debe estar entre 0 y 40.";
        if (t.ShadowBlur is < 0 or > 60) return "Tema: el desenfoque de la sombra debe estar entre 0 y 60.";
        if (t.ShadowOffset is < 0 or > 40) return "Tema: el desplazamiento de la sombra debe estar entre 0 y 40.";
        if (!Enum.IsDefined(t.Align) || !Enum.IsDefined(t.VerticalAlign)) return "Tema: alineación desconocida.";
        if (double.IsNaN(t.LineHeight) || t.LineHeight is < 0.8 or > 3) return "Tema: el interlineado debe estar entre 0,8 y 3.";
        foreach (var m in new[] { t.MarginTop, t.MarginRight, t.MarginBottom, t.MarginLeft })
            if (m is < 0 or > 900) return "Tema: cada margen debe estar entre 0 y 900.";
        if (t.MarginLeft + t.MarginRight >= OutputCanvas.Width - 200 || t.MarginTop + t.MarginBottom >= OutputCanvas.Height - 200)
            return "Tema: los márgenes dejan menos de 200 píxeles para el texto.";
        return null;
    }

    /// <summary>El origen es opcional (nulo = no viene de una lista). Si viene, solo ids y números válidos y un tipo conocido; no se comprueba contra la BD.</summary>
    public static string? ValidateOrigin(SlideOrigin? origin)
    {
        if (origin is null) return null;
        if (origin.PlaylistId < 1 || origin.ItemId < 1) return "El origen del slide tiene un identificador de lista o de elemento no válido.";
        if (origin.ItemType is not (SlideOrigin.Song or SlideOrigin.Passage)) return "El origen del slide tiene un tipo de elemento desconocido (debe ser «song» o «passage»).";
        if (origin.Position is < 0 or > SlideOrigin.MaxPosition) return $"El origen del slide tiene una posición fuera de rango (0 a {SlideOrigin.MaxPosition}).";
        return null;
    }

    public static string? ValidateSlide(IReadOnlyList<string>? blocks, TextTheme? theme, int transitionMs, SlideOrigin? origin)
        => ValidateOrigin(origin) ?? ValidateSlide(blocks, theme, transitionMs);

    public static string? ValidateSlide(IReadOnlyList<string>? blocks, TextTheme? theme, int transitionMs)
    {
        if (blocks is null || blocks.Count == 0) return "El slide no tiene bloques de texto (para quitar el contenido usa «Quitar contenido»).";
        if (blocks.Count > MaxBlocks) return $"El slide tiene demasiados bloques (máximo {MaxBlocks}).";
        var total = 0;
        foreach (var b in blocks)
        {
            if (b is null) return "El slide tiene un bloque vacío (nulo).";
            if (b.Length > MaxBlockChars) return $"Un bloque supera los {MaxBlockChars} caracteres.";
            total += b.Length;
        }
        if (total > MaxTotalChars) return $"El slide supera los {MaxTotalChars} caracteres en total.";
        if (blocks.All(string.IsNullOrWhiteSpace)) return "El slide no tiene texto.";
        if (theme is null) return "Falta el tema del texto.";
        return ValidateTheme(theme) ?? ValidateTransition(transitionMs, "Contenido");
    }
}
