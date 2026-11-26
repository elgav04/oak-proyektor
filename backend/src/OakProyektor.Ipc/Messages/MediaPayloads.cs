using OakProyektor.Core.Media;

namespace OakProyektor.Ipc.Messages;

// Mensajes de la biblioteca local de medios (protocolo v17). Los NOMBRES de los elementos viajan solo en las consultas y respuestas que los necesitan (nunca en eventos de progreso, errores ni
// registros); los eventos llevan ids y conteos de bytes. La importación copia el archivo a la carpeta de la aplicación: reproducir nunca lee del USB ni de la red.

/// <summary>Una carpeta de la biblioteca (un solo nivel).</summary>
public sealed record MediaFolderDto(long Id, string Name, int ItemCount);

/// <summary>
/// Un elemento. <c>FileUrl</c>: URL <c>file:///</c> de la COPIA local (nula si falta el archivo); la usa el panel para verificar y sacar la miniatura. <c>ThumbnailUrl</c> nula = sin miniatura aún.
/// <c>StatusCode</c>/<c>StatusMessage</c>: motivo si no se puede usar, o aviso si se puede (p. ej. HEVC). <c>DurationMs</c> nulo = desconocida.
/// </summary>
public sealed record MediaItemDto(
    long Id, MediaItemKind Kind, string Name, long? FolderId, string OriginalFileName, string Ext, long Bytes, long? DurationMs, int? Width, int? Height,
    string? Container, string? VideoCodec, string? AudioCodec, bool HasAudio, MediaStatus Status, string? StatusCode, string? StatusMessage,
    string? FileUrl, string? ThumbnailUrl, int Revision, string CreatedAt, string UpdatedAt);

/// <summary><c>media.list</c>: <c>FolderId</c> nulo = todas; <c>OnlyUnfiled</c> = solo los que no están en ninguna carpeta; <c>Query</c> filtra por nombre. Como mucho 500 por consulta.</summary>
public sealed record MediaListRequest(long? FolderId, bool OnlyUnfiled, MediaItemKind? Kind, string? Query, int? Limit);

/// <summary>Reply de <c>media.list</c>. <c>TotalBytes</c>/<c>TotalItems</c>: toda la biblioteca; <c>FreeBytes</c>: espacio libre del disco de datos (−1 si no se sabe).</summary>
public sealed record MediaListPayload(IReadOnlyList<MediaFolderDto> Folders, IReadOnlyList<MediaItemDto> Items, bool Truncated, int TotalItems, long TotalBytes, long FreeBytes);

public sealed record MediaGetRequest(long Id);

/// <summary><c>media.import</c>: vuelve AL INSTANTE (la copia corre en un hilo de fondo, UNA a la vez). <c>Path</c> es la ruta COMPLETA del archivo (local, extraíble o de red).</summary>
public sealed record MediaImportPayload(string Path, string? Name, long? FolderId);

public sealed record MediaRenamePayload(long Id, string Name);

public sealed record MediaMovePayload(long Id, long? FolderId);

/// <summary><c>media.delete</c>: se rechaza (<c>media.inUse</c>) si es el fondo que está en la salida.</summary>
public sealed record MediaDeletePayload(long Id);

public sealed record MediaCreateFolderPayload(string Name);

public sealed record MediaRenameFolderPayload(long Id, string Name);

/// <summary>Borrar una carpeta NO borra sus elementos: pasan a «sin carpeta».</summary>
public sealed record MediaDeleteFolderPayload(long Id);

/// <summary>Lo que el PANEL averiguó al cargar el archivo en su reproductor oculto: si se reproduce (primer fotograma decodificado) y su duración y dimensiones reales.</summary>
public sealed record MediaReportProbePayload(long MediaId, bool Ok, long? DurationMs, int? Width, int? Height, string? ErrorCode);

/// <summary>Miniatura JPEG (≤ 200 KB, ≤ 640×360) generada por el panel, en base64.</summary>
public sealed record MediaSetThumbnailPayload(long MediaId, string JpegBase64);

/// <summary>Usa un video o una imagen de la biblioteca como fondo (el backend resuelve la copia local y la valida: nunca una ruta del cliente).</summary>
public sealed record MediaSetBackgroundPayload(long MediaId, bool Muted, int TransitionMs);

/// <summary>Avance de la importación. <c>ImportId</c> = id del comando <c>media.import</c>. Solo bytes: ni nombres ni rutas.</summary>
public sealed record MediaImportProgressPayload(string ImportId, string Phase, long DoneBytes, long TotalBytes);

/// <summary>
/// Resultado de la importación. Un duplicado NO es un fallo técnico: <c>ErrorCode = media.duplicate</c> con <c>DuplicateOfId</c> y <c>DuplicateOfName</c> (el nombre solo viaja aquí, al panel).
/// <c>WarningCode</c>/<c>WarningMessage</c>: importó bien pero con un aviso (HEVC, AC-3…).
/// </summary>
public sealed record MediaImportFinishedPayload(
    string ImportId, bool Ok, string? ErrorCode, string? ErrorMessage, long? MediaId, string? WarningCode, string? WarningMessage, long? DuplicateOfId, string? DuplicateOfName);

/// <summary>Resultado de un comando de la biblioteca, correlacionado por su id. <c>Kind</c>: <c>added</c> | <c>updated</c> | <c>deleted</c> | <c>folders</c> | <c>verified</c>. <c>Count</c>: faltantes al verificar.</summary>
public sealed record MediaChangedPayload(string CommandId, string Kind, long? MediaId, int Count);
