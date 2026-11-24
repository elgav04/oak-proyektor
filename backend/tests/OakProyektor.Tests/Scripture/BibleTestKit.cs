using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OakProyektor.Core.Scripture;
using OakProyektor.Data;
using OakProyektor.Data.Scripture;
using OakProyektor.Tests.Data;

namespace OakProyektor.Tests.Scripture;

/// <summary>
/// Datos SINTÉTICOS para las pruebas del módulo de Biblias: todo el «texto» está inventado aquí («Sintetico…», «lorem ipsum»). Ninguna prueba lee
/// <c>data/</c> ni lleva texto bíblico real.
/// </summary>
internal static class SyntheticBible
{
    public sealed record V(int Book, int Chapter, int Verse, string Text);

    /// <summary>Texto inventado y único por versículo (la palabra <c>Sinteticoxx</c> lo identifica en las búsquedas).</summary>
    public static string TextOf(int book, int chapter, int verse) => $"Sintetico{book}x{chapter}x{verse} lorem ipsum dolor";

    public static V Verse(int book, int chapter, int verse, string? text = null) => new(book, chapter, verse, text ?? TextOf(book, chapter, verse));

    /// <summary>Un capítulo con versículos 1..<paramref name="count"/>.</summary>
    public static IEnumerable<V> Chapter(int book, int chapter, int count) => Enumerable.Range(1, count).Select(v => Verse(book, chapter, v));

    /// <summary>
    /// Una Biblia pequeña con los casos difíciles: Génesis 1 y 2, el Salmo 47 con 10 versículos y 3 Juan con 15 (numeración «partida»), Judas (un solo
    /// capítulo) y el Salmo 119 hasta el versículo 24 (para los rótulos de estrofa).
    /// </summary>
    public static List<V> Small()
    {
        var list = new List<V>();
        list.AddRange(Chapter(1, 1, 5));
        list.AddRange(Chapter(1, 2, 4));
        list.AddRange(Chapter(19, 47, 10));
        list.AddRange(Chapter(19, 119, 24));
        list.AddRange(Chapter(40, 5, 12));
        list.AddRange(Chapter(64, 1, 15));
        list.AddRange(Chapter(65, 1, 6));
        return list;
    }

    /// <summary>
    /// Escribe un archivo en el formato aprobado (JSON con <c>books[].chapters[].items[]</c>), incluidos los detalles que el formato real obliga a manejar:
    /// campo <c>html</c> por capítulo (que se descarta), títulos (<c>heading1</c>), los libros de un solo capítulo con el capítulo REPETIDO y,
    /// opcionalmente, versículos partidos en varios items o líneas.
    /// </summary>
    public static string WriteYouVersion(string directory, IEnumerable<V> verses, bool repeatSingleChapterBooks = true, bool splitInTwoLines = false, string fileName = "fuente.json")
    {
        var path = Path.Combine(directory, fileName);
        Directory.CreateDirectory(directory);
        using var stream = File.Create(path);
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteNumber("version_id", 1);
            w.WriteStartArray("books");
            foreach (var book in verses.GroupBy(v => v.Book).OrderBy(g => g.Key))
            {
                var canonical = CanonicalBooks.Get(book.Key);
                w.WriteStartObject();
                w.WriteString("usfm", canonical.Usfm);
                w.WriteString("human", "Libro sintético");
                w.WriteStartArray("chapters");
                foreach (var chapter in book.GroupBy(v => v.Chapter).OrderBy(g => g.Key))
                {
                    var repeats = repeatSingleChapterBooks && canonical.Chapters == 1 ? 2 : 1;
                    for (var r = 0; r < repeats; r++)
                    {
                        w.WriteStartObject();
                        w.WriteString("usfm", $"{canonical.Usfm}.{chapter.Key}");
                        w.WriteString("human", chapter.Key.ToString());
                        w.WriteNull("previous");
                        w.WriteStartObject("next"); w.WriteString("usfm", "X.1"); w.WriteEndObject();
                        w.WriteString("html", "<div class=\"chapter\"><span class=\"content\">HTML sintético que no debe leerse</span></div>");
                        w.WriteStartArray("items");
                        w.WriteStartObject();
                        w.WriteString("type", "heading1"); w.WriteStartArray("verse_numbers"); w.WriteEndArray();
                        w.WriteStartArray("lines"); w.WriteStringValue("Titulo sintetico"); w.WriteEndArray(); w.WriteBoolean("np", true);
                        w.WriteEndObject();
                        foreach (var verse in chapter.OrderBy(v => v.Verse))
                        {
                            w.WriteStartObject();
                            w.WriteString("type", "verse");
                            w.WriteStartArray("verse_numbers"); w.WriteNumberValue(verse.Verse); w.WriteEndArray();
                            w.WriteStartArray("lines");
                            if (splitInTwoLines && verse.Text.Contains(' '))
                            {
                                var cut = verse.Text.IndexOf(' ');
                                w.WriteStringValue(verse.Text[..cut]);
                                w.WriteStringValue(verse.Text[(cut + 1)..]);
                            }
                            else w.WriteStringValue(verse.Text);
                            w.WriteEndArray();
                            w.WriteBoolean("np", false);
                            w.WriteEndObject();
                        }
                        w.WriteEndArray();
                        w.WriteEndObject();
                    }
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return path;
    }

    /// <summary>Escribe un archivo del formato «solo verificación»: <c>{ "Nombre del libro": { "capítulo": { "versículo": "texto" } }, "lang": "x" }</c>.</summary>
    public static string WriteNamedBooks(string directory, IEnumerable<V> verses, string fileName = "otra.json")
    {
        var path = Path.Combine(directory, fileName);
        Directory.CreateDirectory(directory);
        var root = new Dictionary<string, object>();
        foreach (var book in verses.GroupBy(v => v.Book).OrderBy(g => g.Key))
        {
            var name = book.Key == 40 ? "S. Mateo" : CanonicalBooks.Get(book.Key).Name;   // el formato real usa nombres irregulares
            root[name] = book.GroupBy(v => v.Chapter).ToDictionary(c => c.Key.ToString(), c => c.ToDictionary(v => v.Verse.ToString(), v => v.Text));
        }
        root["lang"] = "xx";
        File.WriteAllText(path, JsonSerializer.Serialize(root), new UTF8Encoding(false));
        return path;
    }
}

/// <summary>Una BD temporal ya inicializada con el importador, las consultas y el verificador listos.</summary>
internal sealed class BibleEnv : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly string _files = Path.Combine(Path.GetTempPath(), "oak-proyektor-tests", "files-" + Guid.NewGuid().ToString("N"));

    private BibleQueryOptions? _queryOptions;

    private BibleEnv() { }

    /// <summary>Vuelve a armar <see cref="Query"/> con un anotador de historial (p. ej. el de segundo plano, H5).</summary>
    public void UseRecorder(ICitationRecorder recorder) => Query = new BibleQueryService(_db.Connections, Readiness, _queryOptions, recorder);

    public DatabaseReadiness Readiness { get; } = new(NullLogger<DatabaseReadiness>.Instance);
    public ISqliteConnectionFactory Connections => _db.Connections;
    public BibleImporter Importer { get; private set; } = null!;
    public BibleQueryService Query { get; private set; } = null!;
    public BibleCrossChecker CrossChecker { get; private set; } = null!;
    public string FilesDirectory => _files;
    public DatabaseOptions DatabaseOptions => _db.Options;
    public string DataDirectory => Path.GetDirectoryName(_db.Options.DatabasePath)!;
    /// <summary>Donde las reimportaciones dejan su copia previa (la misma carpeta de respaldos de la BD).</summary>
    public string BackupsDirectory => _db.Options.BackupDirectory;

    public static async Task<BibleEnv> CreateAsync(BibleQueryOptions? options = null)
    {
        var env = new BibleEnv();
        await env.Readiness.StartAsync(env._db.Initializer, CancellationToken.None);
        env.Importer = new BibleImporter(env._db.Connections, env.Readiness, NullLogger<BibleImporter>.Instance);
        env._queryOptions = options;
        env.Query = new BibleQueryService(env._db.Connections, env.Readiness, options);
        env.CrossChecker = new BibleCrossChecker(env._db.Connections, env.Readiness);
        return env;
    }

    public Task<Microsoft.Data.Sqlite.SqliteConnection> OpenAsync() => _db.OpenAsync();

    /// <summary>Importa <paramref name="verses"/> con la abreviatura dada (escribe el archivo sintético y lo importa).</summary>
    public Task<ImportReport> ImportAsync(IEnumerable<SyntheticBible.V> verses, string abbreviation = "SIN", string? profile = null,
        string? attribution = null, bool allowLargeDeletion = false, IProgress<ImportProgress>? progress = null, string name = "Biblia sintética", bool allowLargeChange = false, CancellationToken cancellationToken = default)
    {
        var path = SyntheticBible.WriteYouVersion(_files, verses, fileName: $"{abbreviation.Trim()}-{Guid.NewGuid():N}.json");
        return ImportFileAsync(path, abbreviation, profile, attribution, allowLargeDeletion, progress, name, allowLargeChange, cancellationToken);
    }

    /// <summary>Importa un archivo YA escrito (p. ej. uno mal armado a propósito).</summary>
    public Task<ImportReport> ImportFileAsync(string path, string abbreviation = "SIN", string? profile = null, string? attribution = null, bool allowLargeDeletion = false,
        IProgress<ImportProgress>? progress = null, string name = "Biblia sintética", bool allowLargeChange = false, CancellationToken cancellationToken = default) =>
        Importer.ImportAsync(new BibleImportRequest(path, abbreviation, name, "es", null, profile, attribution, allowLargeDeletion, allowLargeChange, BackupsDirectory), progress, cancellationToken);

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_files, recursive: true); } catch (Exception) { /* limpieza best-effort */ }
    }
}

/// <summary>Un <see cref="IProgress{T}"/> síncrono que guarda lo recibido.</summary>
internal sealed class RecordingProgress : IProgress<ImportProgress>
{
    public List<ImportProgress> Reports { get; } = [];
    public void Report(ImportProgress value) => Reports.Add(value);
}

/// <summary>Un <see cref="IProgress{T}"/> síncrono que ejecuta una acción con cada informe (p. ej. cancelar a mitad de la escritura).</summary>
internal sealed class ActionProgress(Action<ImportProgress> action) : IProgress<ImportProgress>
{
    public void Report(ImportProgress value) => action(value);
}

/// <summary>Registro en memoria de TODO lo que se loguea (mensaje formateado y la cadena de mensajes de cada excepción), para comprobar que no se filtra texto.</summary>
internal sealed class CapturingLogger : Microsoft.Extensions.Logging.ILoggerProvider
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _lines = new();
    public IReadOnlyList<string> Lines => [.. _lines];
    public string All => string.Join("\n", _lines);

    public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new L(_lines);
    public void Dispose() { }

    public Microsoft.Extensions.Logging.ILoggerFactory Factory() => new F(this);

    private sealed class F(CapturingLogger owner) : Microsoft.Extensions.Logging.ILoggerFactory
    {
        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => owner.CreateLogger(categoryName);
        public void AddProvider(Microsoft.Extensions.Logging.ILoggerProvider provider) { }
        public void Dispose() { }
    }

    private sealed class L(System.Collections.Concurrent.ConcurrentQueue<string> lines) : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lines.Enqueue(formatter(state, exception));
            for (var e = exception; e is not null; e = e.InnerException) lines.Enqueue(e.GetType().Name + ": " + e.Message + "\n" + e.StackTrace);
        }
    }
}
