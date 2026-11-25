using System.Text;
using System.Text.RegularExpressions;

namespace OakProyektor.Data;

/// <summary>
/// Expresión FTS5 segura a partir de lo que escribió el usuario (compartida por la búsqueda de la Biblia y la de canciones): solo letras, dígitos y marcas combinantes
/// (nunca operadores, comillas sueltas, <c>*</c>, <c>NEAR</c>, guiones ni <c>columna:</c>). Las palabras sueltas se piden todas (AND); lo que va entre comillas dobles es una
/// frase. La entrada se normaliza a NFC (como el texto guardado: «e» + acento combinante = «é», que no parte la palabra), se procesan como mucho
/// <see cref="MaxQueryChars"/> caracteres y se arman como mucho <see cref="MaxQueryTerms"/> términos (H13).
/// </summary>
public static partial class FtsQueryBuilder
{
    /// <summary>Texto máximo que se procesa (un pegado enorme no debe armar una consulta gigante).</summary>
    public const int MaxQueryChars = 1000;
    public const int MaxQueryTerms = 32;

    /// <summary>Devuelve null si no queda ninguna palabra. Con <paramref name="prefixOnLastWord"/>, la última palabra suelta (no una frase) se busca por prefijo («alab» encuentra «alabad»).</summary>
    public static string? Build(string? query, bool prefixOnLastWord = false)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        if (query.Length > MaxQueryChars) query = query[..MaxQueryChars];
        try { query = query.Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { /* secuencia Unicode inválida (sustituto suelto): se busca tal cual */ }
        var terms = new List<string>();
        var rest = query;
        foreach (Match phrase in QuotedPhrase().Matches(query))
        {
            var words = Words().Matches(phrase.Groups[1].Value).Select(m => m.Value).ToList();
            if (words.Count > 0) terms.Add("\"" + string.Join(' ', words) + "\"");
            rest = rest.Replace(phrase.Value, " ", StringComparison.Ordinal);
        }
        terms.AddRange(Words().Matches(rest).Select(m => "\"" + m.Value + "\""));
        if (terms.Count > MaxQueryTerms) terms.RemoveRange(MaxQueryTerms, terms.Count - MaxQueryTerms);
        if (terms.Count == 0) return null;
        if (prefixOnLastWord && !terms[^1].Contains(' ')) terms[^1] += "*";   // "alab"*  (un solo término entre comillas dobles + * = prefijo)
        return string.Join(' ', terms);
    }

    [GeneratedRegex("\"([^\"]*)\"")]
    private static partial Regex QuotedPhrase();

    [GeneratedRegex(@"[\p{L}\p{N}\p{M}]+")]
    private static partial Regex Words();
}
