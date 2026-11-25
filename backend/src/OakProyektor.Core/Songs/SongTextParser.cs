using System.Globalization;
using System.Text.RegularExpressions;

namespace OakProyektor.Core.Songs;

/// <summary>Formato que se reconoció en el texto (se muestra en la vista previa).</summary>
public enum SongTextFormat
{
    /// <summary>Sin texto.</summary>
    Empty,
    /// <summary>Con etiquetas entre corchetes: <c>[Verso 1]</c>.</summary>
    Brackets,
    /// <summary>Sin corchetes, pero con líneas que son exactamente un nombre de sección conocido («Coro», «Verso 2:»).</summary>
    AliasLines,
    /// <summary>Sin etiquetas: bloques separados por líneas en blanco.</summary>
    Blocks,
}

/// <summary>Una sección ya interpretada: <c>Label</c> canónica, <c>Kind</c> derivado de ella y <c>Text</c> normalizado (una línea en blanco = corte manual de diapositiva).</summary>
public sealed record ParsedSection(string Label, string Kind, string Text);

/// <summary>
/// Resultado de interpretar un texto. <c>Arrangement</c> son índices en <c>Sections</c> (el orden de reproducción; un índice repetido = una repetición de la sección).
/// <c>Warnings</c> informan sin impedir guardar; <c>Errors</c> impiden guardar (límites, líneas ambiguas, orden vacío). Los avisos y errores NUNCA citan la letra.
/// </summary>
public sealed record ParsedSong(
    IReadOnlyList<ParsedSection> Sections, IReadOnlyList<int> Arrangement, IReadOnlyList<string> Warnings, IReadOnlyList<string> Errors, SongTextFormat Format);

/// <param name="MergeIdenticalBlocks">Sin etiquetas: los bloques con texto idéntico se fusionan como una repetición (por defecto sí).</param>
public sealed record SongParseOptions(bool MergeIdenticalBlocks = true);

/// <summary>
/// Intérprete del texto de una canción (pegado o de un .txt). FUNCIÓN PURA que NUNCA lanza, para cualquier entrada (vacía, de miles de líneas, con etiquetas repetidas,
/// con caracteres de control o sustitutos sueltos), en tiempo lineal.
/// <list type="number">
/// <item><b>Con etiquetas</b>: una línea <c>[Verso 1]</c> abre una sección. Las líneas en blanco dentro del cuerpo son cortes manuales de diapositiva.</item>
/// <item><b>Etiqueta sola</b> (sin texto) o repetida con el MISMO texto = repetición: añade una referencia al orden, no una sección. Repetida con texto DISTINTO: nueva
/// sección «Coro (2)» y aviso. Una etiqueta sola puede ir antes de su definición.</item>
/// <item><b>Sin corchetes</b>: las líneas que son exactamente un nombre conocido («Coro», «Verso 2:») se toman como etiquetas; si no hay ninguna, cada bloque separado
/// por líneas en blanco es una sección («Sección 1…») y los bloques idénticos se fusionan.</item>
/// <item><b>Línea final «Orden:»</b> (opcional; debe ser la última línea no vacía): <c>Orden: Verso 1, Coro, Verso 2, Coro</c> o con abreviaturas <c>Orden: V1 C V2 C</c>.
/// Si falta, el orden es el de aparición. «Orden:» sin nada es un orden vacío explícito (error al guardar).</item>
/// </list>
/// Nunca toca puntuación, tildes ni mayúsculas del texto. Lo que pasa de los límites NO se recorta: se informa en <c>Errors</c>.
/// </summary>
public static partial class SongTextParser
{
    private sealed class Entry(string? label, List<string> body)
    {
        public string? Label { get; } = label;
        public List<string> Body { get; } = body;
    }

    public static ParsedSong Parse(string? text, SongParseOptions? options = null)
    {
        options ??= new SongParseOptions();
        var warnings = new WarningList();
        var errors = new List<string>();

        if (string.IsNullOrEmpty(text)) return new ParsedSong([], [], [], [], SongTextFormat.Empty);
        if (text.Length > SongLimits.MaxTextChars)
            return new ParsedSong([], [], [], [$"El texto es demasiado grande ({text.Length.ToString("N0", CultureInfo.GetCultureInfo("es"))} caracteres; el máximo es {SongLimits.MaxTextChars.ToString("N0", CultureInfo.GetCultureInfo("es"))})."], SongTextFormat.Empty);

        var normalized = SongTextNormalizer.Normalize(text, out var removed);
        if (removed > 0) warnings.Add($"Se quitaron {removed} caracteres de control o invisibles.");
        var lines = normalized.Split('\n');

        // 1) Línea final de orden.
        string? orderPayload = null;
        var last = lines.Length - 1;
        while (last >= 0 && lines[last].Length == 0) last--;
        if (last >= 0 && OrderLine().Match(lines[last]) is { Success: true } order)
        {
            orderPayload = order.Groups[1].Value.Trim();
            Array.Resize(ref lines, last);
        }

        // 2) Formato y etiquetas.
        var labelOfLine = new string?[lines.Length];
        var format = SongTextFormat.Blocks;
        for (var i = 0; i < lines.Length; i++)
            if (BracketLabel().Match(lines[i]) is { Success: true } m && m.Groups[1].Value.Trim().Length > 0)
            {
                labelOfLine[i] = m.Groups[1].Value.Trim();
                format = SongTextFormat.Brackets;
            }
        if (format == SongTextFormat.Blocks)
        {
            for (var i = 0; i < lines.Length; i++)
            {
                var candidate = lines[i].TrimEnd(':').TrimEnd();
                // Solo letras, números, espacios y guiones: «[[Coro]]» o «¡Coro!» son texto, aunque su clave de comparación (que ignora los símbolos) coincida con «Coro».
                if (candidate.Length is > 0 and <= 40 && AliasLineShape().IsMatch(candidate) && SectionAliases.TryParseAlias(candidate, out _, out _, out _))
                {
                    labelOfLine[i] = candidate;
                    format = SongTextFormat.AliasLines;
                }
            }
        }
        var hasText = lines.Any(l => l.Length > 0);
        if (!hasText && labelOfLine.All(l => l is null) && orderPayload is null) return new ParsedSong([], [], warnings.ToList(), errors, SongTextFormat.Empty);

        // 3) Entradas (etiqueta + cuerpo).
        var entries = new List<Entry>();
        if (format == SongTextFormat.Blocks)
        {
            var block = new List<string>();
            void Flush()
            {
                if (block.Count == 0) return;
                entries.Add(new Entry(null, block));
                block = [];
            }
            foreach (var line in lines)
            {
                if (line.Length == 0) Flush();
                else block.Add(line);
            }
            Flush();
        }
        else
        {
            var preamble = new List<string>();
            Entry? current = null;
            for (var i = 0; i < lines.Length; i++)
            {
                if (labelOfLine[i] is { } label)
                {
                    current = new Entry(SectionAliases.CanonicalLabel(label), []);
                    entries.Add(current);
                }
                else (current?.Body ?? preamble).Add(lines[i]);
            }
            var preambleText = SongTextNormalizer.JoinBody(preamble);
            if (preambleText.Length > 0)
            {
                entries.Insert(0, new Entry(null, [.. preamble]));
                warnings.Add("Había texto antes de la primera etiqueta: se guardó como una sección sin nombre.");
            }
        }

        // 4) Secciones y orden de aparición.
        var sections = new List<ParsedSection>();
        var keyToIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var textToIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var usedKeys = new HashSet<string>(StringComparer.Ordinal);
        var explicitKeys = entries.Where(e => e.Label is not null).Select(e => SectionAliases.Key(e.Label)).ToHashSet(StringComparer.Ordinal);
        var nextSuffix = new Dictionary<string, int>(StringComparer.Ordinal);
        var autoCount = 0;
        var merged = 0;
        var arrangement = new List<int>();
        var deferred = new List<(int Position, string Key, string Label)>();   // etiquetas sin cuerpo: se resuelven al final (pueden ir antes de su definición)
        var stop = false;

        foreach (var entry in entries)
        {
            if (stop) break;
            var body = SongTextNormalizer.JoinBody(entry.Body);
            if (body.Length == 0)
            {
                if (entry.Label is null) continue;
                if (arrangement.Count >= SongLimits.MaxArrangementItems + 1) { stop = true; break; }
                deferred.Add((arrangement.Count, SectionAliases.Key(entry.Label), entry.Label));
                arrangement.Add(-1);
                continue;
            }

            var entryLabel = entry.Label;
            if (entryLabel is not null && SectionAliases.Key(entryLabel).Length == 0)
            {
                // «[!!!]»: una etiqueta sin letras ni números no identifica nada; su texto se conserva como una sección sin nombre.
                warnings.Add("Una etiqueta sin letras ni números se tomó como texto de una sección sin nombre.");
                entryLabel = null;
            }

            int index;
            if (entryLabel is { } label)
            {
                var key = SectionAliases.Key(label);
                if (keyToIndex.TryGetValue(key, out var existing))
                {
                    if (sections[existing].Text == body) { index = existing; merged++; }
                    else
                    {
                        var unique = UniqueLabel(label, usedKeys, explicitKeys, nextSuffix);
                        index = Add(sections, new ParsedSection(unique, SectionAliases.KindOf(unique), body), usedKeys);
                        warnings.Add($"«{label}» aparece otra vez con un texto distinto: se creó «{unique}».");
                    }
                }
                else
                {
                    index = Add(sections, new ParsedSection(label, SectionAliases.KindOf(label), body), usedKeys);
                    keyToIndex[key] = index;
                }
            }
            else
            {
                if (options.MergeIdenticalBlocks && textToIndex.TryGetValue(body, out var same)) { index = same; merged++; }
                else
                {
                    string auto;
                    do { auto = "Sección " + (++autoCount).ToString(CultureInfo.InvariantCulture); }
                    while (usedKeys.Contains(SectionAliases.Key(auto)) || explicitKeys.Contains(SectionAliases.Key(auto)));
                    index = Add(sections, new ParsedSection(auto, SongSectionKinds.Other, body), usedKeys);
                    textToIndex.TryAdd(body, index);
                    keyToIndex[SectionAliases.Key(auto)] = index;
                }
            }
            arrangement.Add(index);
            if (sections.Count > SongLimits.MaxSections || arrangement.Count > SongLimits.MaxArrangementItems) stop = true;
        }

        // Referencias sin cuerpo (etiqueta sola): a la primera definición con esa clave.
        foreach (var (position, key, label) in deferred)
        {
            if (keyToIndex.TryGetValue(key, out var target)) arrangement[position] = target;
            else warnings.Add($"«{label}» aparece sin texto y no se definió en ninguna parte: se ignoró.");
        }
        arrangement.RemoveAll(i => i < 0);
        if (merged > 0) warnings.Add($"{merged} repetición(es) de una sección ya definida se guardaron como referencias, sin duplicar el texto.");

        // 5) Línea «Orden:».
        if (orderPayload is not null)
        {
            if (orderPayload.Length == 0) arrangement = [];
            else
            {
                var resolved = ResolveOrder(orderPayload, sections, keyToIndex, warnings);
                if (resolved.Count > 0) arrangement = resolved;
                else warnings.Add("La línea «Orden:» no coincide con ninguna sección: se ignoró.");
            }
        }

        // `stop` solo acota el trabajo con textos hostiles: el exceso se informa aquí (nunca se recorta en silencio).
        errors.AddRange(SongValidator.ValidateStructure(sections, arrangement));

        return new ParsedSong(sections, arrangement, warnings.ToList(), errors, format);
    }

    // ───────────────────────────── internos ─────────────────────────────

    private static int Add(List<ParsedSection> sections, ParsedSection section, HashSet<string> usedKeys)
    {
        sections.Add(section);
        usedKeys.Add(SectionAliases.Key(section.Label));
        return sections.Count - 1;
    }

    /// <summary>
    /// Nombre nuevo para una sección repetida con texto distinto: «Coro» → «Coro 2», «Coro 3»…; «Verso 1» → «Verso 1 bis», «Verso 1 bis 3»…; «Parte A» → «Parte A (2)»…
    /// Siempre en FORMA CANÓNICA (<see cref="SectionAliases.CanonicalLabel"/> lo deja igual: «Verso 1 (2)» se leería como «Verso 12»), con una clave que no choque con ninguna otra
    /// sección ni con una etiqueta escrita en el texto. Un contador por nombre: sin bucles cuadráticos.
    /// </summary>
    private static string UniqueLabel(string label, HashSet<string> usedKeys, HashSet<string> explicitKeys, Dictionary<string, int> nextSuffix)
    {
        var baseKey = SectionAliases.Key(label);
        var isAlias = SectionAliases.TryParseAlias(label, out var display, out _, out var number);
        var n = nextSuffix.TryGetValue(baseKey, out var stored) ? stored : 2;
        for (var attempts = 0; attempts < 10_000; attempts++, n++)
        {
            var counter = n.ToString(CultureInfo.InvariantCulture);
            var candidate = isAlias && number is null ? $"{display} {counter}"
                : isAlias ? (n == 2 ? $"{label} bis" : $"{label} bis {counter}")
                : $"{label} ({counter})";
            if (candidate.Length > SongLimits.MaxLabelChars || SectionAliases.CanonicalLabel(candidate) != candidate) continue;
            var key = SectionAliases.Key(candidate);
            if (usedKeys.Contains(key) || explicitKeys.Contains(key)) continue;
            nextSuffix[baseKey] = n + 1;
            return candidate;
        }
        // Inalcanzable en la práctica (10 000 colisiones): un nombre automático que no puede chocar.
        var fallback = 1;
        while (usedKeys.Contains(SectionAliases.Key("Sección " + fallback)) || explicitKeys.Contains(SectionAliases.Key("Sección " + fallback))) fallback++;
        return "Sección " + fallback.ToString(CultureInfo.InvariantCulture);
    }

    private static List<int> ResolveOrder(string payload, List<ParsedSection> sections, Dictionary<string, int> keyToIndex, WarningList warnings)
    {
        var result = new List<int>();
        var pieces = payload.Split([',', ';', '>', '→', '|'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var delimited = pieces.Length > 1 || payload.IndexOfAny([',', ';', '>', '→', '|']) >= 0;

        if (delimited)
        {
            for (var p = 0; p < pieces.Length; p++)
            {
                if (result.Count > SongLimits.MaxArrangementItems) break;   // se informa como error de longitud; no se sigue trabajando
                var found = ResolveOne(pieces[p], sections, keyToIndex);
                if (found is { } index) result.Add(index);
                else warnings.Add($"No se reconoció el elemento {(p + 1).ToString(CultureInfo.InvariantCulture)} de la línea «Orden:» (se cita por su posición, no por su texto).");
            }
            return result;
        }

        // Sin delimitadores («V1 C V2 C» o «Verso 1 Coro»): se prefiere la coincidencia más larga con una etiqueta (los nombres pueden llevar espacios).
        var tokens = payload.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var i = 0;
        while (i < tokens.Length && result.Count <= SongLimits.MaxArrangementItems)
        {
            var matched = false;
            for (var j = Math.Min(tokens.Length, i + 4); j > i; j--)
            {
                var candidate = string.Join(' ', tokens[i..j]);
                if (ResolveOne(candidate, sections, keyToIndex) is { } index)
                {
                    result.Add(index);
                    i = j;
                    matched = true;
                    break;
                }
            }
            if (!matched)
            {
                warnings.Add($"No se reconoció la palabra {(i + 1).ToString(CultureInfo.InvariantCulture)} de la línea «Orden:» (se cita por su posición, no por su texto).");
                i++;
            }
        }
        return result;
    }

    private static int? ResolveOne(string piece, List<ParsedSection> sections, Dictionary<string, int> keyToIndex)
    {
        var key = SectionAliases.Key(piece);
        if (key.Length == 0) return null;
        if (keyToIndex.TryGetValue(key, out var byLabel)) return byLabel;

        // Abreviatura: letras (V, C, E, P, PC, F, I) y número opcional.
        if (Abbreviation().Match(piece.Trim()) is not { Success: true } m) return null;
        var kind = SectionAliases.KindOfAbbreviation(m.Groups[1].Value);
        if (kind is null) return null;
        var wanted = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : (int?)null;

        var candidates = new List<int>();
        for (var i = 0; i < sections.Count; i++)
            if (sections[i].Kind == kind && (wanted is null || SectionAliases.NumberOf(sections[i].Label) == wanted)) candidates.Add(i);
        if (candidates.Count == 1) return candidates[0];
        if (candidates.Count > 1 && wanted is null)
        {
            var unnumbered = candidates.Where(i => SectionAliases.NumberOf(sections[i].Label) is null).ToList();
            if (unnumbered.Count == 1) return unnumbered[0];
        }
        return null;   // ninguna, o ambigua: no se adivina
    }

    /// <summary>Lista de avisos con tope (un texto hostil no debe producir miles).</summary>
    private sealed class WarningList
    {
        private readonly List<string> _items = [];
        private int _dropped;

        public void Add(string warning)
        {
            if (_items.Count < SongLimits.MaxWarnings) _items.Add(warning);
            else _dropped++;
        }

        public IReadOnlyList<string> ToList() =>
            _dropped == 0 ? _items : [.. _items, $"…y {_dropped} aviso(s) más."];
    }

    [GeneratedRegex(@"^\[\s*([^\[\]]{1,60}?)\s*\]\s*:?$")]
    internal static partial Regex BracketLabel();

    [GeneratedRegex(@"^orden\s*:\s*(.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    internal static partial Regex OrderLine();

    [GeneratedRegex(@"^([A-Za-z]{1,2})\s*(\d{1,3})?$")]
    private static partial Regex Abbreviation();

    [GeneratedRegex(@"^[\p{L}\p{N}][\p{L}\p{N} \-]*$")]
    private static partial Regex AliasLineShape();
}
