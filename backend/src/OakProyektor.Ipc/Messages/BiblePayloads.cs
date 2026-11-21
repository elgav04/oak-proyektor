namespace OakProyektor.Ipc.Messages;

// Mensajes del módulo de Biblias (protocolo v6). Los de importación NUNCA llevan texto bíblico: solo conteos y referencias.
// Las consultas de pasaje y búsqueda sí devuelven texto (es el contenido que se proyecta).

/// <summary>Una Biblia instalada. <c>Attribution</c> es su aviso de derechos (vacío si no tiene). <c>NumberingProfile</c> es el perfil de numeración con que se importó (null = ninguno).</summary>
public sealed record BibleDto(long Id, string Abbreviation, string Name, string Language, string Attribution, string? NumberingProfile, int Books, int Verses);

/// <summary>Reply de <c>bible.list</c>.</summary>
public sealed record BibleListPayload(IReadOnlyList<BibleDto> Bibles);

/// <summary><c>bible.lookup</c>: <c>Reference</c> es lo que escribió el operador («Jn 3:16»); <c>Bible</c> nulo = la predeterminada; <c>Record</c> = anotarla en el historial.</summary>
public sealed record BibleLookupRequest(string Reference, string? Bible, bool Record);

/// <summary><c>bible.chapter</c>: un capítulo completo por número de libro canónico (1 a 66).</summary>
public sealed record BibleChapterRequest(string? Bible, int BookNumber, int Chapter);

public sealed record BibleVerseDto(int BookNumber, string BookName, int Chapter, int VerseNumber, string Text);

/// <summary>Reply de <c>bible.lookup</c> y <c>bible.chapter</c>. <c>Attribution</c> es el aviso de derechos de la Biblia (quien proyecte debe poder mostrarlo).</summary>
public sealed record BiblePassagePayload(string Bible, string Attribution, string Reference, IReadOnlyList<BibleVerseDto> Verses);

/// <summary><c>bible.search</c>: <c>Limit</c> ≤ 0 = el predeterminado (50); máximo 200. Palabras sueltas = todas; entre comillas = frase.</summary>
public sealed record BibleSearchRequest(string Query, string? Bible, int Limit);

/// <param name="Fragment">Trozo del versículo con las coincidencias entre « y ».</param>
public sealed record BibleSearchHitDto(string Bible, int BookNumber, string BookName, int Chapter, int VerseNumber, string Fragment);

/// <summary>Reply de <c>bible.search</c>. <c>Truncated</c> = había más resultados que el límite.</summary>
public sealed record BibleSearchPayload(string Query, bool Truncated, IReadOnlyList<BibleSearchHitDto> Hits);

/// <summary><c>bible.history</c>: <c>Limit</c> ≤ 0 = todo el historial guardado.</summary>
public sealed record BibleHistoryRequest(int Limit);

public sealed record CitationDto(long Id, string Bible, int BookNumber, string BookName, int Chapter, int? VerseStart, int? VerseEnd, string Label, string CitedAt);

/// <summary>Reply de <c>bible.history</c>: la más reciente primero.</summary>
public sealed record BibleHistoryPayload(IReadOnlyList<CitationDto> Entries);

/// <summary><c>bible.suggest</c>: autocompletado del nombre del libro por prefijo.</summary>
public sealed record BibleSuggestRequest(string Prefix, int Limit);

public sealed record BibleSuggestionDto(int BookNumber, string Name, string ShortName, string MatchedAlias);

public sealed record BibleSuggestPayload(IReadOnlyList<BibleSuggestionDto> Suggestions);

/// <summary><c>bible.setAttribution</c>: edita el aviso de derechos de una Biblia (vacío = quitarlo). La aplicación no trae ningún texto por defecto.</summary>
public sealed record BibleSetAttributionPayload(string Bible, string Attribution);

/// <summary>
/// <c>bible.import</c>: importa una Biblia desde un archivo en segundo plano (el comando vuelve al instante; el avance llega como <c>bible.importProgress</c> y el
/// resultado como <c>bible.importFinished</c>). <c>NumberingProfile</c> nulo o «none» = sin uniones de versículos. <c>Attribution</c> nulo = no tocar el actual.
/// </summary>
public sealed record BibleImportPayload(string Path, string Abbreviation, string Name, string Language, string? NumberingProfile, string? Attribution);

/// <summary>Evento <c>bible.importProgress</c>. <c>Phase</c>: <c>reading</c> | <c>cleaning</c> | <c>saving</c> | <c>validating</c>.</summary>
public sealed record BibleImportProgressPayload(string Abbreviation, string Phase, int Done, int Total);

/// <summary>Un elemento de un servicio (<c>kind</c> = <c>playlist</c>) o del historial (<c>history</c>) que apuntaba a un versículo eliminado por la reimportación.</summary>
public sealed record AffectedItemDto(string Kind, long Id, long? PlaylistId, string Reference);

/// <summary>Informe de una importación: solo conteos y referencias.</summary>
public sealed record BibleImportReportDto(
    long BibleId, bool Created, string NumberingProfile,
    int SourceVerses, int Inserted, int Updated, int Unchanged, int Deleted, int CleanedVerses, int JoinedVerses,
    int Books, int Chapters, int Verses, int EmptyVerses, int VerseTotalDifference, bool FtsIntegrityOk,
    IReadOnlyList<AffectedItemDto> AffectedItems, IReadOnlyList<string> Warnings, long ElapsedMs);

/// <summary>Evento <c>bible.importFinished</c>: <c>Report</c> si salió bien; <c>ErrorCode</c> y <c>ErrorMessage</c> si falló.</summary>
public sealed record BibleImportFinishedPayload(string Abbreviation, bool Ok, string? ErrorCode, string? ErrorMessage, BibleImportReportDto? Report);

// ───────────── Configuración de la proyección de Biblia (v8) ─────────────

public sealed record BibleShortNameDto(string Bible, string ShortName);

/// <summary>
/// Reply de <c>bible.getSettings</c>. <c>DefaultBible</c> nulo = la primera instalada. <c>TransitionMs</c> es el crossfade al cambiar de versículo.
/// El aviso de derechos NO va aquí (es de cada Biblia: <c>bible.list</c> / <c>bible.setAttribution</c>).
/// </summary>
public sealed record BibleSettingsPayload(
    string? DefaultBible, bool ProjectOnNavigate, bool SuperscriptNumbers, bool ShowAttribution, int TransitionMs,
    OakProyektor.Core.Output.TextTheme Theme, IReadOnlyList<BibleShortNameDto> ShortNames);

/// <summary><c>bible.setSettings</c>: sustituye TODA la configuración (el panel manda el conjunto completo). Un valor inválido falla con <c>bible.invalidSettings</c> y no cambia nada.</summary>
public sealed record BibleSetSettingsPayload(
    string? DefaultBible, bool ProjectOnNavigate, bool SuperscriptNumbers, bool ShowAttribution, int TransitionMs,
    OakProyektor.Core.Output.TextTheme Theme, IReadOnlyList<BibleShortNameDto> ShortNames);
