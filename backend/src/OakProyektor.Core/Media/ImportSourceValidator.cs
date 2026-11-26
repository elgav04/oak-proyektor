using System.Text.RegularExpressions;
using OakProyektor.Core.Output;

namespace OakProyektor.Core.Media;

/// <summary>Una fuente de importación ya validada.</summary>
public sealed record ImportSource(string Path, MediaItemKind Kind, string Ext, long Length, string OriginalFileName);

public sealed record ImportSourceValidation(ImportSource? Source, string? Code, string? Message)
{
    public bool IsValid => Source is not null;
    public static ImportSourceValidation Ok(ImportSource s) => new(s, null, null);
    public static ImportSourceValidation Reject(string code, string message) => new(null, code, message);
}

/// <summary>
/// Valida de dónde se IMPORTA un archivo. Hermana de <see cref="MediaPathValidator"/> pero con la regla contraria en red y unidades extraíbles: aquí se lee UNA vez de un USB o de la
/// red (ese es el caso de uso: copiar a la biblioteca local); lo que se reproduce después es siempre la copia local. Sí rechaza rutas de dispositivo (<c>\\.\</c>, <c>\\?\</c>), flujos
/// alternativos, caracteres no válidos, extensiones fuera de la lista, archivos vacíos y los que superan el máximo. Función sin estado; nunca lanza.
/// </summary>
public static partial class ImportSourceValidator
{
    [GeneratedRegex(@"^[A-Za-z]:[\\/]")]
    private static partial Regex DriveRootRegex();

    [GeneratedRegex(@"^\\\\[^\\/?.][^\\/]*[\\/][^\\/]+[\\/]")]
    private static partial Regex UncRegex();

    public static ImportSourceValidation Validate(string? rawPath, IMediaFileSystem fs)
    {
        var path = rawPath?.Trim().Trim('"').Trim();
        if (string.IsNullOrEmpty(path)) return Fail(MediaErrorCodes.SourceInvalid, "No se indicó ningún archivo.");
        if (path.Contains('\0') || path.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0) return Fail(MediaErrorCodes.SourceInvalid, "La ruta contiene caracteres no válidos.");
        if (path.StartsWith(@"\\.\", StringComparison.Ordinal) || path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal) && !UncRegex().IsMatch(path.Replace('/', '\\')))
            return Fail(MediaErrorCodes.SourceInvalid, "No se admiten rutas de dispositivo.");

        var isDrive = DriveRootRegex().IsMatch(path);
        var isUnc = !isDrive && UncRegex().IsMatch(path.Replace('/', '\\'));
        if (!isDrive && !isUnc) return Fail(MediaErrorCodes.SourceInvalid, "La ruta debe ser absoluta: con letra de unidad (C:\\…) o de red (\\\\servidor\\recurso\\…).");
        if (isDrive && path.IndexOf(':', 2) >= 0) return Fail(MediaErrorCodes.SourceInvalid, "La ruta contiene caracteres no válidos (flujos alternativos).");

        string canonical;
        try { canonical = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException) { return Fail(MediaErrorCodes.SourceInvalid, "La ruta no es válida."); }
        // «C:\con.mp4» y similares se convierten en rutas de dispositivo al canonicalizar.
        if (canonical.StartsWith(@"\\.\", StringComparison.Ordinal) || canonical.StartsWith(@"\\?\", StringComparison.Ordinal)) return Fail(MediaErrorCodes.SourceInvalid, "No se admiten rutas de dispositivo.");

        // Nombres de dispositivo de Windows (CON, NUL, COM1…, con o sin extensión): abrirlos leería un dispositivo, no un archivo.
        if (IsReservedDeviceName(Path.GetFileNameWithoutExtension(canonical))) return Fail(MediaErrorCodes.SourceInvalid, "No se admiten nombres de dispositivo de Windows.");
        var ext = Path.GetExtension(canonical).ToLowerInvariant();
        var kind = MediaRules.KindOfExtension(ext);
        if (kind is null)
            return Fail(MediaErrorCodes.UnsupportedContainer,
                $"Formato no soportado ({(ext.Length == 0 ? "sin extensión" : ext)}). Video: {string.Join(", ", MediaRules.VideoExtensions)}. Audio: {string.Join(", ", MediaRules.AudioExtensions)}. Imagen: {string.Join(", ", MediaRules.ImageExtensions)}. "
                + "Conviértelo a MP4 (H.264 + AAC) con HandBrake o VLC.");

        var probe = fs.Probe(canonical);
        if (!probe.Exists) return Fail(MediaErrorCodes.SourceInvalid, "El archivo no existe.");
        if (probe.Length == 0) return Fail(MediaErrorCodes.SourceInvalid, "El archivo está vacío.");
        if (probe.Length > MediaLimits.MaxBytes) return Fail(MediaErrorCodes.TooLarge, MediaRules.MessageFor(MediaErrorCodes.TooLarge));
        return ImportSourceValidation.Ok(new ImportSource(canonical, kind.Value, ext, probe.Length, Path.GetFileName(canonical)));
    }

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static bool IsReservedDeviceName(string stem) => ReservedNames.Contains(stem.TrimEnd('.', ' '));

    private static ImportSourceValidation Fail(string code, string message) => ImportSourceValidation.Reject(code, message);
}
