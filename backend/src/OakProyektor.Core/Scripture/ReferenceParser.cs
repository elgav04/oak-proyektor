using System.Globalization;
using System.Text;

namespace OakProyektor.Core.Scripture;

/// <summary>
/// Un tramo de una referencia. Capítulo completo: <c>StartVerse</c> y <c>EndVerse</c> nulos. Un versículo: inicio = fin. Puede cruzar capítulos
/// (<c>Gn 1:1-2:3</c>). Los números son los canónicos; que el versículo exista en una Biblia concreta se comprueba al consultarla.
/// </summary>
public sealed record ReferenceSegment(int StartChapter, int? StartVerse, int EndChapter, int? EndVerse)
{
    public bool IsWholeChapters => StartVerse is null;
}

/// <summary>Una referencia ya interpretada: un libro (1 a 66) y uno o más tramos, en el orden en que se escribieron.</summary>
public sealed record ParsedReference(int BookNumber, IReadOnlyList<ReferenceSegment> Segments)
{
    /// <summary>Forma canónica para mostrar: «Jn 3:16», «Sal 23», «Sal 23:1-6», «Mt 5:3,5,7», «Gn 1:1-2:3».</summary>
    public string Format() => ReferenceFormatter.Format(this);
}

public enum ReferenceErrorCode
{
    Empty,
    MissingBook,
    BookNotFound,
    BookAmbiguous,
    MissingChapter,
    InvalidNumber,
    ChapterOutOfRange,
    InvalidRange,
    UnexpectedText,
    IncompleteReference,
    /// <summary>Más tramos que <see cref="ReferenceParser.MaxSegments"/>: una referencia así no es una cita, y cada tramo es una consulta a la BD.</summary>
    TooManySegments,
}

/// <summary>Por qué no se pudo interpretar. <c>Message</c> está en español y es apto para el operador; nunca contiene texto bíblico.</summary>
/// <param name="Candidates">Libros posibles si el nombre es ambiguo (números canónicos).</param>
public sealed record ReferenceError(ReferenceErrorCode Code, string Message, IReadOnlyList<int> Candidates);

public sealed record ReferenceParseResult(ParsedReference? Reference, ReferenceError? Error)
{
    public bool Success => Reference is not null;
    public static ReferenceParseResult Ok(ParsedReference reference) => new(reference, null);
    public static ReferenceParseResult Fail(ReferenceErrorCode code, string message, IReadOnlyList<int>? candidates = null) =>
        new(null, new ReferenceError(code, message, candidates ?? []));
}

/// <summary>
/// Parser de referencias bíblicas en español. FUNCIÓN PURA: sin estado, sin BD, sin lanzar excepciones (todo fallo es un <see cref="ReferenceError"/>).
/// <list type="bullet">
/// <item>Libro: nombre o abreviatura (<see cref="BookNames"/>), con o sin espacio antes del capítulo: «Jn 3:16», «jn3:16», «1 co 13:4», «I Corintios 13».</item>
/// <item>Capítulo y versículo se separan con «:» o «.»: «Juan 3.16». Rangos con «-» (también «–», «—»): «Sal 23:1-6», «Gn 1:1-2:3», «Sal 23-25».</item>
/// <item>La coma enumera versículos del mismo capítulo tras un «cap:ver» («Mt 5:3,5,7») o capítulos si no hubo «:» («Sal 23,25»); el punto y coma abre
/// un grupo nuevo («Jn 3:16; 4:1»).</item>
/// <item>Solo capítulo («Sal 23») = capítulo completo. Un libro de UN solo capítulo (Abdías, Filemón, 2 y 3 Juan, Judas) usa los números sueltos como
/// versículos («Jud 14» = Judas 1:14) y, sin números, es el libro entero.</item>
/// <item>No existe un capítulo mayor que los del libro ni un versículo mayor que 176 (el Salmo 119); el límite por Biblia lo comprueba la consulta.</item>
/// </list>
/// </summary>
public static class ReferenceParser
{
    /// <summary>Máximo de tramos de una referencia («Mt 5:3,5,7» tiene 3). Cada tramo es una consulta a la BD en el carril en vivo: «Jn 3:99,99,99,…» no debe armar miles (H6).</summary>
    public const int MaxSegments = 50;

    public static ReferenceParseResult Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return ReferenceParseResult.Fail(ReferenceErrorCode.Empty, "Escribe una referencia, por ejemplo «Jn 3:16».");

        var text = input.Trim();
        var (bookText, rest) = SplitBookAndRest(text);
        if (bookText.Length == 0)
            return ReferenceParseResult.Fail(ReferenceErrorCode.MissingBook, $"Falta el libro en «{Shorten(text)}». Ejemplo: «Jn 3:16».");

        var match = BookNames.Resolve(bookText);
        switch (match.Kind)
        {
            case BookMatchKind.NotFound:
                return ReferenceParseResult.Fail(ReferenceErrorCode.BookNotFound, $"No existe el libro «{Shorten(bookText)}».");
            case BookMatchKind.Ambiguous:
                var names = string.Join(", ", match.Candidates.Take(6).Select(n => CanonicalBooks.Get(n).Name)) + (match.Candidates.Count > 6 ? "…" : "");
                return ReferenceParseResult.Fail(ReferenceErrorCode.BookAmbiguous, $"«{Shorten(bookText)}» es ambiguo: podría ser {names}.", match.Candidates);
        }

        var book = CanonicalBooks.Get(match.BookNumber!.Value);
        if (rest.Length == 0)
        {
            // Un libro de un solo capítulo, sin números, es el libro entero.
            return book.Chapters == 1
                ? ReferenceParseResult.Ok(new ParsedReference(book.Number, [new ReferenceSegment(1, null, 1, null)]))
                : ReferenceParseResult.Fail(ReferenceErrorCode.MissingChapter, $"Falta el capítulo de {book.Name}. Ejemplo: «{book.ShortName} 1».");
        }

        if (!Tokenize(rest, out var tokens, out var badChar))
            return ReferenceParseResult.Fail(ReferenceErrorCode.UnexpectedText, $"Carácter inesperado «{badChar}» en «{Shorten(text)}».");

        return new Scanner(book, tokens).Run();
    }

    /// <summary>
    /// Separa el nombre del libro del resto («cap:ver…»). Un 1, 2 o 3 inicial pertenece al libro si lo sigue una letra («1 Co», «1co», «3 Jn») o un
    /// indicador ordinal y luego una letra: «1° Juan», «1º», «1ª», «1.º», «1.ª», «1a Juan», «1o Juan» (las formas con «a»/«o» llevan un espacio antes del
    /// nombre, para no confundirlas con las letras del propio nombre). El libro devuelto queda normalizado a «1 Juan».
    /// Pública para que el autocompletado use exactamente la misma regla.
    /// </summary>
    public static (string Book, string Remainder) SplitBookAndRest(string input)
    {
        var s = input.TrimStart();
        var i = 0;
        string? ordinalBook = null;
        if (s.Length > 0 && s[0] is >= '1' and <= '3')
        {
            var j = 1;
            // Indicador ordinal opcional justo después del dígito.
            var marker = j;
            if (j < s.Length && s[j] == '.' && j + 1 < s.Length && s[j + 1] is 'ª' or 'º' or '°') marker = j + 2;      // «1.ª», «1.º»
            else if (j < s.Length && s[j] is 'ª' or 'º' or '°') marker = j + 1;                                            // «1ª», «1º», «1°»
            else if (j + 1 < s.Length && s[j] is 'a' or 'A' or 'o' or 'O' && char.IsWhiteSpace(s[j + 1])) marker = j + 1; // «1a Juan», «1o Juan»
            else if (j + 1 < s.Length && s[j] == '.' && char.IsWhiteSpace(s[j + 1])) marker = j + 1;                           // «1. Juan»
            j = marker;
            while (j < s.Length && char.IsWhiteSpace(s[j])) j++;
            if (j < s.Length && char.IsLetter(s[j]))
            {
                i = j;
                if (marker != 1)
                {
                    var end = j;
                    while (end < s.Length && !char.IsDigit(s[end])) end++;
                    ordinalBook = s[0] + " " + s[j..end].Trim();
                }
            }
        }
        while (i < s.Length && !char.IsDigit(s[i])) i++;
        return (ordinalBook ?? s[..i].Trim(), s[i..].Trim());
    }

    // ───────────────────────────── lexer ─────────────────────────────

    private enum T { Number, Colon, Dash, Comma, Semicolon }

    private readonly record struct Token(T Kind, int Value, int Position);

    private static bool Tokenize(string text, out List<Token> tokens, out char bad)
    {
        tokens = [];
        bad = '\0';
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c is >= '0' and <= '9')
            {
                var start = i;
                var value = 0L;
                while (i < text.Length && text[i] is >= '0' and <= '9')
                {
                    value = Math.Min(value * 10 + (text[i] - '0'), 1_000_000);   // tope: un número absurdo sigue siendo absurdo, no desborda
                    i++;
                }
                tokens.Add(new Token(T.Number, (int)value, start));
                continue;
            }
            switch (c)
            {
                case ':' or '.': tokens.Add(new Token(T.Colon, 0, i)); break;
                case '-' or '–' or '—' or '‑' or '−': tokens.Add(new Token(T.Dash, 0, i)); break;
                case ',': tokens.Add(new Token(T.Comma, 0, i)); break;
                case ';': tokens.Add(new Token(T.Semicolon, 0, i)); break;
                default: bad = c; return false;
            }
            i++;
        }
        return true;
    }

    // ───────────────────────────── gramática ─────────────────────────────

    private sealed class Scanner(CanonicalBook book, List<Token> tokens)
    {
        private int _pos;
        private readonly List<ReferenceSegment> _segments = [];
        // Capítulo actual si el grupo en curso es de versículos («Mt 5:3» y ahora «,5»); null si es de capítulos.
        private int? _verseChapter;

        private Token? Peek => _pos < tokens.Count ? tokens[_pos] : null;
        private bool Is(T kind) => Peek is { } t && t.Kind == kind;

        public ReferenceParseResult Run()
        {
            while (true)
            {
                if (ReadItem() is { } error) return error;
                if (Peek is null) break;
                var sep = tokens[_pos++];
                if (sep.Kind == T.Semicolon) _verseChapter = null;
                else if (sep.Kind != T.Comma)
                    return ReferenceParseResult.Fail(ReferenceErrorCode.UnexpectedText, "Se esperaba «,» o «;» entre los tramos de la referencia.");
                if (Peek is null)
                    return ReferenceParseResult.Fail(ReferenceErrorCode.IncompleteReference, "La referencia termina en un separador: falta un número.");
            }
            return ReferenceParseResult.Ok(new ParsedReference(book.Number, _segments));
        }

        private ReferenceParseResult? ReadItem()
        {
            if (!Is(T.Number)) return Unexpected("Se esperaba un número de capítulo o de versículo.");
            var first = tokens[_pos++];

            // «cap:ver» (con rango opcional, que puede cruzar de capítulo: «1:1-2:3»).
            if (Is(T.Colon))
            {
                _pos++;
                if (!Is(T.Number)) return Fail(ReferenceErrorCode.IncompleteReference, "Falta el número de versículo después de «:».");
                var verse = tokens[_pos++];
                return ReadVerseRange(chapter: first.Value, verse.Value);
            }

            // Libro de un solo capítulo: los números sueltos son versículos («Jud 14», «Jud 3-5», «Jud 3,5»).
            if (book.Chapters == 1)
                return ReadVerseRange(chapter: 1, first.Value, bare: true);

            // Dentro de un grupo de versículos («Mt 5:3,5»): el número es un versículo del mismo capítulo.
            if (_verseChapter is { } current) return ReadVerseRange(current, first.Value, bare: true);

            // Capítulo(s) completo(s): «Sal 23», «Sal 23-25».
            var startChapter = first.Value;
            if (CheckChapterError(startChapter) is { } chapterError) return chapterError;
            var endChapter = startChapter;
            if (Is(T.Dash))
            {
                _pos++;
                if (!Is(T.Number)) return Fail(ReferenceErrorCode.IncompleteReference, "Falta el número después del guion.");
                endChapter = tokens[_pos++].Value;
                if (Is(T.Colon)) return Fail(ReferenceErrorCode.UnexpectedText, "Un rango «cap-cap:ver» no es válido; escribe «cap:ver-cap:ver».");
                if (CheckChapterError(endChapter) is { } endError) return endError;
                if (endChapter < startChapter) return Fail(ReferenceErrorCode.InvalidRange, $"El rango de capítulos está al revés ({startChapter}-{endChapter}).");
            }
            return AddSegment(new ReferenceSegment(startChapter, null, endChapter, null));
        }

        private ReferenceParseResult? ReadVerseRange(int chapter, int startVerse, bool bare = false)
        {
            if (CheckChapterError(chapter) is { } chapterError) return chapterError;
            if (CheckVerseError(startVerse) is { } verseError) return verseError;

            var endChapter = chapter;
            var endVerse = startVerse;
            if (Is(T.Dash))
            {
                _pos++;
                if (!Is(T.Number)) return Fail(ReferenceErrorCode.IncompleteReference, "Falta el número después del guion.");
                var second = tokens[_pos++].Value;
                if (Is(T.Colon) && !bare)
                {
                    // «1:1-2:3»: el segundo número es un capítulo.
                    _pos++;
                    if (!Is(T.Number)) return Fail(ReferenceErrorCode.IncompleteReference, "Falta el número de versículo después de «:».");
                    endChapter = second;
                    endVerse = tokens[_pos++].Value;
                    if (CheckChapterError(endChapter) is { } endChapterError) return endChapterError;
                    if (CheckVerseError(endVerse) is { } endVerseError) return endVerseError;
                    if (endChapter <= chapter) return Fail(ReferenceErrorCode.InvalidRange, $"El rango cruza de capítulo al revés ({chapter}:{startVerse}-{endChapter}:{endVerse}).");
                }
                else
                {
                    endVerse = second;
                    if (CheckVerseError(endVerse) is { } endVerseError) return endVerseError;
                    if (endVerse < startVerse) return Fail(ReferenceErrorCode.InvalidRange, $"El rango de versículos está al revés ({startVerse}-{endVerse}).");
                }
            }
            _verseChapter = endChapter;   // las comas que sigan son versículos de este capítulo
            return AddSegment(new ReferenceSegment(chapter, startVerse, endChapter, endVerse));
        }

        private ReferenceParseResult? AddSegment(ReferenceSegment segment)
        {
            if (_segments.Count >= MaxSegments)
                return Fail(ReferenceErrorCode.TooManySegments, $"Demasiados tramos en la referencia (máximo {MaxSegments}). Acórtala o divídela en varias consultas.");
            _segments.Add(segment);
            return null;
        }

        private ReferenceParseResult? CheckChapterError(int chapter) =>
            chapter < 1 ? Fail(ReferenceErrorCode.InvalidNumber, "El capítulo debe ser 1 o mayor.")
            : chapter > book.Chapters ? Fail(ReferenceErrorCode.ChapterOutOfRange, $"{book.Name} tiene {book.Chapters} capítulo{(book.Chapters == 1 ? "" : "s")}; no existe el {chapter}.")
            : null;

        private static ReferenceParseResult? CheckVerseError(int verse) =>
            verse < 1 ? Fail(ReferenceErrorCode.InvalidNumber, "El versículo debe ser 1 o mayor.")
            : verse > CanonicalBooks.MaxVersesInAChapter ? Fail(ReferenceErrorCode.InvalidNumber, $"No existe el versículo {verse}: ningún capítulo tiene más de {CanonicalBooks.MaxVersesInAChapter}.")
            : null;

        private static ReferenceParseResult Fail(ReferenceErrorCode code, string message) => ReferenceParseResult.Fail(code, message);
        private static ReferenceParseResult Unexpected(string message) => ReferenceParseResult.Fail(ReferenceErrorCode.UnexpectedText, message);
    }

    private static string Shorten(string text) => text.Length <= 30 ? text : text[..30] + "…";
}

/// <summary>Forma canónica de una referencia (la inversa del parser): <c>Parse(Format(x)) == x</c>.</summary>
internal static class ReferenceFormatter
{
    public static string Format(ParsedReference reference)
    {
        var book = CanonicalBooks.Get(reference.BookNumber);
        var sb = new StringBuilder(book.ShortName);
        var single = book.Chapters == 1;

        int? verseChapter = null;   // capítulo del grupo de versículos en curso
        var first = true;
        foreach (var s in reference.Segments)
        {
            var sameGroup = !first && verseChapter is { } c && s is { StartVerse: not null } && s.StartChapter == c;
            sb.Append(first ? " " : sameGroup ? "," : "; ");
            first = false;

            if (s.IsWholeChapters)
            {
                verseChapter = null;
                if (single && s.StartChapter == 1) { sb.Length -= 1; continue; }   // «Jud» = el libro entero
                sb.Append(s.StartChapter.ToString(CultureInfo.InvariantCulture));
                if (s.EndChapter != s.StartChapter) sb.Append('-').Append(s.EndChapter.ToString(CultureInfo.InvariantCulture));
                continue;
            }

            if (!sameGroup) sb.Append(s.StartChapter.ToString(CultureInfo.InvariantCulture)).Append(':');
            sb.Append(s.StartVerse!.Value.ToString(CultureInfo.InvariantCulture));
            if (s.EndChapter != s.StartChapter)
                sb.Append('-').Append(s.EndChapter.ToString(CultureInfo.InvariantCulture)).Append(':').Append(s.EndVerse!.Value.ToString(CultureInfo.InvariantCulture));
            else if (s.EndVerse != s.StartVerse)
                sb.Append('-').Append(s.EndVerse!.Value.ToString(CultureInfo.InvariantCulture));
            verseChapter = s.EndChapter;
        }
        return sb.ToString();
    }
}
