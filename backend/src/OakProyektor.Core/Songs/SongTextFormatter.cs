using System.Text;

namespace OakProyektor.Core.Songs;

/// <summary>
/// Reconstruye el TEXTO ÚNICO con etiquetas a partir de las secciones y del orden (la inversa de <see cref="SongTextParser"/>). Cada sección se escribe UNA vez, en su
/// posición, como <c>[Etiqueta]</c> + cuerpo; si el orden no es «cada sección una vez, en el mismo orden», se añade al final la línea
/// <c>Orden: Etiqueta, Etiqueta, …</c>. Para modelos válidos y normalizados, <c>Parse(Format(x))</c> devuelve el mismo modelo (secciones, tipos y orden); lo comprueba
/// una prueba de propiedad con casos aleatorios.
/// </summary>
public static class SongTextFormatter
{
    public static string Format(IReadOnlyList<ParsedSection> sections, IReadOnlyList<int> arrangement)
    {
        if (sections.Count == 0) return "";
        var sb = new StringBuilder();
        for (var i = 0; i < sections.Count; i++)
        {
            if (i > 0) sb.Append("\n\n");
            sb.Append('[').Append(sections[i].Label).Append("]\n").Append(sections[i].Text);
        }
        if (!IsDefaultOrder(sections.Count, arrangement))
        {
            sb.Append("\n\nOrden:");
            var first = true;
            foreach (var index in arrangement)
            {
                if (index < 0 || index >= sections.Count) continue;   // un modelo inválido no debe romper el formato; el validador ya lo habrá rechazado
                sb.Append(first ? " " : ", ").Append(sections[index].Label);
                first = false;
            }
        }
        return sb.ToString();
    }

    /// <summary>true si el orden es 0, 1, 2… (cada sección una vez, en su posición): entonces no hace falta la línea «Orden:».</summary>
    internal static bool IsDefaultOrder(int sectionCount, IReadOnlyList<int> arrangement)
    {
        if (arrangement.Count != sectionCount) return false;
        for (var i = 0; i < sectionCount; i++)
            if (arrangement[i] != i) return false;
        return true;
    }
}
