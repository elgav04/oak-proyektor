namespace OakProyektor.Core.Display;

/// <summary>
/// Elige el «mejor» monitor para la salida cuando el suyo desaparece y la política es mover sola (función pura). Con 2, 3 o más monitores:
/// <list type="number">
/// <item><b>Excluir siempre el del panel</b> (la salida lo taparía) y el excluido por el llamador (el que acaba de desaparecer).</item>
/// <item><b>Preferir el último usado</b>: el primero de <c>recent</c> (más reciente primero) que siga conectado y sea candidato.</item>
/// <item>Si no, <b>preferir uno no principal</b> (el principal suele ser el equipo del operador).</item>
/// <item>Si hay empate, <b>el más a la derecha</b> (mayor X; después, el más arriba; después, por ID, para que sea determinista).</item>
/// </list>
/// Devuelve null si no queda ningún candidato (solo está el monitor del panel): entonces se pausa y se avisa.
/// </summary>
public static class BestDisplayPicker
{
    public static DisplayInfo? Choose(IReadOnlyList<DisplayInfo> displays, IReadOnlyList<string> recent, string? excludeId = null)
    {
        var candidates = displays.Where(d => !d.HostsPanel && d.Id != excludeId).ToList();
        if (candidates.Count == 0) return null;

        foreach (var id in recent)
            if (candidates.FirstOrDefault(d => d.Id == id) is { } lastUsed) return lastUsed;

        var pool = candidates.Where(d => !d.IsPrimary).ToList();
        if (pool.Count == 0) pool = candidates;

        return pool.OrderByDescending(d => d.X).ThenBy(d => d.Y).ThenBy(d => d.Id, StringComparer.Ordinal).First();
    }
}
