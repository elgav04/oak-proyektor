using System.Globalization;
using System.Text;

namespace OakProyektor.Core.Output;

/// <summary>
/// Reglas de las alertas: función pura, el backend es quien valida (la salida solo pinta). Los motivos van en español y NUNCA citan el texto de la alerta.
/// </summary>
public static class AlertRules
{
    public const int MaxTextChars = 300;
    public const int MinDurationSec = 5;
    /// <summary>Tope de cordura de «hasta quitarla»: ningún aviso queda para siempre (30 min).</summary>
    public const int MaxStickySeconds = 1800;
    public const int DefaultDurationSec = 30;

    /// <summary>
    /// Normaliza el texto (NFC, espacios en blanco —incluidos saltos de línea y tabuladores— colapsados a uno, recortado). Devuelve el motivo si no es válido y <paramref name="text"/> vacío;
    /// null si es válido. Los demás caracteres de control se rechazan.
    /// </summary>
    public static string? TryNormalizeText(string? raw, out string text)
    {
        text = "";
        if (raw is null) return "La alerta no tiene texto.";
        var sb = new StringBuilder(raw.Length);
        var pendingSpace = false;
        foreach (var ch in raw.Normalize(NormalizationForm.FormC))
        {
            if (char.IsWhiteSpace(ch)) { pendingSpace = sb.Length > 0; continue; }
            if (char.IsControl(ch)) return "La alerta no puede llevar caracteres de control.";
            if (pendingSpace) { sb.Append(' '); pendingSpace = false; }
            sb.Append(ch);
            if (sb.Length > MaxTextChars) return $"El aviso es demasiado largo (máximo {MaxTextChars} caracteres).";
        }
        if (sb.Length == 0) return "La alerta no tiene texto.";
        text = sb.ToString();
        return null;
    }

    /// <summary>Clave de comparación: dos avisos que solo se distinguen por mayúsculas o espacios son el mismo. El texto ya debe estar normalizado.</summary>
    public static string TextKey(string normalizedText) => normalizedText.ToLower(CultureInfo.InvariantCulture);

    /// <summary>0 = «hasta quitarla»; si no, de 5 s a 30 min. El panel ofrece 15 s, 30 s, 1 min, 2 min y 5 min.</summary>
    public static string? ValidateDuration(int durationSec) =>
        durationSec == 0 || durationSec is >= MinDurationSec and <= MaxStickySeconds
            ? null
            : $"Alertas: la duración debe ser 0 (hasta quitarla) o de {MinDurationSec} a {MaxStickySeconds} segundos.";

    public static string? ValidateMode(AlertMode mode) => Enum.IsDefined(mode) ? null : "Alertas: modo desconocido (debe ser «band» o «ticker»).";

    public static string? ValidateTheme(AlertTheme? t)
    {
        if (t is null) return "Alertas: falta el tema.";
        if (!ContentValidator.IsFontFamily(t.FontFamily)) return "Alertas: la familia de fuente solo admite letras, números, espacios, comas, guiones y apóstrofos (máx. 100).";
        if (t.FontSize is < 16 or > 200) return "Alertas: el tamaño de fuente debe estar entre 16 y 200 (píxeles del lienzo de 1920×1080).";
        if (t.MinFontSize < 12 || t.MinFontSize > t.FontSize) return "Alertas: el tamaño mínimo debe estar entre 12 y el tamaño de fuente.";
        if (ContentValidator.ValidateColor(t.Color, "Alertas (texto)") is { } c1) return c1;
        if (ContentValidator.ValidateColor(t.OutlineColor, "Alertas (contorno)") is { } c2) return c2;
        if (ContentValidator.ValidateColor(t.BandColor, "Alertas (cinta)") is { } c3) return c3;
        if (t.OutlineWidth is < 0 or > 20) return "Alertas: el grosor del contorno debe estar entre 0 y 20.";
        if (!Enum.IsDefined(t.Align)) return "Alertas: alineación desconocida.";
        if (!Enum.IsDefined(t.Position)) return "Alertas: posición desconocida (debe ser «bottom» o «top»).";
        if (t.PaddingY is < 0 or > 120) return "Alertas: el relleno vertical debe estar entre 0 y 120.";
        if (t.MaxLines is < 1 or > 3) return "Alertas: los renglones de la cinta deben estar entre 1 y 3.";
        if (t.TickerSpeed is < 60 or > 400) return "Alertas: la velocidad del desplazamiento debe estar entre 60 y 400 píxeles por segundo.";
        return null;
    }

    public const string TickerDisabledMessage = "El desplazamiento está desactivado en los ajustes de las alertas: usa la cinta estática.";

    /// <summary>El modo pedido debe estar permitido: con el desplazamiento desactivado solo vale la cinta.</summary>
    public static string? ValidateModeAllowed(AlertMode mode, bool tickerEnabled) =>
        ValidateMode(mode) ?? (mode == AlertMode.Ticker && !tickerEnabled ? TickerDisabledMessage : null);

    public static string? ValidateSettings(AlertDisplaySettings s) =>
        ValidateModeAllowed(s.Mode, s.TickerEnabled) ?? ValidateDuration(s.DurationSec) ?? ValidateTheme(s.EffectiveTheme);
}
