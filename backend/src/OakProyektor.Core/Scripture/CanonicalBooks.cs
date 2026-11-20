namespace OakProyektor.Core.Scripture;

/// <summary>
/// Un libro del canon protestante de 66 libros. <c>Number</c> es el número CANÓNICO (1 a 66) que usan los pasajes de los servicios y el historial
/// (no depende de ninguna Biblia instalada). Los nombres de los libros y las cifras de capítulos no tienen derechos de autor.
/// </summary>
/// <param name="Usfm">Código USFM de tres caracteres (<c>GEN</c>, <c>1CO</c>…): lo usan las fuentes que identifican los libros así.</param>
/// <param name="Name">Nombre completo en español, como se muestra al operador.</param>
/// <param name="ShortName">Abreviatura canónica para mostrar referencias («Jn 3:16»).</param>
/// <param name="Testament"><c>OT</c> (Antiguo) o <c>NT</c> (Nuevo): el mismo valor que guarda <c>Books.Testament</c>.</param>
/// <param name="Chapters">Número de capítulos (1189 en total). Es una cifra del canon, igual en todas las Biblias protestantes.</param>
public sealed record CanonicalBook(int Number, string Usfm, string Name, string ShortName, string Testament, int Chapters)
{
    public const string OldTestament = "OT";
    public const string NewTestament = "NT";
}

public static class CanonicalBooks
{
    public const int Count = 66;
    public const int TotalChapters = 1189;
    /// <summary>El capítulo con más versículos del canon (Salmo 119): cota superior de cualquier número de versículo.</summary>
    public const int MaxVersesInAChapter = 176;

    public static IReadOnlyList<CanonicalBook> All { get; } = Build();

    public static CanonicalBook Get(int number) =>
        number is >= 1 and <= Count ? All[number - 1] : throw new ArgumentOutOfRangeException(nameof(number), number, "El número de libro debe estar entre 1 y 66.");

    public static CanonicalBook? Find(int number) => number is >= 1 and <= Count ? All[number - 1] : null;

    public static CanonicalBook? FindByUsfm(string? usfm) =>
        usfm is null ? null : All.FirstOrDefault(b => string.Equals(b.Usfm, usfm, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<CanonicalBook> Build()
    {
        (string Usfm, string Name, string Short, int Chapters)[] rows =
        [
            ("GEN", "Génesis", "Gn", 50), ("EXO", "Éxodo", "Éx", 40), ("LEV", "Levítico", "Lv", 27), ("NUM", "Números", "Nm", 36),
            ("DEU", "Deuteronomio", "Dt", 34), ("JOS", "Josué", "Jos", 24), ("JDG", "Jueces", "Jue", 21), ("RUT", "Rut", "Rt", 4),
            ("1SA", "1 Samuel", "1 S", 31), ("2SA", "2 Samuel", "2 S", 24), ("1KI", "1 Reyes", "1 R", 22), ("2KI", "2 Reyes", "2 R", 25),
            ("1CH", "1 Crónicas", "1 Cr", 29), ("2CH", "2 Crónicas", "2 Cr", 36), ("EZR", "Esdras", "Esd", 10), ("NEH", "Nehemías", "Neh", 13),
            ("EST", "Ester", "Est", 10), ("JOB", "Job", "Job", 42), ("PSA", "Salmos", "Sal", 150), ("PRO", "Proverbios", "Pr", 31),
            ("ECC", "Eclesiastés", "Ec", 12), ("SNG", "Cantares", "Cnt", 8), ("ISA", "Isaías", "Is", 66), ("JER", "Jeremías", "Jer", 52),
            ("LAM", "Lamentaciones", "Lm", 5), ("EZK", "Ezequiel", "Ez", 48), ("DAN", "Daniel", "Dn", 12), ("HOS", "Oseas", "Os", 14),
            ("JOL", "Joel", "Jl", 3), ("AMO", "Amós", "Am", 9), ("OBA", "Abdías", "Abd", 1), ("JON", "Jonás", "Jon", 4),
            ("MIC", "Miqueas", "Miq", 7), ("NAM", "Nahúm", "Nah", 3), ("HAB", "Habacuc", "Hab", 3), ("ZEP", "Sofonías", "Sof", 3),
            ("HAG", "Hageo", "Hag", 2), ("ZEC", "Zacarías", "Zac", 14), ("MAL", "Malaquías", "Mal", 4),
            ("MAT", "Mateo", "Mt", 28), ("MRK", "Marcos", "Mr", 16), ("LUK", "Lucas", "Lc", 24), ("JHN", "Juan", "Jn", 21),
            ("ACT", "Hechos", "Hch", 28), ("ROM", "Romanos", "Ro", 16), ("1CO", "1 Corintios", "1 Co", 16), ("2CO", "2 Corintios", "2 Co", 13),
            ("GAL", "Gálatas", "Gá", 6), ("EPH", "Efesios", "Ef", 6), ("PHP", "Filipenses", "Fil", 4), ("COL", "Colosenses", "Col", 4),
            ("1TH", "1 Tesalonicenses", "1 Ts", 5), ("2TH", "2 Tesalonicenses", "2 Ts", 3), ("1TI", "1 Timoteo", "1 Ti", 6), ("2TI", "2 Timoteo", "2 Ti", 4),
            ("TIT", "Tito", "Tit", 3), ("PHM", "Filemón", "Flm", 1), ("HEB", "Hebreos", "Heb", 13), ("JAS", "Santiago", "Stg", 5),
            ("1PE", "1 Pedro", "1 P", 5), ("2PE", "2 Pedro", "2 P", 3), ("1JN", "1 Juan", "1 Jn", 5), ("2JN", "2 Juan", "2 Jn", 1),
            ("3JN", "3 Juan", "3 Jn", 1), ("JUD", "Judas", "Jud", 1), ("REV", "Apocalipsis", "Ap", 22),
        ];
        var list = new List<CanonicalBook>(rows.Length);
        for (var i = 0; i < rows.Length; i++)
        {
            var number = i + 1;
            list.Add(new CanonicalBook(number, rows[i].Usfm, rows[i].Name, rows[i].Short,
                number <= 39 ? CanonicalBook.OldTestament : CanonicalBook.NewTestament, rows[i].Chapters));
        }
        return list;
    }
}
