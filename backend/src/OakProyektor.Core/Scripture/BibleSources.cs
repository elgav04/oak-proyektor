using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OakProyektor.Core.Scripture;

/// <summary>Un versículo tal como lo entrega una fuente: libro CANÓNICO (1 a 66), capítulo, número y texto sin limpiar.</summary>
public sealed record SourceVerse(int BookNumber, int Chapter, int VerseNumber, string Text);

/// <summary>Lo que lee una fuente. <c>Notes</c> son avisos de la lectura (nunca llevan texto bíblico, solo códigos y conteos).</summary>
/// <param name="JoinedVerses">Versículos que la fuente trae en varios trozos (líneas o items) y que se unieron con un espacio.</param>
/// <param name="DuplicateChaptersDiscarded">Capítulos repetidos en la fuente (mismo libro y capítulo en CUALQUIER parte del archivo) que se descartaron (se conserva la primera aparición).</param>
/// <param name="DuplicateChaptersDifferent">De esos, cuántos tenían un texto DISTINTO del de la primera aparición (señal de un archivo mal armado: dos exportaciones mezcladas).</param>
public sealed record BibleSourceData(IReadOnlyList<SourceVerse> Verses, int JoinedVerses, int DuplicateChaptersDiscarded, IReadOnlyList<string> Notes, int DuplicateChaptersDifferent = 0);

/// <summary>
/// Comprobación de codificación de una fuente de texto. Un archivo en Windows-1252 o Latin-1 (o UTF-16) NO es UTF-8: el lector JSON lanzaría una excepción poco clara
/// al transcodificar. Se valida ANTES de leer, en bloques (sin cargar el archivo entero), para dar un mensaje claro y no escribir nada en la base de datos.
/// </summary>
public static class SourceEncoding
{
    public const string NotUtf8Message = "El archivo no está en UTF-8 (probablemente Windows-1252, Latin-1 o UTF-16): vuelve a guardarlo como UTF-8 y repite la importación.";

    public static async Task EnsureUtf8Async(string path, CancellationToken cancellationToken)
    {
        var decoder = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetDecoder();
        var bytes = new byte[81920];
        var chars = new char[bytes.Length + 4];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bytes.Length, useAsync: true);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(bytes, cancellationToken).ConfigureAwait(false)) > 0)
                decoder.GetChars(bytes, 0, read, chars, 0, flush: false);
            decoder.GetChars([], 0, 0, chars, 0, flush: true);   // una secuencia truncada al final del archivo también es inválida
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidDataException(NotUtf8Message);
        }
    }
}

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
    /// <summary>¿Ya está limpio? (NFC, sin caracteres de control, solo espacios simples entre palabras y ninguno en los extremos.) Entonces `Clean` devuelve la MISMA cadena: no duplica el texto en memoria.</summary>
    private static bool IsAlreadyClean(string raw)
    {
        if (raw.Length == 0) return true;
        var previousSpace = true;   // el primer carácter no puede ser un espacio
        foreach (var ch in raw)
        {
            if (char.IsControl(ch)) return false;
            if (char.IsWhiteSpace(ch))
            {
                if (ch != ' ' || previousSpace) return false;
                previousSpace = true;
            }
            else previousSpace = false;
        }
        return !previousSpace && raw.IsNormalized(NormalizationForm.FormC);
    }

    public static string Clean(string raw)
    {
        if (IsAlreadyClean(raw)) return raw;
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
        await SourceEncoding.EnsureUtf8Async(path, cancellationToken).ConfigureAwait(false);
        Root? root;
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            root = await JsonSerializer.DeserializeAsync<Root>(stream, Options, cancellationToken).ConfigureAwait(false);   // las propiedades no declaradas (html…) se saltan
        if (root?.Books is null) throw new InvalidDataException("El archivo no tiene la lista «books»: no es el formato esperado.");

        var pieces = new Dictionary<(int, int, int), List<string>>();
        var notes = new List<string>();
        // Capítulos ya leídos en TODO el archivo (no solo dentro de un libro: un libro repetido o dos exportaciones concatenadas repiten capítulos) y la firma de su contenido,
        // para saber si el repetido es idéntico (lo normal: los libros de un solo capítulo lo repiten) o DISTINTO (señal de un archivo mal armado).
        var seenChapters = new Dictionary<(int Book, int Chapter), string>();
        var differentRefs = new List<string>();
        var duplicateChapters = 0;
        var multiNumberItems = 0;

        foreach (var book in root.Books)
        {
            var canonical = CanonicalBooks.FindByUsfm(book.Usfm);
            if (canonical is null) { notes.Add($"Libro con código desconocido «{Safe(book.Usfm)}»: se ignora."); continue; }

            foreach (var chapter in book.Chapters ?? [])
            {
                if (!TryChapterNumber(chapter.Usfm, out var chapterNumber)) { notes.Add($"{canonical.Usfm}: capítulo con código «{Safe(chapter.Usfm)}» ilegible; se ignora."); continue; }

                // Los versículos de ESTE capítulo (un versículo puede venir en varios items o líneas).
                var local = new SortedDictionary<int, List<string>>();
                var multiInChapter = 0;
                foreach (var item in chapter.Items ?? [])
                {
                    if (item.Type != "verse" || item.VerseNumbers is not { Count: > 0 }) continue;
                    if (item.VerseNumbers.Count > 1) multiInChapter++;
                    if (!local.TryGetValue(item.VerseNumbers[0], out var list)) local[item.VerseNumbers[0]] = list = [];
                    foreach (var line in item.Lines ?? []) if (line is not null) list.Add(line);
                }

                var signature = Signature(local);
                if (!seenChapters.TryAdd((canonical.Number, chapterNumber), signature))
                {
                    duplicateChapters++;
                    if (seenChapters[(canonical.Number, chapterNumber)] != signature) differentRefs.Add($"{canonical.Usfm}.{chapterNumber.ToString(CultureInfo.InvariantCulture)}");
                    continue;   // se conserva la primera aparición
                }
                multiNumberItems += multiInChapter;
                foreach (var (verse, lines) in local)
                {
                    var key = (canonical.Number, chapterNumber, verse);
                    if (!pieces.TryGetValue(key, out var list)) pieces[key] = list = [];
                    list.AddRange(lines);
                }
            }
        }

        if (multiNumberItems > 0) notes.Add($"{multiNumberItems} item(s) con varios números de versículo: el texto se asignó al primero.");
        if (differentRefs.Count > 0)
            notes.Add($"ATENCIÓN: {differentRefs.Count} capítulo(s) repetido(s) en el archivo tenían un texto DISTINTO del de su primera aparición (se conservó la primera; ¿dos exportaciones mezcladas?): " +
                      string.Join(", ", differentRefs.Take(10)) + (differentRefs.Count > 10 ? ", …" : "") + ".");

        var verses = pieces
            .OrderBy(p => p.Key.Item1).ThenBy(p => p.Key.Item2).ThenBy(p => p.Key.Item3)
            .Select(p => new SourceVerse(p.Key.Item1, p.Key.Item2, p.Key.Item3, p.Value.Count == 1 ? p.Value[0] : string.Join(' ', p.Value)))
            .ToList();
        var joined = pieces.Count(p => p.Value.Count > 1);
        return new BibleSourceData(verses, joined, duplicateChapters, notes, differentRefs.Count);
    }

    /// <summary>Huella exacta (SHA-256) del contenido de un capítulo: números de versículo y líneas, en orden de versículo.</summary>
    private static string Signature(SortedDictionary<int, List<string>> chapter)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (verse, lines) in chapter)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(verse.ToString(CultureInfo.InvariantCulture)));
            hash.AppendData([0]);
            foreach (var line in lines) { hash.AppendData(Encoding.UTF8.GetBytes(line)); hash.AppendData([1]); }
            hash.AppendData([2]);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
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
        await SourceEncoding.EnsureUtf8Async(path, cancellationToken).ConfigureAwait(false);
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
