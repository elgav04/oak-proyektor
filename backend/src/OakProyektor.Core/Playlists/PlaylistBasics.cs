using System.Globalization;
using OakProyektor.Core.Library;

namespace OakProyektor.Core.Playlists;

/// <summary>Fallo esperado del módulo de listas de culto. <c>Message</c> es apto para el operador y NUNCA lleva títulos, notas ni letra (solo códigos, ids y conteos).</summary>
public sealed class PlaylistException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}

public static class PlaylistErrorCodes
{
    public const string NotFound = "playlist.notFound";
    public const string ItemNotFound = "playlist.itemNotFound";
    public const string InvalidRequest = "playlist.invalidRequest";
    /// <summary>La revisión de la lista que trae la vista ya no es la vigente: nada se cambió; la vista debe volver a leer la lista.</summary>
    public const string Conflict = "playlist.conflict";
    public const string Full = "playlist.full";
    public const string Busy = "playlist.busy";
    public const string InvalidSettings = "playlist.invalidSettings";
}

/// <summary>Límites del módulo (los valida el backend; nunca se recorta en silencio).</summary>
public static class PlaylistLimits
{
    public const int MaxItems = 100;
    public const int MaxNameChars = 120;
    public const int MaxNotesChars = 2000;
    public const int MaxItemNoteChars = 500;
    public const int MaxTitleChars = 200;
    public const int MaxListed = 500;
}

/// <summary>Qué hace «siguiente» al llegar a la última diapositiva de un elemento (<c>settings.json</c> → <c>playlistDisplay</c>).</summary>
public enum PlaylistOnItemEnd
{
    /// <summary>Se queda en la última diapositiva (el panel muestra «FIN · siguiente: …»). Por defecto: una pulsación de más no cambia lo proyectado.</summary>
    Stay,
    /// <summary>Proyecta la primera diapositiva del siguiente elemento proyectable.</summary>
    Next,
    /// <summary>Quita lo proyectado.</summary>
    Clear,
}

/// <summary>Cómo se opera una lista en vivo (protocolo v13). Se guarda en <c>settings.json</c>, NO en la BD de contenido.</summary>
public sealed record PlaylistDisplaySettings(PlaylistOnItemEnd OnItemEnd = PlaylistOnItemEnd.Stay)
{
    /// <summary>Motivo en español si la configuración no es válida; null si lo es. Función pura.</summary>
    public static string? Validate(PlaylistDisplaySettings s) =>
        Enum.IsDefined(s.OnItemEnd) ? null : "Listas: «al terminar un elemento» tiene un valor desconocido.";
}

/// <summary>Referencia a una lista (para avisar «esta canción está en: …»). Solo id, nombre y fecha: nunca letra.</summary>
public sealed record PlaylistRef(long Id, string Name, string? ServiceDate);

/// <summary>Una lista en el listado (sin elementos).</summary>
public sealed record PlaylistSummary(long Id, string Name, string? ServiceDate, int ItemCount, int MissingCount, int Revision);

public sealed record PlaylistListResult(IReadOnlyList<PlaylistSummary> Playlists, bool Truncated);

/// <summary>Estado de un pasaje al resolverlo (ver <see cref="PassageStatus"/>); <c>null</c> en canciones y notas.</summary>
public static class PlaylistResolutions
{
    public const string Resolved = "resolved";
    public const string BibleNotInstalled = "bibleNotInstalled";
    public const string PassageNotFound = "passageNotFound";

    public static string Of(PassageStatus status) => status switch
    {
        PassageStatus.Resolved => Resolved,
        PassageStatus.BibleNotInstalled => BibleNotInstalled,
        _ => PassageNotFound,
    };
}

/// <summary>
/// Un elemento de una lista, tal como se lee. <c>Title</c> es el título ACTUAL de la canción si existe (si no, la instantánea guardada). <c>Projectable</c> lo calcula el backend
/// (<see cref="PlaylistNavigation.IsProjectable"/>): «siguiente» y «anterior» saltan lo que no lo es.
/// </summary>
public sealed record PlaylistItemDetail(
    long Id, int Position, string ItemType, string Title, bool IsMissing, long? SongId,
    int? BookNumber, int? Chapter, int? VerseStart, int? VerseEnd, string? BibleAbbreviation,
    string? Note, bool ClearOnActivate, string? Resolution, string? OfferedBible, bool Projectable);

public sealed record PlaylistDetail(
    long Id, string Name, string? ServiceDate, string Notes, int Revision, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    int MissingCount, IReadOnlyList<PlaylistItemDetail> Items);

/// <summary>
/// Un elemento que se añade. <c>ItemType</c>: <c>SONG</c> (<c>SongId</c>), <c>PASSAGE</c> (referencia canónica + Biblia preferida como texto) o <c>NOTE</c> (<c>Title</c> y, opcional, <c>Note</c>).
/// Para una canción el título se toma de la canción (instantánea); para un pasaje, de su referencia si no se indica.
/// </summary>
public sealed record PlaylistItemInput(
    string ItemType, long? SongId, int? BookNumber, int? Chapter, int? VerseStart, int? VerseEnd, string? BibleAbbreviation,
    string? Title, string? Note, bool ClearOnActivate);

/// <summary>Cambios de un elemento (todo opcional: lo que no viene no se toca). Solo notas y la Biblia preferida de un pasaje se pueden cambiar.</summary>
public sealed record PlaylistItemUpdate(string? Title, string? Note, bool? ClearOnActivate, string? BibleAbbreviation);

public sealed record PlaylistWriteResult(long PlaylistId, int Revision, long? ItemId);

public sealed record PlaylistDuplicateResult(long NewPlaylistId, int Revision, int Copied, int Skipped);

public sealed record PlaylistDeleteResult(long PlaylistId);

/// <summary>Reglas de validación y normalización. Funciones puras.</summary>
public static class PlaylistRules
{
    /// <summary>Fecha de servicio válida: <c>AAAA-MM-DD</c> y un día que existe.</summary>
    public static bool IsValidDate(string? value) =>
        value is { Length: 10 }
        && DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    public static bool HasControlChars(string value) => value.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'));

    /// <summary>Nombre recortado, o lanza <see cref="PlaylistException"/> si está vacío, es demasiado largo o lleva caracteres de control.</summary>
    public static string NormalizeName(string? name)
    {
        var value = (name ?? "").Trim();
        if (value.Length == 0) throw Invalid("La lista necesita un nombre.");
        if (value.Length > PlaylistLimits.MaxNameChars) throw Invalid($"El nombre de la lista supera los {PlaylistLimits.MaxNameChars} caracteres.");
        if (value.Any(char.IsControl)) throw Invalid("El nombre de la lista no puede llevar caracteres de control.");
        return value;
    }

    /// <summary>Fecha normalizada: vacío o nulo = sin fecha (plantilla). Una fecha mal formada se rechaza.</summary>
    public static string? NormalizeDate(string? date)
    {
        var value = date?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        return IsValidDate(value) ? value : throw Invalid("La fecha debe tener la forma AAAA-MM-DD y ser un día que exista.");
    }

    public static string NormalizeNotes(string? notes)
    {
        var value = (notes ?? "").Trim();
        if (value.Length > PlaylistLimits.MaxNotesChars) throw Invalid($"Las notas de la lista superan los {PlaylistLimits.MaxNotesChars} caracteres.");
        if (HasControlChars(value)) throw Invalid("Las notas de la lista no pueden llevar caracteres de control.");
        return value;
    }

    public static string? NormalizeItemNote(string? note)
    {
        var value = note?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        if (value.Length > PlaylistLimits.MaxItemNoteChars) throw Invalid($"La nota supera los {PlaylistLimits.MaxItemNoteChars} caracteres.");
        if (HasControlChars(value)) throw Invalid("La nota no puede llevar caracteres de control.");
        return value;
    }

    public static string NormalizeItemTitle(string? title)
    {
        var value = (title ?? "").Trim();
        if (value.Length == 0) throw Invalid("El elemento necesita un título.");
        if (value.Length > PlaylistLimits.MaxTitleChars) throw Invalid($"El título supera los {PlaylistLimits.MaxTitleChars} caracteres.");
        if (value.Any(char.IsControl)) throw Invalid("El título no puede llevar caracteres de control.");
        return value;
    }

    /// <summary>Etiqueta de un pasaje para su título instantánea: solo números, nunca texto bíblico («Libro 43 3:16-17»).</summary>
    public static string PassageTitle(int book, int chapter, int verseStart, int? verseEnd, string bible) =>
        $"Libro {book} {chapter}:{verseStart}" + (verseEnd is { } end && end != verseStart ? $"-{end}" : "") + $" · {bible}";

    private static PlaylistException Invalid(string message) => new(PlaylistErrorCodes.InvalidRequest, message);
}

/// <summary>
/// Navegación entre elementos: «siguiente» y «anterior» SALTAN lo que no se puede proyectar (canciones borradas, pasajes sin Biblia o sin ese pasaje, notas). Los elementos no
/// se pierden ni se esconden (la lista los muestra y los avisa); solo la navegación pasa de largo. Funciones puras.
/// </summary>
public static class PlaylistNavigation
{
    /// <summary>Un elemento se proyecta si es una canción que existe o un pasaje resuelto. Notas, presentaciones y faltantes no.</summary>
    public static bool IsProjectable(string itemType, bool isMissing, string? resolution) => itemType switch
    {
        PlaylistItemTypes.Song => !isMissing,
        PlaylistItemTypes.Passage => resolution == PlaylistResolutions.Resolved,
        _ => false,
    };

    /// <summary>
    /// Posición (en la lista, no el id) del siguiente (<paramref name="direction"/> = +1) o anterior (−1) elemento proyectable a partir de <paramref name="from"/>
    /// (nulo = antes del primero para +1, después del último para −1). Nulo si no hay.
    /// </summary>
    public static int? Step(IReadOnlyList<PlaylistItemDetail> items, int? from, int direction)
    {
        if (direction is not (1 or -1)) throw new ArgumentOutOfRangeException(nameof(direction));
        var i = from ?? (direction > 0 ? -1 : items.Count);
        for (i += direction; i >= 0 && i < items.Count; i += direction)
            if (items[i].Projectable) return i;
        return null;
    }
}
