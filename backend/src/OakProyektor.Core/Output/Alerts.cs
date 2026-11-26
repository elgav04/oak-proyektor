namespace OakProyektor.Core.Output;

/// <summary>Cómo se muestra una alerta (protocolo v15): <c>band</c> = cinta estática (por defecto) | <c>ticker</c> = cinta con desplazamiento (para textos largos).</summary>
public enum AlertMode
{
    Band,
    Ticker,
}

/// <summary>Dónde se ancla la cinta en el lienzo de 1920×1080: <c>bottom</c> (por defecto) | <c>top</c>.</summary>
public enum AlertPosition
{
    Bottom,
    Top,
}

/// <summary>
/// Tema de la alerta, INDEPENDIENTE del tema de la Biblia y de las canciones. Todas las medidas están en píxeles del lienzo virtual de 1920×1080.
/// La cinta ocupa todo el ancho y su altura sale del contenido (hasta <see cref="MaxLines"/> renglones; con <see cref="AutoFit"/> la letra baja hasta
/// <see cref="MinFontSize"/> antes de crecer): nunca una altura fija que recorte el aviso.
/// </summary>
/// <param name="BandColor">Fondo de la cinta, <c>#RRGGBB[AA]</c> (por defecto casi opaco: se lee sobre cualquier video o letra).</param>
/// <param name="PaddingY">Relleno vertical de la cinta.</param>
/// <param name="MaxLines">Renglones que admite la cinta estática antes de reducir la letra (1 a 3).</param>
/// <param name="TickerSpeed">Velocidad del desplazamiento en píxeles del lienzo por segundo (solo modo <c>ticker</c>).</param>
public sealed record AlertTheme(
    string FontFamily, int FontSize, string Color, int OutlineWidth, string OutlineColor, string BandColor,
    TextAlign Align, AlertPosition Position, int PaddingY, int MaxLines, bool AutoFit, int MinFontSize, int TickerSpeed)
{
    public static AlertTheme Default { get; } = new(
        "Arial, sans-serif", 52, "#FFFFFF", 0, "#000000", "#000000D9",
        TextAlign.Center, AlertPosition.Bottom, 22, 2, true, 31, 180);
}

/// <summary>
/// La alerta que hay en la capa 3 (v15). <see cref="Id"/> identifica la alerta: la salida solo anima al aparecer, desaparecer o cambiar de id; volver a mostrar el MISMO texto, modo y tema
/// mientras ya está visible conserva el id (no parpadea ni reinicia el desplazamiento). El tema es una COPIA tomada al mostrarla (como el <see cref="Slide"/>): cambiar los ajustes no
/// altera la que está en pantalla. El texto es lo que se proyecta; nunca va a los registros.
/// </summary>
/// <param name="Sticky"><c>true</c> = «hasta quitarla» (con el tope de <see cref="AlertRules.MaxStickySeconds"/>).</param>
/// <param name="ExpiresAt">Cuándo caduca (ISO-8601 UTC). SOLO informativo para la cuenta atrás del panel: quien la quita es el reloj del backend; la salida lo ignora.</param>
public sealed record AlertOverlay(string Id, string Text, AlertMode Mode, AlertTheme Theme, bool Sticky, string ExpiresAt);

/// <summary>Ajustes de las alertas (<c>settings.json</c> → <c>alertDisplay</c>): modo y duración por defecto del panel y tema.</summary>
/// <param name="DurationSec">0 = «hasta quitarla»; si no, de <see cref="AlertRules.MinDurationSec"/> a <see cref="AlertRules.MaxStickySeconds"/> segundos.</param>
/// <param name="TickerEnabled">Si el desplazamiento está permitido (v16). Es un interruptor de SEGURIDAD: si en el proyector real el desplazamiento baja la fluidez del video se desactiva aquí y solo queda la cinta estática; con él apagado, mostrar una alerta en modo <c>ticker</c> se rechaza y un <c>settings.json</c> con modo <c>ticker</c> vuelve a <c>band</c>.</param>
public sealed record AlertDisplaySettings(AlertMode Mode = AlertMode.Band, int DurationSec = AlertRules.DefaultDurationSec, AlertTheme? Theme = null, bool TickerEnabled = true)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public AlertTheme EffectiveTheme => Theme ?? AlertTheme.Default;
}
