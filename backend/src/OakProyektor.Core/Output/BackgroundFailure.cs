namespace OakProyektor.Core.Output;

/// <summary>Por qué la salida no pudo reproducir el fondo. La salida manda el CÓDIGO; el texto para el operador lo pone el backend (<see cref="BackgroundFailures.Describe"/>).</summary>
public enum BackgroundFailureReason
{
    /// <summary>El navegador no soporta el formato o el códec (p. ej. HEVC/H.265).</summary>
    UnsupportedFormat,
    /// <summary>El archivo se leyó pero no se pudo decodificar (dañado o códec incompatible).</summary>
    DecodeError,
    /// <summary>No se pudo leer el archivo (movido, borrado o unidad desconectada).</summary>
    ReadError,
    /// <summary>La reproducción dejó de avanzar o el medio no llegó a cargar a tiempo.</summary>
    Stalled,
    /// <summary>La imagen no se pudo cargar.</summary>
    ImageError,
}

public static class BackgroundFailures
{
    public static string Describe(BackgroundFailureReason reason, string? fileName)
    {
        var name = string.IsNullOrWhiteSpace(fileName) ? "el fondo" : $"«{fileName}»";
        var what = reason switch
        {
            BackgroundFailureReason.UnsupportedFormat =>
                "el formato o el códec no es compatible con la salida (por ejemplo HEVC/H.265). Convierte el video a MP4 con H.264.",
            BackgroundFailureReason.DecodeError => "no se pudo decodificar (archivo dañado o códec no compatible).",
            BackgroundFailureReason.ReadError => "no se pudo leer el archivo (¿se movió, se borró o se desconectó la unidad?).",
            BackgroundFailureReason.Stalled => "la reproducción se detuvo o no llegó a cargar a tiempo (¿unidad lenta o desconectada?).",
            BackgroundFailureReason.ImageError => "no se pudo cargar la imagen (formato no compatible o archivo dañado).",
            _ => "falló la reproducción.",
        };
        return $"No se pudo mostrar {name}: {what} La salida usa el color de reserva.";
    }
}
