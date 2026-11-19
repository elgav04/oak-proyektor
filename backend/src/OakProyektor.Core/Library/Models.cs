namespace OakProyektor.Core.Library;

// Modelos mínimos de la biblioteca; reflejan las tablas de la BD.

public sealed record Bible(long Id, string Abbreviation, string Name, string Language);

public sealed record Book(long Id, long BibleId, int BookNumber, string Name, string Testament);

public sealed record Verse(long Id, long BookId, int Chapter, int VerseNumber, string Text);

public sealed record Song(long Id, string Title, string? Author, string? Copyright, string? CcliNumber, DateTimeOffset CreatedAt);

public sealed record SongSlide(long Id, long SongId, int SequenceIndex, string? Label, string Content);

public sealed record Playlist(long Id, string Name, string? ServiceDate);

/// <summary>Tipos de elemento de un servicio (columna <c>PlaylistItems.ItemType</c>).</summary>
public static class PlaylistItemTypes
{
    public const string Song = "SONG";
    public const string Presentation = "PRESENTATION";
    public const string Passage = "PASSAGE";
}

/// <summary>
/// Elemento de un servicio. <c>Title</c> es una instantánea del nombre al añadirlo.
/// <list type="bullet">
/// <item><b>Canción o presentación</b>: clave foránea real. Si el registro se borra (su FK pasa a NULL), el elemento NO desaparece ni queda con una
/// referencia rota: <see cref="IsMissing"/> es true y el título permite avisar al operador de qué falta («Falta: …») al abrir el servicio.</item>
/// <item><b>Pasaje</b>: referencia CANÓNICA, sin clave foránea: <see cref="BookNumber"/> (1 a 66), <see cref="Chapter"/>, <see cref="VerseStart"/>,
/// <see cref="VerseEnd"/> y <see cref="BibleAbbreviation"/> (la Biblia preferida, como TEXTO). Borrar o reimportar una Biblia no lo toca nunca;
/// se RESUELVE al cargar el servicio (<see cref="PassageResolution"/>) y, si la Biblia no está instalada, se muestra como «Biblia no instalada».
/// <see cref="IsMissing"/> es siempre false para un pasaje.</item>
/// </list>
/// </summary>
public sealed record PlaylistItem(
    long Id, long PlaylistId, int SequenceIndex, string ItemType, string Title,
    long? SongId, long? PresentationId, int? BookNumber, int? Chapter, int? VerseStart, int? VerseEnd, string? BibleAbbreviation, bool IsMissing);

public sealed record Presentation(long Id, string Title, string SourceType, DateTimeOffset CreatedAt);

public sealed record PresentationSlide(long Id, long PresentationId, int SlideIndex, string? ImagePath, string ContentJson);
