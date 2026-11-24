namespace OakProyektor.Data.Scripture;

// Modelos del módulo de Biblias. Regla de oro: NINGUNO de los informes de importación, validación o verificación lleva texto bíblico; solo conteos,
// códigos de libro (USFM) y referencias numéricas. (Las consultas de pasaje y búsqueda SÍ devuelven texto: es el contenido que se proyecta.)

/// <summary>Fallo esperado del módulo (el IPC lo convierte en un error de protocolo con este <c>Code</c>). El mensaje nunca lleva texto bíblico.</summary>
public sealed class BibleException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}

public static class BibleErrorCodes
{
    public const string NotFound = "bible.notFound";
    public const string InvalidReference = "bible.invalidReference";
    public const string PassageNotFound = "bible.passageNotFound";
    public const string EmptyQuery = "bible.emptyQuery";
    public const string InvalidRequest = "bible.invalidRequest";
    public const string FileMissing = "bible.fileMissing";
    public const string SourceInvalid = "bible.sourceInvalid";
    public const string TooManyDeletions = "bible.tooManyDeletions";
    /// <summary>La reimportación cambiaría demasiados textos ya guardados, el nombre de la Biblia o dejaría vacíos textos que no lo están (¿otra traducción con la misma abreviatura?). Se permite con <c>AllowLargeChange</c>.</summary>
    public const string TooManyChanges = "bible.tooManyChanges";
    /// <summary>La reimportación haría desaparecer un libro o un capítulo entero de lo guardado (¿un archivo incompleto?). Se permite con <c>AllowLargeDeletion</c>.</summary>
    public const string BooksWouldDisappear = "bible.booksWouldDisappear";
    public const string ImportBusy = "bible.importBusy";
    public const string ImportFailed = "bible.importFailed";
    /// <summary>La referencia pide más versículos de los que se devuelven por consulta (<see cref="BibleQueryService.MaxPassageVerses"/>): se rechaza, nunca se recorta en silencio.</summary>
    public const string RangeTooLarge = "bible.rangeTooLarge";
}

/// <param name="Path">Archivo de la Biblia.</param>
/// <param name="FormatId">Formato de la fuente (<c>IBibleSourceFormat.Id</c>); nulo = el aprobado (<see cref="OakProyektor.Core.Scripture.BibleSourceFormats.Default"/>).</param>
/// <param name="NumberingProfileId">Perfil de numeración (<c>NumberingProfiles</c>). <b>Nulo = no se indicó: se CONSERVA el perfil guardado de la Biblia</b> (ninguno si es nueva); «none» = sin uniones, pedido de forma explícita.</param>
/// <param name="Attribution">Aviso de derechos: nulo = no tocar el actual (vacío si es una Biblia nueva).</param>
/// <param name="AllowLargeDeletion">Permite que una reimportación elimine muchos versículos (más del 1 %) o haga desaparecer un libro o capítulo entero. Protege de importar un archivo equivocado o truncado.</param>
/// <param name="AllowLargeChange">Permite que una reimportación cambie muchos textos ya guardados (más del 1 %), cambie el nombre de la Biblia o deje vacíos textos que no lo están. Protege de sobrescribir una Biblia con otra traducción por error de abreviatura.</param>
/// <param name="BackupDirectory">Carpeta donde se hace la copia (<c>VACUUM INTO</c>) previa a una reimportación que vaya a modificar o borrar algo; se conservan las últimas <see cref="BibleImporter.BackupsToKeep"/>. Nulo = sin copia.</param>
public sealed record BibleImportRequest(
    string Path, string Abbreviation, string Name, string Language,
    string? FormatId = null, string? NumberingProfileId = null, string? Attribution = null, bool AllowLargeDeletion = false,
    bool AllowLargeChange = false, string? BackupDirectory = null);

/// <param name="Phase"><c>reading</c> | <c>cleaning</c> | <c>saving</c> | <c>validating</c>.</param>
public sealed record ImportProgress(string Phase, int Done, int Total);

/// <summary>Un elemento que apuntaba a un versículo que la reimportación eliminó (<c>Kind</c> = <c>playlist</c> | <c>history</c>). <c>Reference</c> usa el código USFM («PSA 47:10»).</summary>
public sealed record AffectedItem(string Kind, long Id, long? PlaylistId, string Reference);

public sealed record ValidationReport(
    int Books, int Chapters, int Verses, int EmptyVerses,
    IReadOnlyList<string> MissingBooks,
    IReadOnlyList<string> ChapterCountMismatches,
    int VerseGapChapters, IReadOnlyList<string> VerseGaps,
    int DifferenceFromStandardVerseTotal,
    IReadOnlyList<string> Warnings);

public sealed record ImportReport(
    long BibleId, bool CreatedBible, string FormatId, string NumberingProfile,
    int SourceVerses, int Inserted, int Updated, int Unchanged, int Deleted, int BooksDeleted,
    int CleanedVerses, int JoinedVerses, int DuplicateChaptersDiscarded,
    IReadOnlyList<string> MergesApplied, IReadOnlyList<string> MergesSkipped,
    IReadOnlyList<AffectedItem> AffectedItems,
    ValidationReport Validation, bool FtsIntegrityOk, string? FtsNote,
    IReadOnlyList<string> Warnings, long ElapsedMs,
    int PreviousVerses = 0, string? BackupPath = null, bool NumberingProfileKept = false, int DiscardedVerses = 0, int DuplicateChaptersDifferent = 0);

public sealed record CrossCheckReport(
    string FormatId, string NumberingProfile,
    int InDatabase, int InFile, int Identical, int PunctuationOrSpacingOnly, int AccentOrCaseOnly, int WordsDiffer, int KnownLabelVerses,
    int OnlyInDatabase, int OnlyInFile,
    IReadOnlyList<string> AccentOrCaseRefs, IReadOnlyList<string> WordsDifferRefs, IReadOnlyList<string> KnownLabelRefs,
    IReadOnlyList<string> OnlyInDatabaseRefs, IReadOnlyList<string> OnlyInFileRefs,
    IReadOnlyList<string> Warnings);

// ───────────── consultas ─────────────

public sealed record BibleInfo(long Id, string Abbreviation, string Name, string Language, string Attribution, string? NumberingProfile, int Books, int Verses);

public sealed record BibleVerse(int BookNumber, string BookName, int Chapter, int VerseNumber, string Text);

/// <summary>Un pasaje resuelto. <c>Attribution</c> es el aviso de derechos de esa Biblia (vacío si no tiene): quien lo muestre debe poder enseñarlo.</summary>
public sealed record BiblePassage(string Bible, string Attribution, string Reference, IReadOnlyList<BibleVerse> Verses);

public sealed record BibleSearchHit(string Bible, int BookNumber, string BookName, int Chapter, int VerseNumber, string Fragment);

public sealed record BibleSearchResult(string Query, bool Truncated, IReadOnlyList<BibleSearchHit> Hits);

public sealed record CitationEntry(long Id, string Bible, int BookNumber, string BookName, int Chapter, int? VerseStart, int? VerseEnd, string Label, string CitedAt);

/// <summary>Configuración que el módulo lee en cada consulta (viene de <c>settings.json</c>; así Data no depende de la aplicación).</summary>
public sealed record BibleQueryOptions(Func<string?> DefaultBible, Func<int> HistoryLimit)
{
    public const int DefaultHistoryLimit = 100;
    public const int MaxHistoryLimit = 1000;
    public static BibleQueryOptions Default { get; } = new(() => null, () => DefaultHistoryLimit);
}
