using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OakProyektor.Core.Scripture;

/// <summary>Un versículo tal como lo entrega una fuente: libro CANÓNICO (1 a 66), capítulo, número y texto sin limpiar.</summary>
public sealed record SourceVerse(int BookNumber, int Chapter, int VerseNumber, string Text);

/// <summary>Lo que lee una fuente. <c>Notes</c> son avisos de la lectura (nunca llevan texto bíblico, solo códigos y conteos).</summary>
/// <param name="JoinedVerses">Versículos que la fuente trae en varios trozos (líneas o items) y que se unieron con un espacio.</param>
/// <param name="DuplicateChaptersDiscarded">Capítulos repetidos en la fuente que se descartaron (se conserva la primera aparición).</param>
public sealed record BibleSourceData(IReadOnlyList<SourceVerse> Verses, int JoinedVerses, int DuplicateChaptersDiscarded, IReadOnlyList<string> Notes);

/// <summary>
/// Un formato de archivo de Biblia. Para añadir otro formato (Zefania XML, otro JSON…) se escribe una clase que implemente esta interfaz y se añade a
/// <see cref="BibleSourceFormats.All"/>: el importador, el validador y la verificación cruzada no cambian.
/// </summary>
public interface IBibleSourceFormat
{
    /// <summary>Identificador estable (el que se pasa a <c>--format</c>).</summary>
    string Id { get; }
    string Description { get; }
    /// <summary>true = se puede IMPORTAR a la BD; false = solo sirve para comparar (verificación cruzada).</summary>
    bool CanImport { get; }
    Task<BibleSourceData> ReadAsync(string path, CancellationToken cancellationToken);
}

public static class BibleSourceFormats
{
    public static IReadOnlyList<IBibleSourceFormat> All { get; } = [new YouVersionJsonSource(), new NamedBooksJsonSource()];

    /// <summary>El formato que se usa para importar si no se indica otro: el único aprobado como fuente.</summary>
    public static IBibleSourceFormat Default => All[0];

    public static IBibleSourceFormat? Find(string? id) =>
        All.FirstOrDefault(f => string.Equals(f.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Limpieza MÍNIMA del texto de un versículo (acordada): normalización Unicode NFC, espacios en blanco (incluidos no separables y saltos de línea)
/// colapsados a un solo espacio, recorte de los extremos y eliminación de caracteres de control. NO toca la puntuación, las tildes ni las mayúsculas.
/// </summary>
public static class VerseCleaner
{
    public static string Clean(string raw)
    {
        var normalized = raw.Normalize(NormalizationForm.FormC);
        var sb = new StringBuilder(normalized.Length);
        var pendingSpace = false;
        foreach (var ch in normalized)
        {
            if (char.IsWhiteSpace(ch)) { pendingSpace = sb.Length > 0; continue; }
            if (char.IsControl(ch)) continue;
            if (pendingSpace) { sb.Append(' '); pendingSpace = false; }
            sb.Append(ch);
        }
        return sb.ToString();
    }
}

/// <summary>
/// El formato APROBADO como fuente de importación: JSON con <c>books[].usfm</c>, <c>chapters[].usfm</c> («GEN.1») y <c>items[]</c>; solo cuentan los items de
/// tipo <c>verse</c> (<c>verse_numbers</c> y <c>lines</c>). Todo lo demás (<c>html</c>, títulos, rótulos, notas) se descarta sin leerlo a memoria.
/// Detalles que el formato obliga a manejar (ver docs/FORMATO_BIBLIA.md): un libro de un solo capítulo repite el capítulo; un versículo puede venir en
/// varios items o en varias líneas.
/// </summary>
public sealed class YouVersionJsonSource : IBibleSourceFormat
{
    public string Id => "youversion-json";
    public string Description => "JSON por capítulos con items de tipo «verse» (libros por código USFM).";
    public bool CanImport => true;

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = false, NumberHandling = JsonNumberHandling.Strict };

    public async Task<BibleSourceData> ReadAsync(string path, CancellationToken cancellationToken)
    {
        Root? root;
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            root = await JsonSerializer.DeserializeAsync<Root>(stream, Options, cancellationToken).ConfigureAwait(false);   // las propiedades no declaradas (html…) se saltan
        if (root?.Books is null) throw new InvalidDataException("El archivo no tiene la lista «books»: no es el formato esperado.");

        var pieces = new Dictionary<(int, int, int), List<string>>();
        var notes = new List<string>();
        var duplicateChapters = 0;
        var multiNumberItems = 0;

        foreach (var book in root.Books)
        {
            var canonical = CanonicalBooks.FindByUsfm(book.Usfm);
            if (canonical is null) { notes.Add($"Libro con código desconocido «{Safe(book.Usfm)}»: se ignora."); continue; }

            var seenChapters = new HashSet<int>();
            foreach (var chapter in book.Chapters ?? [])
            {
                if (!TryChapterNumber(chapter.Usfm, out var chapterNumber)) { notes.Add($"{canonical.Usfm}: capítulo con código «{Safe(chapter.Usfm)}» ilegible; se ignora."); continue; }
                if (!seenChapters.Add(chapterNumber)) { duplicateChapters++; continue; }

                foreach (var item in chapter.Items ?? [])
                {
                    if (item.Type != "verse" || item.VerseNumbers is not { Count: > 0 }) continue;
                    if (item.VerseNumbers.Count > 1) multiNumberItems++;
                    var key = (canonical.Number, chapterNumber, item.VerseNumbers[0]);
                    if (!pieces.TryGetValue(key, out var list)) pieces[key] = list = [];
                    foreach (var line in item.Lines ?? []) if (line is not null) list.Add(line);
                }
            }
        }

        if (multiNumberItems > 0) notes.Add($"{multiNumberItems} item(s) con varios números de versículo: el texto se asignó al primero.");

        var verses = pieces
            .OrderBy(p => p.Key.Item1).ThenBy(p => p.Key.Item2).ThenBy(p => p.Key.Item3)
            .Select(p => new SourceVerse(p.Key.Item1, p.Key.Item2, p.Key.Item3, string.Join(' ', p.Value)))
            .ToList();
        var joined = pieces.Count(p => p.Value.Count > 1);
        return new BibleSourceData(verses, joined, duplicateChapters, notes);
    }

    private static bool TryChapterNumber(string? usfm, out int chapter)
    {
        chapter = 0;
        var dot = usfm?.LastIndexOf('.') ?? -1;
        return dot >= 0 && int.TryParse(usfm![(dot + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out chapter) && chapter >= 1;
    }

    private static string Safe(string? code) => code is null ? "(vacío)" : code.Length <= 15 ? code : code[..15];

    private sealed class Root { [JsonPropertyName("books")] public List<BookDto>? Books { get; set; } }
    private sealed class BookDto
    {
        [JsonPropertyName("usfm")] public string? Usfm { get; set; }
        [JsonPropertyName("chapters")] public List<ChapterDto>? Chapters { get; set; }
    }
    private sealed class ChapterDto
    {
        [JsonPropertyName("usfm")] public string? Usfm { get; set; }
        [JsonPropertyName("items")] public List<ItemDto>? Items { get; set; }
    }
    private sealed class ItemDto
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("verse_numbers")] public List<int>? VerseNumbers { get; set; }
        [JsonPropertyName("lines")] public List<string?>? Lines { get; set; }
    }
}

/// <summary>
/// SOLO PARA VERIFICAR (no se importa): JSON <c>{ "Nombre del libro": { "capítulo": { "versículo": "texto" } } }</c> más claves sueltas (p. ej. <c>lang</c>) que se
/// ignoran. Los libros se identifican por nombre con <see cref="BookNames"/>.
/// </summary>
public sealed class NamedBooksJsonSource : IBibleSourceFormat
{
    public string Id => "named-books-json";
    public string Description => "JSON { libro: { capítulo: { versículo: texto } } } (solo verificación cruzada).";
    public bool CanImport => false;

    public async Task<BibleSourceData> ReadAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("El archivo no es un objeto JSON: no es el formato esperado.");

        var verses = new List<SourceVerse>();
        var notes = new List<string>();
        foreach (var bookProperty in document.RootElement.EnumerateObject())
        {
            if (bookProperty.Value.ValueKind != JsonValueKind.Object) continue;   // «lang» y similares
            var match = BookNames.Resolve(bookProperty.Name);
            if (match.Kind is not (BookMatchKind.Exact or BookMatchKind.UniquePrefix)) { notes.Add($"Libro «{Safe(bookProperty.Name)}» no reconocido: se ignora."); continue; }

            foreach (var chapterProperty in bookProperty.Value.EnumerateObject())
            {
                if (!int.TryParse(chapterProperty.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var chapter) || chapterProperty.Value.ValueKind != JsonValueKind.Object) continue;
                foreach (var verseProperty in chapterProperty.Value.EnumerateObject())
                {
                    if (!int.TryParse(verseProperty.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var verse) || verseProperty.Value.ValueKind != JsonValueKind.String) continue;
                    verses.Add(new SourceVerse(match.BookNumber!.Value, chapter, verse, verseProperty.Value.GetString() ?? ""));
                }
            }
        }
        verses.Sort(static (a, b) => (a.BookNumber, a.Chapter, a.VerseNumber).CompareTo((b.BookNumber, b.Chapter, b.VerseNumber)));
        return new BibleSourceData(verses, 0, 0, notes);
    }

    private static string Safe(string code) => code.Length <= 15 ? code : code[..15];
}
