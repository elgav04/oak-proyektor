using System.Globalization;
using System.Text;

namespace OakProyektor.Core.Media;

/// <summary>Tipo de un elemento de la BIBLIOTECA de medios (protocolo v17). No confundir con <c>Output.MediaKind</c>, que solo distingue imagen/video al servir un fondo.</summary>
public enum MediaItemKind { Video, Audio, Image }

/// <summary>
/// <c>Pending</c>: copiado, aún sin verificar en el reproductor del panel. <c>Ok</c>: se reproduce en este equipo. <c>Unsupported</c>: se guardó pero NO se puede proyectar (con su motivo en
/// <see cref="MediaItem.StatusCode"/>). <c>Missing</c>: el archivo desapareció del disco («Verificar biblioteca»).
/// </summary>
public enum MediaStatus { Pending, Ok, Unsupported, Missing }

/// <summary>Fallo esperado del módulo de medios. El mensaje NUNCA cita nombres ni rutas del operador.</summary>
public sealed class MediaException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}

public static class MediaErrorCodes
{
    public const string UnsupportedContainer = "media.unsupportedContainer";
    public const string VideoCodecUnsupported = "media.videoCodecUnsupported";
    /// <summary>Aviso (no error): HEVC se acepta si el equipo lo reproduce, pero se recomienda H.264.</summary>
    public const string VideoCodecWarning = "media.videoCodecWarning";
    public const string AudioCodecUnsupported = "media.audioCodecUnsupported";
    /// <summary>Aviso (no error): AC-3/E-AC-3 puede no oírse en otros equipos.</summary>
    public const string AudioCodecWarning = "media.audioCodecWarning";
    public const string DecodeFailed = "media.decodeFailed";
    public const string NoAudioTrack = "media.noAudioTrack";
    public const string TooLarge = "media.tooLarge";
    public const string DiskFull = "media.diskFull";
    public const string SourceStalled = "media.sourceStalled";
    public const string SourceInvalid = "media.sourceInvalid";
    public const string Duplicate = "media.duplicate";
    public const string NotFound = "media.notFound";
    public const string MissingFile = "media.missingFile";
    public const string Busy = "media.busy";
    public const string ImportBusy = "media.importBusy";
    public const string InvalidRequest = "media.invalidRequest";
    public const string FolderNotFound = "media.folderNotFound";
    public const string FolderDuplicate = "media.folderDuplicate";
    public const string NotBackgroundable = "media.notBackgroundable";
    public const string InvalidThumbnail = "media.invalidThumbnail";
    public const string ImportFailed = "media.importFailed";
    public const string Cancelled = "media.cancelled";
}

public static class MediaLimits
{
    /// <summary>Tamaño máximo de un archivo importado (4 GiB).</summary>
    public const long MaxBytes = 4L * 1024 * 1024 * 1024;
    /// <summary>Reserva de disco: el mayor de 1 GiB y el 5 % del volumen.</summary>
    public const long MinReserveBytes = 1L * 1024 * 1024 * 1024;
    public const int ReservePercent = 5;
    /// <summary>Sin avance durante este tiempo la copia se aborta (USB retirado, red caída).</summary>
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(15);
    public const int CopyBlockBytes = 1024 * 1024;
    public const int MaxNameChars = 120;
    public const int MaxFolderNameChars = 80;
    public const int MaxFolders = 200;
    public const int MaxListed = 500;
    public const int MaxThumbnailBytes = 200 * 1024;
    public const int ThumbnailWidth = 320;
    public const int ThumbnailHeight = 180;
}

/// <summary>Un elemento de la biblioteca. <c>Sha256</c> es el nombre del archivo en disco (con la extensión): el nombre visible es solo un dato.</summary>
public sealed record MediaItem(
    long Id, MediaItemKind Kind, string Name, long? FolderId, string OriginalFileName, string Ext, long Bytes, string Sha256,
    long? DurationMs, int? Width, int? Height, string? Container, string? VideoCodec, string? AudioCodec, bool HasAudio,
    MediaStatus Status, string? StatusCode, bool HasThumbnail, int Revision, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record MediaFolder(long Id, string Name, int ItemCount);

public sealed record MediaListResult(IReadOnlyList<MediaFolder> Folders, IReadOnlyList<MediaItem> Items, bool Truncated, int TotalItems, long TotalBytes);

/// <summary>Lo que el alta de un elemento necesita (ya sondeado). El id lo pone la base de datos.</summary>
public sealed record NewMediaItem(
    MediaItemKind Kind, string Name, long? FolderId, string OriginalFileName, string Ext, long Bytes, string Sha256,
    long? DurationMs, int? Width, int? Height, string? Container, string? VideoCodec, string? AudioCodec, bool HasAudio, string? StatusCode);

/// <summary>Reglas de nombres y extensiones de la biblioteca (funciones puras).</summary>
public static class MediaRules
{
    public static readonly IReadOnlyList<string> VideoExtensions = [".mp4", ".m4v", ".webm"];
    public static readonly IReadOnlyList<string> AudioExtensions = [".mp3", ".m4a", ".aac", ".wav", ".flac"];
    public static readonly IReadOnlyList<string> ImageExtensions = [".jpg", ".jpeg", ".png", ".webp"];

    public static MediaItemKind? KindOfExtension(string? extension) => extension?.ToLowerInvariant() switch
    {
        { } e when VideoExtensions.Contains(e) => MediaItemKind.Video,
        { } e when AudioExtensions.Contains(e) => MediaItemKind.Audio,
        { } e when ImageExtensions.Contains(e) => MediaItemKind.Image,
        _ => null,
    };

    /// <summary>Nombre visible: NFC, espacios colapsados, sin caracteres de control, recortado a 120. Vacío → «Sin nombre».</summary>
    public static string NormalizeName(string? raw, int max = MediaLimits.MaxNameChars)
    {
        var sb = new StringBuilder();
        var pending = false;
        foreach (var ch in (raw ?? "").Normalize(NormalizationForm.FormC))
        {
            if (char.IsWhiteSpace(ch)) { pending = sb.Length > 0; continue; }
            if (char.IsControl(ch)) continue;
            if (pending) { sb.Append(' '); pending = false; }
            sb.Append(ch);
            if (sb.Length >= max) break;
        }
        var text = sb.ToString().TrimEnd();
        return text.Length == 0 ? "Sin nombre" : text;
    }

    /// <summary>Nombre inicial de un archivo importado: su nombre sin la extensión.</summary>
    public static string NameFromFile(string fileName) => NormalizeName(Path.GetFileNameWithoutExtension(fileName));

    /// <summary>Clave de comparación de nombres de carpeta (sin distinguir mayúsculas).</summary>
    public static string FolderKey(string name) => name.ToLower(CultureInfo.InvariantCulture);

    /// <summary>Nombre en disco: el SHA-256 en hexadecimal y la extensión en minúsculas. Siempre ASCII: sin espacios, acentos ni nombres reservados de Windows.</summary>
    public static string StoredFileName(string sha256, string ext) => sha256.ToLowerInvariant() + ext.ToLowerInvariant();

    /// <summary>Subcarpeta por los 2 primeros caracteres del hash (evita carpetas con miles de archivos).</summary>
    public static string ShardOf(string sha256) => sha256[..2].ToLowerInvariant();

    public static string MessageFor(string code) => code switch
    {
        MediaErrorCodes.UnsupportedContainer => "Este archivo no se puede usar: el formato no es válido o no está soportado. Conviértelo a MP4 (H.264 + AAC) con HandBrake o VLC y vuelve a importarlo.",
        MediaErrorCodes.VideoCodecUnsupported => "El video usa un códec que este equipo no reproduce (por ejemplo HEVC/H.265 sin la extensión de Windows). Conviértelo a H.264.",
        MediaErrorCodes.VideoCodecWarning => "Este video usa HEVC (H.265): se reproduce en este equipo, pero depende de Windows y de la tarjeta gráfica. Se recomienda H.264.",
        MediaErrorCodes.AudioCodecUnsupported => "El audio de este archivo usa un formato que no se reproduce (por ejemplo AC-3, DTS u Opus). Conviértelo a AAC o MP3.",
        MediaErrorCodes.AudioCodecWarning => "El audio de este video usa AC-3/E-AC-3: puede no oírse en otros equipos. Se recomienda AAC.",
        MediaErrorCodes.DecodeFailed => "No se pudo reproducir: el archivo está dañado o incompleto.",
        MediaErrorCodes.NoAudioTrack => "Este video no tiene audio.",
        MediaErrorCodes.TooLarge => "El archivo supera el máximo de 4 GiB.",
        MediaErrorCodes.DiskFull => "No hay espacio suficiente en el disco para importar este archivo.",
        MediaErrorCodes.SourceStalled => "La copia se detuvo: el archivo de origen dejó de leerse (¿se retiró el USB o se cayó la red?). No se importó nada.",
        MediaErrorCodes.SourceInvalid => "No se puede importar ese archivo.",
        MediaErrorCodes.Duplicate => "Ese archivo ya está en la biblioteca.",
        MediaErrorCodes.NotFound => "El elemento no existe (puede que se haya borrado).",
        MediaErrorCodes.MissingFile => "El archivo de este elemento ya no está en el disco.",
        _ => "Error en la biblioteca de medios.",
    };
}
