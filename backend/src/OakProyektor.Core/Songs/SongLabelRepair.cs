using System.Text;
using System.Text.RegularExpressions;

namespace OakProyektor.Core.Songs;

/// <summary>Una sección tal como quedó guardada (p. ej. tras convertir <c>SongSlides</c> en la migración 9).</summary>
public sealed record RepairSectionInput(long Id, int Position, string Label, string Text);

/// <summary>El resultado para una sección: su etiqueta, tipo y texto ya válidos, o <c>Remove</c> si no tenía texto.</summary>
public sealed record RepairedSection(long Id, string Label, string Kind, string Text, bool Remove);

/// <summary>
/// Repara las secciones de UNA canción para que se puedan guardar con las reglas actuales (<see cref="SongValidator"/>), usando las MISMAS reglas que el código
/// (<see cref="SectionAliases.CanonicalLabel"/>, <see cref="SectionAliases.Key"/> y el nombre «bis»/«n» de <see cref="SongTextParser.UniqueLabel"/>). Función PURA y estable:
/// aplicada a secciones que ya son válidas no cambia nada. No cita nunca la letra.
/// <list type="bullet">
/// <item>Sin texto (tras normalizar) → se quita (una diapositiva en blanco no proyecta nada).</item>
/// <item>Una línea con forma de etiqueta («[…]») o que empieza por «Orden:» → se envuelve entre paréntesis (no se pierde ni una letra).</item>
/// <item>Etiquetas: sin <c>[ ] , ;</c> ni caracteres de control, ≤ 60 caracteres, en forma canónica y con clave única. Un sufijo «(n)» puesto por la migración 9 sobre un nombre conocido
/// («Verso 1 (2)») NO se lee como número (daría «Verso 12»): se renombra como el intérprete («Verso 1 bis», «Coro 2»).</item>
/// <item>El tipo se deriva del nombre (un «Coro» ya no queda como «other»).</item>
/// </list>
/// </summary>
public static partial class SongLabelRepair
{
    public static IReadOnlyList<RepairedSection> Repair(IReadOnlyList<RepairSectionInput> sections)
    {
        var ordered = sections.OrderBy(s => s.Position).ThenBy(s => s.Id).ToList();
        var result = new List<RepairedSection>(ordered.Count);

        // 1) Texto: normalizar, quitar las vacías y desambiguar las líneas que se confundirían con una etiqueta.
        var survivors = new List<(RepairSectionInput Input, string Text, string Base, bool Forced)>();
        foreach (var s in ordered)
        {
            var text = FixText(s.Text);
            if (text.Length == 0) { result.Add(new RepairedSection(s.Id, s.Label, SongSectionKinds.Other, s.Text, true)); continue; }
            var (baseLabel, forced) = BaseLabel(s.Label, s.Position);
            survivors.Add((s, text, baseLabel, forced));
        }

        // 2) Etiquetas únicas: las claras conservan su nombre (la primera que lo usa); las que chocan o eran «(n)» se renombran sin pisar ninguna etiqueta escrita.
        var explicitKeys = survivors.Where(x => !x.Forced).Select(x => SectionAliases.Key(x.Base)).ToHashSet(StringComparer.Ordinal);
        var usedKeys = new HashSet<string>(StringComparer.Ordinal);
        var nextSuffix = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (input, text, baseLabel, forced) in survivors)
        {
            string label;
            var key = SectionAliases.Key(baseLabel);
            if (!forced && !usedKeys.Contains(key) && SongValidator.LabelProblem(baseLabel) is null && SectionAliases.CanonicalLabel(baseLabel) == baseLabel) label = baseLabel;
            else
            {
                var room = baseLabel.Length > 48 ? baseLabel[..48].TrimEnd() : baseLabel;   // sitio para el sufijo dentro de los 60 caracteres
                label = SongTextParser.UniqueLabel(room, usedKeys, explicitKeys, nextSuffix);
            }
            usedKeys.Add(SectionAliases.Key(label));
            result.Add(new RepairedSection(input.Id, label, SectionAliases.KindOf(label), text, false));
        }
        var position = ordered.Select((s, i) => (s.Id, i)).ToDictionary(x => x.Id, x => x.i);
        return [.. result.OrderBy(r => position[r.Id])];   // en el mismo orden de entrada
    }

    /// <summary>Texto normalizado como al guardar; las líneas ambiguas se envuelven entre paréntesis. Vacío si no queda nada.</summary>
    public static string FixText(string? text)
    {
        var body = SongTextNormalizer.NormalizeBody(text, out _);
        if (body.Length == 0) return "";
        var lines = body.Split('\n');
        for (var i = 0; i < lines.Length; i++)
            if (lines[i].Length > 0 && (SongTextParser.BracketLabel().IsMatch(lines[i]) || SongTextParser.OrderLine().IsMatch(lines[i])))
                lines[i] = "(" + lines[i] + ")";
        return string.Join('\n', lines);
    }

    private static (string Label, bool Forced) BaseLabel(string? raw, int position)
    {
        var cleaned = SongTextNormalizer.NormalizeSingleLine(Strip(raw));
        var forced = false;
        if (DisambiguationSuffix().Match(cleaned) is { Success: true } m && SectionAliases.TryParseAlias(m.Groups[1].Value, out _, out _, out _))
        {
            cleaned = m.Groups[1].Value;
            forced = true;
        }
        var label = SectionAliases.CanonicalLabel(cleaned);
        if (label.Length > SongLimits.MaxLabelChars) label = SectionAliases.CanonicalLabel(label[..SongLimits.MaxLabelChars].TrimEnd());
        if (SectionAliases.Key(label).Length == 0) { label = "Sección " + (position + 1).ToString(System.Globalization.CultureInfo.InvariantCulture); forced = false; }
        return (label, forced);
    }

    private static string Strip(string? label)
    {
        if (string.IsNullOrEmpty(label)) return "";
        var sb = new StringBuilder(label.Length);
        foreach (var c in label)
            if (c is not ('[' or ']' or ',' or ';')) sb.Append(c);
        return sb.ToString();
    }

    /// <summary>«Verso 1 (2)»: el sufijo que la migración 9 puso a las etiquetas repetidas.</summary>
    [GeneratedRegex(@"^(.*\S)\s+\(\d+\)$")]
    private static partial Regex DisambiguationSuffix();
}