namespace OakProyektor.Core.Library;

// Modelos mínimos de la biblioteca; reflejan las tablas de la BD.

public sealed record Bible(long Id, string Abbreviation, string Name, string Language);

public sealed record Book(long Id, long BibleId, int BookNumber, string Name, string Testament);

public sealed record Verse(long Id, long BookId, int Chapter, int VerseNumber, string Text);

public sealed record Song(long Id, string Title, string? Author, string? Copyright, string? CcliNumber, DateTimeOffset CreatedAt);

public sealed record SongSlide(long Id, long SongId, int SequenceIndex, string? Label, string Content);

public sealed record Playlist(long Id, string Name, string? ServiceDate, string ItemsJson);

public sealed record Presentation(long Id, string Title, string SourceType, DateTimeOffset CreatedAt);

public sealed record PresentationSlide(long Id, long PresentationId, int SlideIndex, string? ImagePath, string ContentJson);
