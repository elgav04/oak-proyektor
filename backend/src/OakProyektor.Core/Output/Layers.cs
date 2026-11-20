namespace OakProyektor.Core.Output;

/// <summary>Tipo de fondo (capa 1). Camel case en el protocolo: none, color, image, video.</summary>
public enum BackgroundKind
{
    None,
    Color,
    Image,
    Video,
}

public enum TextAlign
{
    Left,
    Center,
    Right,
}

public enum VerticalAlign
{
    Top,
    Middle,
    Bottom,
}

/// <summary>
/// Capa 1, Fondo. <see cref="Color"/> es SIEMPRE un color válido: es el fondo de <c>color</c> y, para imagen y video, el color sólido al que cae la salida
/// si el medio no se puede reproducir (nunca negro por sorpresa) y que se ve mientras el medio carga. <see cref="MediaUrl"/> es una URL <c>file:///</c>
/// ya VALIDADA por el backend (<see cref="MediaPathValidator"/>); la salida no valida ni decide nada. El video va en bucle (siempre) y silenciado por defecto.
/// </summary>
/// <param name="FileName">Nombre del archivo, solo para mostrarlo en el panel.</param>
/// <param name="TransitionMs">Duración del fundido al cambiar a ESTE fondo.</param>
/// <param name="Warning">Aviso para el panel sobre el medio (<c>removableDrive</c>: está en una unidad extraíble y puede desaparecer); la salida lo ignora.</param>
public sealed record BackgroundLayer(BackgroundKind Kind, string Color, string? MediaUrl, string? FileName, bool Muted, int TransitionMs, string? Warning)
{
    public const string DefaultColor = "#000000";
    public const string RemovableDriveWarning = "removableDrive";
    public const int DefaultTransitionMs = 600;

    public static BackgroundLayer Empty { get; } = new(BackgroundKind.None, DefaultColor, null, null, true, 0, null);

    public static BackgroundLayer OfColor(string color, int transitionMs = DefaultTransitionMs) => new(BackgroundKind.Color, color, null, null, true, transitionMs, null);
}

/// <summary>Un bloque de texto del slide (un párrafo o estrofa). Puede contener saltos de línea.</summary>
public sealed record TextBlock(string Text);

/// <summary>
/// Tema del texto. Todas las medidas están en píxeles del LIENZO VIRTUAL de 1920×1080 (<see cref="OutputCanvas"/>), no de la pantalla real.
/// Con <see cref="AutoFit"/> el texto se reduce (nunca por debajo de <see cref="MinFontSize"/>) hasta caber en el área definida por los márgenes.
/// </summary>
public sealed record TextTheme(
    string FontFamily, int FontSize, string Color,
    int OutlineWidth, string OutlineColor, int ShadowBlur, int ShadowOffset, string ShadowColor,
    TextAlign Align, VerticalAlign VerticalAlign, double LineHeight,
    int MarginTop, int MarginRight, int MarginBottom, int MarginLeft,
    bool AutoFit, int MinFontSize)
{
    public static TextTheme Default { get; } = new(
        "Arial, sans-serif", 72, "#FFFFFF",
        0, "#000000", 8, 3, "#000000CC",
        TextAlign.Center, VerticalAlign.Middle, 1.25,
        80, 120, 80, 120,
        true, 28);
}

/// <summary>Capa 2, Contenido: un slide. <see cref="Id"/> identifica el slide (la salida hace el fundido solo cuando cambia).</summary>
public sealed record Slide(string Id, IReadOnlyList<TextBlock> Blocks, TextTheme Theme);

/// <param name="Slide">El slide actual; null = sin contenido (solo fondo).</param>
/// <param name="TransitionMs">Duración del crossfade al llegar el slide siguiente (o al quitarlo).</param>
public sealed record ContentLayer(Slide? Slide, int TransitionMs)
{
    public const int DefaultTransitionMs = 400;
    public static ContentLayer Empty { get; } = new(null, DefaultTransitionMs);
}

/// <summary>Un elemento de la capa 3. Reservado: todavía no hay alertas ni ticker, así que la lista siempre está vacía.</summary>
public sealed record OverlayItem(string Id, string Kind);

/// <summary>Capa 3, Superposiciones: solo la estructura (contenedor de elementos). Sin implementar.</summary>
public sealed record OverlayLayer(IReadOnlyList<OverlayItem> Items)
{
    public static OverlayLayer Empty { get; } = new([]);
}

/// <summary>El lienzo virtual de la salida: el diseño es idéntico en cualquier resolución porque se escala a la ventana.</summary>
public static class OutputCanvas
{
    public const int Width = 1920;
    public const int Height = 1080;
}
