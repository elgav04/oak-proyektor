using System.Globalization;
using System.Text;

namespace OakProyektor.Core.Scripture;

/// <summary>Un nombre o abreviatura de un libro: <c>Key</c> es la forma normalizada (única en todo el canon); <c>Alias</c>, cómo se escribe normalmente.</summary>
public sealed record BookAlias(int BookNumber, string Alias, string Key);

/// <summary>Un libro propuesto al autocompletar. <c>MatchedAlias</c> es el nombre o abreviatura que coincidió con lo escrito.</summary>
public sealed record BookSuggestion(int BookNumber, string Name, string ShortName, string MatchedAlias);

public enum BookMatchKind { Exact, UniquePrefix, Ambiguous, NotFound }

/// <summary>Resultado de identificar un libro en un texto. <c>Candidates</c> son los números de libro posibles si es ambiguo.</summary>
public sealed record BookMatch(BookMatchKind Kind, int? BookNumber, IReadOnlyList<int> Candidates);

/// <summary>
/// Nombres y abreviaturas de los libros en español, insensibles a tildes, mayúsculas, puntos y espacios (<c>Gn</c>, <c>Gén.</c>, <c>génesis</c>;
/// <c>1 Co</c>, <c>1Co</c>, <c>I Corintios</c>, <c>Primera de Corintios</c>). Es la ÚNICA fuente de los alias: la migración 5 siembra la tabla
/// <c>BookAliases</c> con exactamente esta lista (un test compara ambas) y el parser de referencias la usa sin tocar la BD.
/// </summary>
public static class BookNames
{
    /// <summary>
    /// Alias por libro, en el orden canónico (índice 0 = Génesis). Solo se escriben las formas en números arabigos: «I Corintios», «Primera de Corintios»,
    /// «1ra Corintios»… se reducen a «1 Corintios» al normalizar (<see cref="NormalizeKey"/>). Una misma clave en dos libros distintos es un error (lo vigila un test).
    /// </summary>
    private static readonly string[][] AliasesByBook =
    [
        ["Gn", "Gen", "Gén", "Génesis"],
        ["Éx", "Ex", "Exo", "Éxodo"],
        ["Lv", "Lev", "Levítico"],
        ["Nm", "Núm", "Num", "Números"],
        ["Dt", "Deut", "Deuteronomio"],
        ["Jos", "Josué"],
        ["Jue", "Jc", "Jueces"],
        ["Rt", "Rut"],
        ["1 S", "1 Sam", "1 Samuel"],
        ["2 S", "2 Sam", "2 Samuel"],
        ["1 R", "1 Re", "1 Rey", "1 Reyes"],
        ["2 R", "2 Re", "2 Rey", "2 Reyes"],
        ["1 Cr", "1 Crón", "1 Crónicas"],
        ["2 Cr", "2 Crón", "2 Crónicas"],
        ["Esd", "Esdras"],
        ["Neh", "Ne", "Nehemías"],
        ["Est", "Ester"],
        ["Job", "Jb"],
        ["Sal", "Sl", "Salmo", "Salmos"],
        ["Pr", "Prov", "Proverbios"],
        ["Ec", "Ecl", "Eclesiastés"],
        ["Cnt", "Cant", "Cantar", "Cantares", "Cantar de los Cantares"],
        ["Is", "Isa", "Isaías"],
        ["Jer", "Jr", "Jeremías"],
        ["Lm", "Lam", "Lamentaciones"],
        ["Ez", "Ezq", "Ezeq", "Ezequiel"],
        ["Dn", "Dan", "Daniel"],
        ["Os", "Oseas"],
        ["Jl", "Joel"],
        ["Am", "Amós"],
        ["Abd", "Abdías"],
        ["Jon", "Jonás"],
        ["Miq", "Mi", "Miqueas"],
        ["Nah", "Na", "Nahúm"],
        ["Hab", "Habacuc"],
        ["Sof", "Sofonías"],
        ["Hag", "Hageo"],
        ["Zac", "Zacarías"],
        ["Mal", "Malaquías"],
        ["Mt", "Mat", "Mateo", "S. Mateo"],
        ["Mr", "Mc", "Mar", "Marcos", "S. Marcos"],
        ["Lc", "Luc", "Lucas", "S. Lucas"],
        ["Jn", "Juan", "S. Juan"],
        ["Hch", "Hech", "Hechos", "Hechos de los Apóstoles"],
        ["Ro", "Rom", "Romanos"],
        ["1 Co", "1 Cor", "1 Corintios"],
        ["2 Co", "2 Cor", "2 Corintios"],
        ["Gá", "Gal", "Gálatas"],
        ["Ef", "Efe", "Efesios"],
        ["Fil", "Flp", "Filipenses"],
        ["Col", "Colosenses"],
        ["1 Ts", "1 Tes", "1 Tesalonicenses"],
        ["2 Ts", "2 Tes", "2 Tesalonicenses"],
        ["1 Ti", "1 Tim", "1 Timoteo"],
        ["2 Ti", "2 Tim", "2 Timoteo"],
        ["Tit", "Tito"],
        ["Flm", "Filem", "Filemón"],
        ["Heb", "Hebreos"],
        ["Stg", "Sant", "Santiago"],
        ["1 P", "1 Pe", "1 Ped", "1 Pedro"],
        ["2 P", "2 Pe", "2 Ped", "2 Pedro"],
        ["1 Jn", "1 Juan"],
        ["2 Jn", "2 Juan"],
        ["3 Jn", "3 Juan"],
        ["Jud", "Judas"],
        ["Ap", "Apoc", "Apocalipsis"],
    ];

    private static readonly Dictionary<string, string> Ordinals = new(StringComparer.Ordinal)
    {
        ["primera"] = "1", ["primero"] = "1", ["1ra"] = "1", ["1ro"] = "1", ["1er"] = "1", ["i"] = "1",
        ["segunda"] = "2", ["segundo"] = "2", ["2da"] = "2", ["2do"] = "2", ["ii"] = "2",
        ["tercera"] = "3", ["tercero"] = "3", ["3ra"] = "3", ["3ro"] = "3", ["3er"] = "3", ["iii"] = "3",
    };

    /// <summary>Todos los alias (sin duplicar claves), en orden de libro. Es lo que se siembra en la tabla <c>BookAliases</c>.</summary>
    public static IReadOnlyList<BookAlias> All { get; } = BuildAll();

    private static readonly Dictionary<string, int> ByKey = All.ToDictionary(a => a.Key, a => a.BookNumber, StringComparer.Ordinal);

    /// <summary>
    /// Clave de comparación: minúsculas, sin tildes, sin puntos ni espacios; «I», «Primera (de)», «1ra»… al inicio pasan a «1» (y análogos para 2 y 3).
    /// «Gén.» → <c>gen</c>; «I Corintios» → <c>1corintios</c>; «Primera de Corintios» → <c>1corintios</c>; «S. Juan» → <c>sjuan</c>.
    /// </summary>
    public static string NormalizeKey(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var decomposed = text.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;   // tildes y diéresis (ñ → n: no hay libros que dependan de ella)
            sb.Append(ch is '.' or 'º' or 'ª' or '°' or '\u00A0' ? ' ' : ch);
        }

        var tokens = sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (tokens.Count >= 2 && Ordinals.TryGetValue(tokens[0], out var digit))
        {
            tokens[0] = digit;
            if (tokens.Count >= 3 && tokens[1] is "de" or "del") tokens.RemoveAt(1);   // «Primera de Corintios»
        }
        return string.Concat(tokens);
    }

    /// <summary>Número de libro de un nombre o abreviatura exactos (tras normalizar), o null.</summary>
    public static int? FindExact(string? text) => ByKey.TryGetValue(NormalizeKey(text), out var n) ? n : null;

    /// <summary>
    /// Identifica un libro: coincidencia exacta de alias; si no, un prefijo (de al menos 2 caracteres) que pertenezca a UN SOLO libro («Gene» → Génesis);
    /// si el prefijo pertenece a varios, es ambiguo («Jo» → Job, Joel, Jonás, Josué); si no coincide con nada, no existe.
    /// </summary>
    public static BookMatch Resolve(string? text)
    {
        var key = NormalizeKey(text);
        if (key.Length == 0) return new BookMatch(BookMatchKind.NotFound, null, []);
        if (ByKey.TryGetValue(key, out var exact)) return new BookMatch(BookMatchKind.Exact, exact, [exact]);

        var candidates = BooksWithPrefix(key);
        if (candidates.Count == 0) return new BookMatch(BookMatchKind.NotFound, null, []);
        if (candidates.Count == 1 && key.Length >= 2) return new BookMatch(BookMatchKind.UniquePrefix, candidates[0], candidates);
        return new BookMatch(BookMatchKind.Ambiguous, null, candidates);
    }

    /// <summary>
    /// Sugerencias para autocompletar: libros cuyo nombre o abreviatura EMPIEZA por lo escrito, sin repetir libro, primero los que coinciden
    /// exactamente y luego por orden canónico. Si el texto ya incluye un capítulo (un dígito después del nombre), no hay sugerencias de libro.
    /// </summary>
    public static IReadOnlyList<BookSuggestion> Suggest(string? prefix, int limit = 8)
    {
        if (limit <= 0 || string.IsNullOrWhiteSpace(prefix)) return [];
        var (book, rest) = ReferenceParser.SplitBookAndRest(prefix);
        // «1» (o «2», «3») a secas es el comienzo de «1 Corintios»…, no un capítulo.
        if (book.Length == 0 && rest is "1" or "2" or "3") { book = rest; rest = ""; }
        if (rest.Length > 0) return [];

        var key = NormalizeKey(book);
        if (key.Length == 0) return [];

        var result = new List<BookSuggestion>();
        var seen = new HashSet<int>();
        // Primero la coincidencia exacta (si «Jn» es un alias, Juan va antes que 1 Juan), luego el resto por orden de libro.
        if (ByKey.TryGetValue(key, out var exact))
        {
            var alias = All.First(a => a.Key == key);
            var b = CanonicalBooks.Get(exact);
            result.Add(new BookSuggestion(exact, b.Name, b.ShortName, alias.Alias));
            seen.Add(exact);
        }
        foreach (var alias in All)
        {
            if (result.Count >= limit) break;
            if (!alias.Key.StartsWith(key, StringComparison.Ordinal) || !seen.Add(alias.BookNumber)) continue;
            var b = CanonicalBooks.Get(alias.BookNumber);
            result.Add(new BookSuggestion(alias.BookNumber, b.Name, b.ShortName, alias.Alias));
        }
        return result.Count > limit ? result[..limit] : result;
    }

    private static List<int> BooksWithPrefix(string key) =>
        [.. All.Where(a => a.Key.StartsWith(key, StringComparison.Ordinal)).Select(a => a.BookNumber).Distinct().Order()];

    private static IReadOnlyList<BookAlias> BuildAll()
    {
        if (AliasesByBook.Length != CanonicalBooks.Count)
            throw new InvalidOperationException($"La lista de alias tiene {AliasesByBook.Length} libros; debe tener {CanonicalBooks.Count}.");

        var list = new List<BookAlias>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < AliasesByBook.Length; i++)
        {
            var number = i + 1;
            // El nombre completo canónico y la abreviatura canónica siempre son alias del libro (aunque no estén en la lista).
            var forms = AliasesByBook[i].Concat([CanonicalBooks.Get(number).Name, CanonicalBooks.Get(number).ShortName]);
            foreach (var alias in forms)
            {
                var key = NormalizeKey(alias);
                if (seen.TryGetValue(key, out var owner))
                {
                    if (owner != number) throw new InvalidOperationException($"La clave de alias «{key}» pertenece a los libros {owner} y {number}.");
                    continue;   // misma clave en el mismo libro («Éx» y «Ex»): se guarda la primera forma
                }
                seen[key] = number;
                list.Add(new BookAlias(number, alias, key));
            }
        }
        return list;
    }
}
