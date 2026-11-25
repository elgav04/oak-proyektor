namespace OakProyektor.Core.Songs;

/// <summary>Fallo esperado del módulo de canciones. <c>Message</c> es apto para el operador y NUNCA lleva letra de canciones (los errores no se parecen a lo que se proyecta).</summary>
public sealed class SongException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}

public static class SongErrorCodes
{
    public const string NotFound = "song.notFound";
    public const string InvalidRequest = "song.invalidRequest";
    public const string Conflict = "song.conflict";
    public const string TextTooLarge = "song.textTooLarge";
    public const string TooManySections = "song.tooManySections";
    public const string Busy = "song.busy";
    public const string FileMissing = "song.fileMissing";
    public const string FileTooLarge = "song.fileTooLarge";
    public const string NotText = "song.notText";
    public const string InvalidSettings = "song.invalidSettings";
}

/// <summary>Límites del módulo (los valida el backend; el panel nunca recorta en silencio: lo que pasa de aquí se rechaza o se informa).</summary>
public static class SongLimits
{
    /// <summary>Texto máximo que procesa el parser de una vez (≈ 200 KB de texto).</summary>
    public const int MaxTextChars = 200_000;
    /// <summary>Tamaño máximo de un archivo .txt importado (en bytes; UTF-16 ocupa el doble que UTF-8).</summary>
    public const int MaxFileBytes = 256 * 1024;
    public const int MaxSections = 60;
    /// <summary>Igual que <c>MAX_BLOCK_CHARS</c> del panel: una sección cabe en un bloque proyectable.</summary>
    public const int MaxSectionChars = 4000;
    public const int MaxArrangementItems = 200;
    public const int MaxLabelChars = 60;
    public const int MaxTitleChars = 200;
    public const int MaxAuthorChars = 200;
    public const int MaxCopyrightChars = 500;
    public const int MaxCcliChars = 40;
    public const int MaxNotesChars = 4000;
    public const int MaxBackgroundPathChars = 1000;
    /// <summary>Copias previas (<c>SongRevisions</c>) que se conservan por canción.</summary>
    public const int MaxRevisionsPerSong = 20;
    /// <summary>Máximo de resúmenes que devuelve la lista completa.</summary>
    public const int MaxListed = 2000;
    public const int DefaultSearchLimit = 50;
    public const int MaxSearchLimit = 200;
    /// <summary>Avisos máximos que lleva un resultado del parser (un texto hostil no debe generar miles).</summary>
    public const int MaxWarnings = 20;
}

/// <summary>Tipos de sección (columna <c>SongSections.Kind</c>). Se DERIVAN de la etiqueta (<see cref="SectionAliases.KindOf"/>): «Verso 1» es <c>verse</c>.</summary>
public static class SongSectionKinds
{
    public const string Verse = "verse";
    public const string Chorus = "chorus";
    public const string PreChorus = "prechorus";
    public const string Bridge = "bridge";
    public const string Ending = "ending";
    public const string Intro = "intro";
    public const string Interlude = "interlude";
    public const string Other = "other";

    public static readonly IReadOnlyList<string> All = [Verse, Chorus, PreChorus, Bridge, Ending, Intro, Interlude, Other];

    public static bool IsValid(string? kind) => kind is not null && All.Contains(kind);
}
