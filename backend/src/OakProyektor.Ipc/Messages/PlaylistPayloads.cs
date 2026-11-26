using OakProyektor.Core.Playlists;

namespace OakProyektor.Ipc.Messages;

// Mensajes de las listas de culto (protocolo v13). Comandos PEQUEÑOS y atómicos (nunca «guardar la lista entera»): una vista desactualizada no puede borrar elementos.
// Mover, quitar y editar exigen la revisión vigente de la lista (playlist.conflict si caducó); añadir no la exige. Los mensajes llevan títulos y notas del operador solo en las
// consultas y comandos que los necesitan; NUNCA letra ni texto bíblico, y los eventos, errores y registros llevan solo ids y conteos.

/// <summary>Una lista en el listado. <c>ServiceDate</c> nulo = plantilla (sin fecha).</summary>
public sealed record PlaylistSummaryDto(long Id, string Name, string? ServiceDate, int ItemCount, int MissingCount, int Revision);

/// <summary>Reply de <c>playlist.list</c>: las que tienen fecha primero (la más reciente arriba), luego las plantillas; como mucho 500.</summary>
public sealed record PlaylistListPayload(bool Truncated, IReadOnlyList<PlaylistSummaryDto> Playlists);

public sealed record PlaylistGetRequest(long Id);

/// <summary>
/// Un elemento de la lista. <c>ItemType</c>: <c>song</c> | <c>passage</c> | <c>note</c>. <c>Title</c> es el título ACTUAL de la canción (la instantánea si falta). <c>IsMissing</c>: la
/// canción ya no existe. <c>Resolution</c> (solo pasajes): <c>resolved</c> | <c>bibleNotInstalled</c> | <c>passageNotFound</c>; <c>OfferedBible</c>: la Biblia por defecto que sí tiene el pasaje
/// (se OFRECE, nunca se aplica sola). <c>Projectable</c>: lo calcula el backend; «siguiente» y «anterior» saltan lo que no lo es (faltantes, pasajes sin resolver, notas).
/// </summary>
public sealed record PlaylistItemDto(
    long Id, int Position, string ItemType, string Title, bool IsMissing, long? SongId,
    int? BookNumber, int? Chapter, int? VerseStart, int? VerseEnd, string? BibleAbbreviation,
    string? Note, bool ClearOnActivate, string? Resolution, string? OfferedBible, bool Projectable);

/// <summary>Reply de <c>playlist.get</c>: la lista con sus elementos en orden (<c>Position</c> = 0..n−1).</summary>
public sealed record PlaylistPayload(
    long Id, string Name, string? ServiceDate, string Notes, int Revision, string CreatedAt, string UpdatedAt, int MissingCount, IReadOnlyList<PlaylistItemDto> Items);

public sealed record PlaylistCreatePayload(string Name, string? ServiceDate, string? Notes);

/// <summary><c>playlist.update</c>: nombre, fecha (<c>AAAA-MM-DD</c> o nulo = plantilla) y notas. <c>ExpectedRevision</c> es obligatorio.</summary>
public sealed record PlaylistUpdatePayload(long Id, string Name, string? ServiceDate, string? Notes, int ExpectedRevision);

/// <summary>
/// Un elemento nuevo. <c>ItemType</c>: <c>song</c> (<c>SongId</c>) | <c>passage</c> (libro canónico 1 a 66, capítulo, versículos y la abreviatura de la Biblia preferida) | <c>note</c>
/// (<c>Title</c>, <c>Note</c> opcional y <c>ClearOnActivate</c>, apagado por defecto). El título de una canción se toma de la canción.
/// </summary>
public sealed record PlaylistNewItemDto(
    string ItemType, long? SongId, int? BookNumber, int? Chapter, int? VerseStart, int? VerseEnd, string? BibleAbbreviation, string? Title, string? Note, bool ClearOnActivate);

/// <summary><c>playlist.addItem</c>: <c>Position</c> nulo = al final. No exige revisión (añadir no destruye nada), pero sube la de la lista.</summary>
public sealed record PlaylistAddItemPayload(long PlaylistId, PlaylistNewItemDto Item, int? Position);

public sealed record PlaylistMoveItemPayload(long PlaylistId, long ItemId, int ToIndex, int ExpectedRevision);

public sealed record PlaylistRemoveItemPayload(long PlaylistId, long ItemId, int ExpectedRevision);

/// <summary><c>playlist.updateItem</c>: lo que viene nulo no se toca (<c>Note</c> vacío la borra). Una nota admite título, texto y «limpiar al activar»; un pasaje, su Biblia preferida.</summary>
public sealed record PlaylistUpdateItemPayload(
    long PlaylistId, long ItemId, string? Title, string? Note, bool? ClearOnActivate, string? BibleAbbreviation, int ExpectedRevision);

/// <summary><c>playlist.duplicate</c>: copia con otro nombre y fecha. Los elementos faltantes no se copian (el resultado dice cuántos).</summary>
public sealed record PlaylistDuplicatePayload(long Id, string Name, string? ServiceDate);

/// <summary><c>playlist.delete</c> (v14): <c>ExpectedRevision</c> es obligatorio; con una revisión caducada falla con <c>playlist.conflict</c> sin borrar nada.</summary>
public sealed record PlaylistDeletePayload(long Id, int ExpectedRevision);

/// <summary>
/// Evento <c>playlist.changed</c> (a la ventana de control): el RESULTADO de un comando de escritura, correlacionado por su id (un fallo llega como <c>command.failed</c>).
/// <c>Kind</c>: <c>created</c> | <c>updated</c> | <c>itemAdded</c> | <c>itemMoved</c> | <c>itemRemoved</c> | <c>itemUpdated</c> | <c>duplicated</c> | <c>deleted</c>. <c>Revision</c> es la nueva (0 si se borró).
/// En <c>duplicated</c>, <c>PlaylistId</c> es la copia, <c>SourceId</c> el origen y <c>Copied</c>/<c>Skipped</c> los elementos copiados y los omitidos por faltar su canción. Solo ids y conteos.
/// </summary>
public sealed record PlaylistChangedPayload(string CommandId, long PlaylistId, int Revision, string Kind, long? ItemId, long? SourceId, int Copied, int Skipped);

/// <summary>Reply de <c>playlist.getSettings</c> y cuerpo de <c>playlist.setSettings</c>: qué hace «siguiente» al terminar un elemento.</summary>
public sealed record PlaylistSettingsPayload(PlaylistOnItemEnd OnItemEnd);

/// <summary>Una lista que incluye una canción (id, nombre y fecha). Se usa en <c>song.get</c> y <c>song.changed</c> para que el aviso de borrado nombre las listas.</summary>
public sealed record PlaylistRefDto(long Id, string Name, string? ServiceDate);
