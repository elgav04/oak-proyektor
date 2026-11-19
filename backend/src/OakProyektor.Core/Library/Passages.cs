namespace OakProyektor.Core.Library;

/// <summary>Resultado de resolver un pasaje al cargar un servicio.</summary>
public enum PassageStatus
{
    /// <summary>La Biblia preferida está instalada y tiene el pasaje.</summary>
    Resolved,
    /// <summary>La Biblia preferida NO está instalada (se borró, o aún no se importó). El pasaje sigue en el servicio: se muestra como «Biblia no instalada».</summary>
    BibleNotInstalled,
    /// <summary>La Biblia está instalada pero no tiene ningún versículo de ese pasaje (p. ej. una Biblia incompleta).</summary>
    PassageNotFound,
}

/// <summary>
/// Referencia canónica a un pasaje: no depende de ningún Id de la BD, así que sobrevive a borrar o reimportar la Biblia.
/// <paramref name="BookNumber"/> es el número canónico del libro (1 a 66); <paramref name="BibleAbbreviation"/> es la Biblia preferida, como TEXTO.
/// </summary>
public sealed record PassageReference(int BookNumber, int Chapter, int VerseStart, int? VerseEnd, string BibleAbbreviation)
{
    public const int FirstCanonicalBook = 1;
    public const int LastCanonicalBook = 66;

    /// <summary>Último versículo del rango (el inicial si es un solo versículo).</summary>
    public int LastVerse => VerseEnd ?? VerseStart;

    /// <summary>La referencia de un elemento de servicio de tipo pasaje; null si no lo es o si está incompleto.</summary>
    public static PassageReference? From(PlaylistItem item) =>
        item is { ItemType: PlaylistItemTypes.Passage, BookNumber: { } book, Chapter: { } chapter, VerseStart: { } start, BibleAbbreviation: { } abbreviation }
            ? new PassageReference(book, chapter, start, item.VerseEnd, abbreviation)
            : null;
}

/// <summary>
/// Cómo quedó un pasaje al resolverlo. <paramref name="Offered"/>: si la Biblia preferida no sirve y la Biblia POR DEFECTO sí tiene el pasaje, aquí va la
/// resolución con esa Biblia, para OFRECERLA al operador (nunca se sustituye sola: el elemento conserva su Biblia preferida hasta que el operador elija).
/// </summary>
public sealed record PassageResolution(
    PassageStatus Status, string PreferredBibleAbbreviation, long? BibleId, long? BookId, int VersesFound, PassageResolution? Offered = null)
{
    public bool IsResolved => Status == PassageStatus.Resolved;

    /// <summary>El aviso que debe ver el operador, o null si el pasaje se resolvió.</summary>
    public string? Warning => Status switch
    {
        PassageStatus.BibleNotInstalled => $"Biblia no instalada: {PreferredBibleAbbreviation}",
        PassageStatus.PassageNotFound => $"Pasaje no encontrado en {PreferredBibleAbbreviation}",
        _ => null,
    };
}
