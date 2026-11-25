using OakProyektor.Core.Output;
using OakProyektor.Core.Songs;

namespace OakProyektor.Ipc.Messages;

// Mensajes del módulo de canciones (protocolo v12). La letra viaja SOLO en las consultas y comandos que la necesitan (song.get, song.parseText, song.save, song.search);
// nunca en eventos, errores ni registros. Los errores esperados son IpcException con los códigos de SongErrorCodes y mensajes sin letra.

/// <summary>
/// Una sección. <c>Id</c> nulo = sección nueva (al guardar) o sin guardar aún (resultado de <c>song.parseText</c>). <c>Kind</c> se deriva de la etiqueta: al guardar se ignora lo que mande el
/// panel. Lleva una línea en blanco por cada corte manual de diapositiva.
/// </summary>
public sealed record SongSectionDto(long? Id, string Kind, string Label, string Text);

/// <summary>Fondo propio de una canción. <c>Kind</c>: <c>image</c> | <c>video</c> | <c>color</c>. Sin fondo (null) = no se toca el fondo actual de la salida.</summary>
public sealed record SongBackgroundDto(string Kind, string? Path, string? Color, bool Muted);

/// <summary>Una canción de la lista o de una búsqueda. <c>Fragment</c> (solo en <c>song.search</c>) lleva las coincidencias entre « y ».</summary>
public sealed record SongSummaryDto(long Id, string Title, string? Author, string UpdatedAt, string? Fragment);

/// <summary><c>song.search</c>: título, autor y letra, sin tildes. <c>Limit</c> ≤ 0 = 50; máximo 200.</summary>
public sealed record SongSearchRequest(string Query, int Limit);

public sealed record SongSearchPayload(string Query, bool Truncated, IReadOnlyList<SongSummaryDto> Songs);

/// <summary>Reply de <c>song.list</c>: todas las canciones por título (sin letra), como mucho 2000; <c>Truncated</c> si había más.</summary>
public sealed record SongListPayload(bool Truncated, IReadOnlyList<SongSummaryDto> Songs);

public sealed record SongGetRequest(long Id);

/// <summary>
/// Reply de <c>song.get</c>. <c>Arrangement</c> son índices (posiciones) en <c>Sections</c>: el orden de reproducción con repeticiones. <c>Text</c> es el texto único con etiquetas
/// reconstruido por el backend (el editor lo muestra tal cual). <c>UsedInServices</c> = en cuántos servicios está incluida (para avisar antes de borrar).
/// </summary>
public sealed record SongPayload(
    long Id, string Title, string? Author, string? Copyright, string? CcliNumber, string? Notes, int Revision, string CreatedAt, string UpdatedAt,
    SongBackgroundDto? Background, IReadOnlyList<SongSectionDto> Sections, IReadOnlyList<int> Arrangement, string Text, int UsedInServices);

/// <summary>
/// <c>song.parseText</c>: interpreta un texto pegado o leído de un .txt (función pura, sin BD). <c>MergeIdenticalBlocks</c>: sin etiquetas, fusionar los bloques idénticos como
/// una repetición.
/// </summary>
public sealed record SongParseTextRequest(string Text, bool MergeIdenticalBlocks);

/// <summary>
/// Reply de <c>song.parseText</c>. <c>Detected</c>: <c>empty</c> | <c>brackets</c> | <c>aliasLines</c> | <c>blocks</c>. <c>Errors</c> impiden guardar (límites, orden vacío…);
/// <c>Warnings</c> solo informan. Ninguno cita la letra.
/// </summary>
public sealed record SongParseTextPayload(
    IReadOnlyList<SongSectionDto> Sections, IReadOnlyList<int> Arrangement, IReadOnlyList<string> Warnings, IReadOnlyList<string> Errors, string Detected);

/// <summary>
/// <c>song.save</c>: crea (<c>Id</c> nulo) o actualiza una canción COMPLETA. Al actualizar, <c>ExpectedRevision</c> es obligatorio: si ya no es la vigente falla con <c>song.conflict</c> sin
/// cambiar nada. <c>Arrangement</c> son índices en <c>Sections</c>. <c>Reason</c>: <c>save</c> (por defecto) | <c>import</c> (motivo de la copia previa). El resultado llega como el evento
/// <c>song.changed</c> con el <c>id</c> de este comando; un fallo, como <c>command.failed</c>.
/// </summary>
public sealed record SongSavePayload(
    long? Id, int? ExpectedRevision, string Title, string? Author, string? Copyright, string? CcliNumber, string? Notes, SongBackgroundDto? Background,
    IReadOnlyList<SongSectionDto> Sections, IReadOnlyList<int> Arrangement, string? Reason);

public sealed record SongHistoryRequest(long SongId);

/// <summary>Una copia previa de la canción (sin texto). <c>Reason</c>: <c>save</c> | <c>import</c> | <c>restore</c>.</summary>
public sealed record SongRevisionDto(long Id, string CreatedAt, string Reason, int SectionCount);

/// <summary>Reply de <c>song.history</c>: la copia más reciente primero (se conservan las últimas 20).</summary>
public sealed record SongHistoryPayload(IReadOnlyList<SongRevisionDto> Revisions);

/// <summary><c>song.restoreRevision</c>: <c>RevisionId</c> nulo = la copia más reciente («Deshacer último guardado»). Restaurar toma antes una copia del estado actual.</summary>
public sealed record SongRestoreRevisionPayload(long SongId, long? RevisionId, int ExpectedRevision);

public sealed record SongDeletePayload(long Id);

/// <summary>
/// Evento <c>song.changed</c> (a la ventana de control). <c>CommandId</c> es el id del comando que lo causó (<c>song.save</c>, <c>song.restoreRevision</c> o <c>song.delete</c>).
/// <c>Kind</c>: <c>created</c> | <c>updated</c> | <c>restored</c> | <c>deleted</c>. <c>Revision</c> es la nueva (0 si se borró). <c>UsedInServices</c>: servicios donde la canción estaba
/// (al borrar, sus elementos quedan como «Falta: …»). Sin letra.
/// </summary>
public sealed record SongChangedPayload(string CommandId, long SongId, int Revision, string Kind, int UsedInServices);

/// <summary>Reply de <c>song.getSettings</c> y cuerpo de <c>song.setSettings</c>: tema propio de las canciones, crossfade y qué hacer al pasar de la última diapositiva.</summary>
public sealed record SongSettingsPayload(TextTheme Theme, int TransitionMs, SongAtEnd AtEnd);
