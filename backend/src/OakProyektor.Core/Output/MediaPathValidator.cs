using System.Text.RegularExpressions;

namespace OakProyektor.Core.Output;

public enum MediaKind { Image, Video }

public enum MediaRejectionReason
{
    Empty,
    InvalidPath,
    NotAbsolute,
    NetworkPath,
    NetworkDrive,
    UnsupportedExtension,
    KindMismatch,
    NotFound,
    EmptyFile,
}

/// <summary>Medio ya validado: <see cref="Url"/> (<c>file:///…</c>) es lo ÚNICO que se envía a la salida.</summary>
/// <param name="Removable">Está en una unidad extraíble (USB, disco óptico): puede desaparecer en pleno culto.</param>
public sealed record ValidatedMedia(MediaKind Kind, string Path, string Url, string FileName, bool Removable);

public sealed record MediaValidation(ValidatedMedia? Media, MediaRejectionReason? Reason, string? Message)
{
    public bool IsValid => Media is not null;
    public static MediaValidation Ok(ValidatedMedia media) => new(media, null, null);
    public static MediaValidation Reject(MediaRejectionReason reason, string message) => new(null, reason, message);
}

/// <summary>Lo que la validación necesita del sistema de archivos (se sustituye en los tests).</summary>
public interface IMediaFileSystem
{
    /// <summary>Tipo de la unidad de <paramref name="root"/> (p. ej. <c>C:\</c>); <see cref="DriveType.Unknown"/> si no se sabe.</summary>
    DriveType DriveTypeOf(string root);
    /// <summary>Datos del archivo; <c>Exists</c> es false si no existe o es una carpeta. <c>FinalPath</c> es el destino final si es un enlace simbólico; null si no lo es.</summary>
    FileProbe Probe(string path);
}

public sealed record FileProbe(bool Exists, long Length, string? FinalPath);

public sealed class PhysicalMediaFileSystem : IMediaFileSystem
{
    public DriveType DriveTypeOf(string root)
    {
        try { return new DriveInfo(root).DriveType; }
        catch (ArgumentException) { return DriveType.Unknown; }
    }

    public FileProbe Probe(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return new FileProbe(false, 0, null);   // FileInfo.Exists es false para carpetas
            string? final = null;
            if (info.LinkTarget is not null) final = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
            return new FileProbe(true, info.Length, final);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return new FileProbe(false, 0, null);
        }
    }
}

/// <summary>
/// Valida una ruta de medio ANTES de enviar nada a la salida: ruta canónica local con letra de unidad, sin rutas de red (UNC ni unidades de red),
/// extensión permitida, archivo existente y no vacío. Avisa si está en una unidad extraíble. Función sin estado; nunca lanza.
/// </summary>
public static partial class MediaPathValidator
{
    public static readonly IReadOnlyList<string> VideoExtensions = [".mp4", ".m4v", ".webm"];
    public static readonly IReadOnlyList<string> ImageExtensions = [".jpg", ".jpeg", ".png", ".webp"];

    [GeneratedRegex(@"^[A-Za-z]:[\\/]")]
    private static partial Regex DriveRootRegex();

    public static MediaKind? KindOfExtension(string? extension) => extension?.ToLowerInvariant() switch
    {
        { } e when VideoExtensions.Contains(e) => MediaKind.Video,
        { } e when ImageExtensions.Contains(e) => MediaKind.Image,
        _ => null,
    };

    public static MediaValidation Validate(string? rawPath, IMediaFileSystem fs, MediaKind? expected = null)
    {
        var first = Check(rawPath, fs, expected, followLinks: true);
        return first;
    }

    /// <summary>
    /// <c>file:///C:/carpeta/archivo.mp4</c> con cada segmento escapado. No se usa <c>new Uri(ruta).AbsoluteUri</c>: no escapa «%», y «a%20b.mp4» apuntaría a «a b.mp4» (otro archivo).
    /// </summary>
    internal static string ToFileUrl(string canonicalPath)
    {
        var rest = canonicalPath[3..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        return $"file:///{char.ToUpperInvariant(canonicalPath[0])}:/" + string.Join("/", rest.Select(Uri.EscapeDataString));
    }

    private static MediaValidation Check(string? rawPath, IMediaFileSystem fs, MediaKind? expected, bool followLinks)
    {
        var path = rawPath?.Trim();
        if (string.IsNullOrEmpty(path)) return MediaValidation.Reject(MediaRejectionReason.Empty, "No se indicó ningún archivo.");

        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            return MediaValidation.Reject(MediaRejectionReason.NetworkPath,
                "No se admiten rutas de red ni de dispositivo (\\\\servidor\\…). Copia el archivo a un disco local.");
        if (path.Contains('\0') || path.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0)
            return MediaValidation.Reject(MediaRejectionReason.InvalidPath, "La ruta contiene caracteres no válidos.");
        if (!DriveRootRegex().IsMatch(path))
            return MediaValidation.Reject(MediaRejectionReason.NotAbsolute, "La ruta debe ser absoluta, con letra de unidad (por ejemplo C:\\Videos\\fondo.mp4).");
        if (path.IndexOf(':', 2) >= 0)
            return MediaValidation.Reject(MediaRejectionReason.InvalidPath, "La ruta contiene caracteres no válidos (flujos alternativos).");

        string canonical;
        try { canonical = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return MediaValidation.Reject(MediaRejectionReason.InvalidPath, "La ruta no es válida.");
        }
        // Tras canonicalizar también: «C:\con.mp4» y similares se convierten en rutas de dispositivo.
        if (!DriveRootRegex().IsMatch(canonical) || canonical.StartsWith(@"\\", StringComparison.Ordinal))
            return MediaValidation.Reject(MediaRejectionReason.InvalidPath, "La ruta no apunta a un archivo normal de un disco local.");

        var extension = Path.GetExtension(canonical);
        var kind = KindOfExtension(extension);
        if (kind is null)
            return MediaValidation.Reject(MediaRejectionReason.UnsupportedExtension,
                $"Formato no soportado ({(extension.Length == 0 ? "sin extensión" : extension)}). Video: {string.Join(", ", VideoExtensions)}. Imagen: {string.Join(", ", ImageExtensions)}.");
        if (expected is { } want && want != kind)
            return MediaValidation.Reject(MediaRejectionReason.KindMismatch,
                $"El archivo es {(kind == MediaKind.Video ? "un video" : "una imagen")}, pero se pidió {(want == MediaKind.Video ? "un video" : "una imagen")}.");

        // Tipo de unidad ANTES de tocar el archivo: una unidad de red desconectada puede bloquear Exists durante segundos.
        var root = Path.GetPathRoot(canonical) ?? "";
        var drive = fs.DriveTypeOf(root);
        if (drive == DriveType.Network)
            return MediaValidation.Reject(MediaRejectionReason.NetworkDrive, "El archivo está en una unidad de red. Copia el archivo a un disco local.");
        var removable = drive is DriveType.Removable or DriveType.CDRom;

        var probe = fs.Probe(canonical);
        if (!probe.Exists) return MediaValidation.Reject(MediaRejectionReason.NotFound, $"El archivo no existe: {System.IO.Path.GetFileName(canonical)}.");
        if (probe.FinalPath is { } target && followLinks && !string.Equals(target, canonical, StringComparison.OrdinalIgnoreCase))
        {
            // Un enlace simbólico se valida por su DESTINO real (podría apuntar a la red o a otra unidad).
            var inner = Check(target, fs, expected, followLinks: false);
            return inner.IsValid ? MediaValidation.Ok(inner.Media! with { FileName = System.IO.Path.GetFileName(canonical) }) : inner;
        }
        if (probe.Length == 0) return MediaValidation.Reject(MediaRejectionReason.EmptyFile, "El archivo está vacío.");

        return MediaValidation.Ok(new ValidatedMedia(kind.Value, canonical, ToFileUrl(canonical), System.IO.Path.GetFileName(canonical), removable));
    }
}
