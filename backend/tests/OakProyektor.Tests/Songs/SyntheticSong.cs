using System.Text;
using OakProyektor.Core.Songs;

namespace OakProyektor.Tests.Songs;

/// <summary>
/// Datos SINTÉTICOS para las pruebas del módulo de canciones: toda la «letra» está inventada aquí a partir de sílabas sueltas (nada parecido a una obra real, con o sin
/// derechos de autor). Ninguna prueba lee <c>data/</c> ni lleva letra de verdad.
/// </summary>
internal static class SyntheticSong
{
    private static readonly string[] Syllables = ["lu", "ma", "ri", "te", "no", "sa", "vi", "ko", "pe", "ña", "dú", "bé", "ló", "qui", "zu", "fa", "mé", "tí"];

    /// <summary>Frase única e inventada: sirve para comprobar que NINGÚN registro ni mensaje la contiene.</summary>
    public const string Sentinel = "Xilofonía-Quimérica-Zafiral-7291";

    public static string Word(Random random)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < random.Next(2, 4); i++) sb.Append(Syllables[random.Next(Syllables.Length)]);
        return sb.ToString();
    }

    public static string Line(Random random, int words = 4)
    {
        var line = string.Join(' ', Enumerable.Range(0, words).Select(_ => Word(random)));
        return char.ToUpperInvariant(line[0]) + line[1..];
    }

    /// <summary>Un cuerpo normalizado: <paramref name="lines"/> líneas con <paramref name="cuts"/> cortes manuales (una línea en blanco) repartidos entre ellas.</summary>
    public static string Body(Random random, int lines, int cuts = 0)
    {
        var cutAfter = Enumerable.Range(1, Math.Max(0, lines - 1)).OrderBy(_ => random.Next()).Take(cuts).ToHashSet();
        var sb = new StringBuilder();
        for (var i = 0; i < lines; i++)
        {
            if (i > 0) sb.Append('\n');
            if (cutAfter.Contains(i)) sb.Append('\n');
            sb.Append(Line(random));
        }
        return sb.ToString();
    }

    /// <summary>Un cuerpo corto y determinista a partir de un número (sin azar), para pruebas legibles.</summary>
    public static string Text(int n) => $"Linea sintetica {n}a lumari\nLinea sintetica {n}b tenoko";

    private static readonly string[] CustomLabels = ["Parte A", "Parte B", "Tema extra", "Mi sección", "Coro final", "Verso 2b", "Cierre", "Coro 1000", "Segunda voz", "Año nuevo"];

    /// <summary>
    /// Un modelo aleatorio VÁLIDO y normalizado (etiquetas canónicas y únicas, tipo derivado de la etiqueta, cuerpos normalizados) con un orden con repeticiones y, a veces, secciones
    /// sin usar. Es lo que el parser produce, y por tanto lo que debe sobrevivir a un ciclo formato → texto → parser.
    /// </summary>
    public static (List<ParsedSection> Sections, List<int> Arrangement) RandomModel(Random random)
    {
        var count = random.Next(1, 9);
        var labels = new List<string>();
        var keys = new HashSet<string>();
        var pool = new List<string>();
        for (var n = 1; n <= 5; n++) pool.Add($"Verso {n}");
        pool.AddRange(["Coro", "Estribillo", "Pre-coro", "Puente", "Final", "Intro", "Introducción", "Interludio", "Coda", "Estrofa 2", "Pre-estribillo", "Instrumental", "Outro"]);
        pool.AddRange(CustomLabels);
        while (labels.Count < count)
        {
            var label = SectionAliases.CanonicalLabel(pool[random.Next(pool.Count)]);
            if (keys.Add(SectionAliases.Key(label))) labels.Add(label);
        }

        var sections = labels.Select(l => new ParsedSection(l, SectionAliases.KindOf(l), Body(random, random.Next(1, 6), random.Next(0, 2)))).ToList();

        List<int> arrangement;
        switch (random.Next(4))
        {
            case 0: arrangement = [.. Enumerable.Range(0, count)]; break;                                   // el orden natural (sin línea «Orden:»)
            case 1: arrangement = [.. Enumerable.Range(0, count).OrderBy(_ => random.Next())]; break;       // reordenado
            case 2: arrangement = [.. Enumerable.Range(0, random.Next(2, 15)).Select(_ => random.Next(count))]; break;   // con repeticiones
            default:
                var used = Enumerable.Range(0, count).Where(_ => random.Next(3) > 0).DefaultIfEmpty(0).ToList();     // con secciones sin usar
                arrangement = [.. used.Concat(used.Take(2))];
                break;
        }
        return (sections, arrangement);
    }
}
