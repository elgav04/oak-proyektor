using System.Globalization;

namespace OakProyektor.Core.Songs;

/// <summary>
/// Reglas del modelo de canción (función pura). Los mensajes se refieren a las secciones por su POSICIÓN («la sección 3»), nunca por su texto: no llevan letra de canciones.
/// Se validan los datos ya normalizados (<see cref="NormalizeSection"/>).
/// </summary>
public static class SongValidator
{
    private const int MaxProblems = 20;

    /// <summary>Etiqueta canónica, tipo derivado y cuerpo normalizado de una sección escrita por el operador.</summary>
    public static ParsedSection NormalizeSection(string? label, string? text)
    {
        var canonical = SectionAliases.CanonicalLabel(label);
        return new ParsedSection(canonical, SectionAliases.KindOf(canonical), SongTextNormalizer.NormalizeBody(text, out _));
    }

    /// <summary>Motivos (en español, sin letra) por los que las secciones y el orden no se pueden guardar; vacío si todo está bien.</summary>
    public static IReadOnlyList<string> ValidateStructure(IReadOnlyList<ParsedSection> sections, IReadOnlyList<int> arrangement)
    {
        var problems = new List<string>();
        void Add(string problem) { if (problems.Count < MaxProblems) problems.Add(problem); }

        if (sections.Count > SongLimits.MaxSections) Add($"Hay demasiadas secciones ({sections.Count}; el máximo es {SongLimits.MaxSections}).");
        var firstWithKey = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < sections.Count && problems.Count < MaxProblems; i++)
        {
            var section = sections[i];
            var number = (i + 1).ToString(CultureInfo.InvariantCulture);

            if (LabelProblem(section.Label) is { } labelProblem) Add($"La sección {number}: {labelProblem}");
            else
            {
                var key = SectionAliases.Key(section.Label);
                if (key.Length == 0) Add($"La sección {number}: la etiqueta necesita al menos una letra o un número.");
                else if (firstWithKey.TryGetValue(key, out var first)) Add($"Las secciones {(first + 1).ToString(CultureInfo.InvariantCulture)} y {number} tienen la misma etiqueta (sin distinguir mayúsculas, tildes ni guiones).");
                else firstWithKey[key] = i;
            }
            if (!SongSectionKinds.IsValid(section.Kind)) Add($"La sección {number} tiene un tipo desconocido.");

            if (string.IsNullOrWhiteSpace(section.Text)) Add($"La sección {number} no tiene texto.");
            else if (section.Text.Length > SongLimits.MaxSectionChars) Add($"La sección {number} es demasiado larga ({section.Text.Length.ToString(CultureInfo.InvariantCulture)} caracteres; el máximo es {SongLimits.MaxSectionChars.ToString(CultureInfo.InvariantCulture)}).");
            else if (HasControlCharacters(section.Text)) Add($"La sección {number} contiene caracteres de control.");
            else if (HasAmbiguousLine(section.Text)) Add($"Una línea de la sección {number} tiene forma de etiqueta ([…]) o empieza por «Orden:»; cámbiala para que no se confunda al volver a leer el texto.");
        }

        if (arrangement.Count > SongLimits.MaxArrangementItems) Add($"El orden es demasiado largo ({arrangement.Count}; el máximo es {SongLimits.MaxArrangementItems} elementos).");
        else
        {
            if (sections.Count > 0 && arrangement.Count == 0) Add("El orden está vacío: añade al menos una sección.");
            if (arrangement.Any(i => i < 0 || i >= sections.Count)) Add("El orden hace referencia a una sección que no existe.");
        }
        return problems;
    }

    /// <summary>Motivo si la etiqueta no sirve (recortada, ≤ 60 caracteres, sin <c>[ ] , ;</c> ni caracteres de control); null si sirve.</summary>
    public static string? LabelProblem(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return "falta la etiqueta.";
        if (label.Length > SongLimits.MaxLabelChars) return $"la etiqueta es demasiado larga (máximo {SongLimits.MaxLabelChars} caracteres).";
        if (!string.Equals(label, label.Trim(), StringComparison.Ordinal) || HasControlCharacters(label)) return "la etiqueta tiene espacios sobrantes o caracteres de control.";
        if (label.AsSpan().IndexOfAny("[],;") >= 0) return "la etiqueta no puede llevar corchetes, comas ni punto y coma.";
        return null;
    }

    /// <summary>Motivos por los que los metadatos no se pueden guardar (se esperan ya normalizados: una línea, recortados).</summary>
    public static IReadOnlyList<string> ValidateMetadata(string title, string? author, string? copyright, string? ccli, string? notes)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(title)) problems.Add("Falta el título.");
        else if (title.Length > SongLimits.MaxTitleChars) problems.Add($"El título es demasiado largo (máximo {SongLimits.MaxTitleChars} caracteres).");
        if (author is { Length: > SongLimits.MaxAuthorChars }) problems.Add($"El autor es demasiado largo (máximo {SongLimits.MaxAuthorChars} caracteres).");
        if (copyright is { Length: > SongLimits.MaxCopyrightChars }) problems.Add($"Los derechos son demasiado largos (máximo {SongLimits.MaxCopyrightChars} caracteres).");
        if (ccli is { Length: > SongLimits.MaxCcliChars }) problems.Add($"El número CCLI es demasiado largo (máximo {SongLimits.MaxCcliChars} caracteres).");
        if (notes is { Length: > SongLimits.MaxNotesChars }) problems.Add($"Las notas son demasiado largas (máximo {SongLimits.MaxNotesChars} caracteres).");
        return problems;
    }

    public static readonly IReadOnlyList<string> BackgroundKinds = ["image", "video", "color"];

    /// <summary>
    /// Motivo si el fondo propio de la canción no sirve; null si sirve (o si no hay fondo). Aquí solo se comprueba la FORMA: que el archivo exista, no esté en una red, etc. lo
    /// valida <c>MediaPathValidator</c> en el momento de proyectar (<c>output.setBackground</c>).
    /// </summary>
    public static string? ValidateBackground(SongBackground? background)
    {
        if (background is null) return null;
        if (!BackgroundKinds.Contains(background.Kind)) return "El tipo de fondo debe ser imagen, video o color.";
        if (background.Kind == "color")
            return Output.ContentValidator.IsColor(background.Color) ? null : "El color de fondo no es válido (usa #RRGGBB).";
        if (string.IsNullOrWhiteSpace(background.Path)) return "Falta la ruta del archivo de fondo.";
        if (background.Path.Length > SongLimits.MaxBackgroundPathChars) return "La ruta del fondo es demasiado larga.";
        if (HasControlCharacters(background.Path) || background.Path.Contains('\n')) return "La ruta del fondo tiene caracteres no válidos.";
        if (!Path.IsPathFullyQualified(background.Path)) return "La ruta del fondo debe ser absoluta (con letra de unidad).";
        if (background.Color is not null && !Output.ContentValidator.IsColor(background.Color)) return "El color de reserva del fondo no es válido (usa #RRGGBB).";
        return null;
    }

    private static bool HasControlCharacters(string text)
    {
        foreach (var c in text)
            if (char.IsControl(c) && c != '\n') return true;
        return false;
    }

    private static bool HasAmbiguousLine(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0) continue;
            if (SongTextParser.BracketLabel().IsMatch(line) || SongTextParser.OrderLine().IsMatch(line)) return true;
        }
        return false;
    }
}
