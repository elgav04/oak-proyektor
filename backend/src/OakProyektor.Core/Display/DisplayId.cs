using OakProyektor.Core.Output;

namespace OakProyektor.Core.Display;

/// <summary>Un monitor tal como lo ve el sistema: su rectángulo y, si se pudo leer, la ruta de dispositivo del monitor físico (Win32).</summary>
public readonly record struct DisplayCandidate(ScreenRect Rect, string? DevicePath);

/// <summary>
/// Identificador de un monitor. Es el ÚNICO punto donde se define el formato.
/// <list type="bullet">
/// <item><b><c>dev:&lt;ruta&gt;</c></b>: la ruta de la interfaz del monitor (<c>EnumDisplayDevices</c> con <c>EDD_GET_DEVICE_INTERFACE_NAME</c>, p. ej.
/// <c>\\?\DISPLAY#VTK2150#4&amp;1331027&amp;2&amp;UID4145#{…}</c>): identifica al monitor FÍSICO (modelo + puerto) y NO cambia con la resolución, el
/// escalado, la posición ni cuál sea el monitor principal (verificado, ver <c>tools/monitor-ids.ps1</c>). Cambia si el cable pasa a otro puerto
/// o si se sustituye el monitor: eso sí es «otro monitor».</item>
/// <item><b><c>x,y,wxh</c></b>: respaldo si Windows no dio la ruta. Cambia con la resolución y la posición.</item>
/// </list>
/// El nombre GDI (<c>\\.\DISPLAY1</c>) se descartó: Windows lo renumera al cambiar el monitor principal o reconectar.
/// </summary>
public static class DisplayId
{
    /// <summary>Respaldo por rectángulo (formato histórico <c>x,y,wxh</c>, sin prefijo; los ID de dispositivo siempre empiezan por <c>dev:</c>).</summary>
    public static string From(int x, int y, int width, int height) => $"{x},{y},{width}x{height}";

    public static string FromDevice(string devicePath) => $"dev:{devicePath.Trim().ToLowerInvariant()}";

    /// <summary>
    /// Asigna un ID único a cada monitor, en el mismo orden. Con ruta de dispositivo: <c>dev:</c>; sin ella (vacía o nula): por rectángulo.
    /// Si dos monitores comparten ruta (no debería pasar), se desempata por posición (izquierda→derecha, arriba→abajo) con <c>#2</c>, <c>#3</c>…,
    /// para que los ID sigan siendo únicos y estables.
    /// </summary>
    public static IReadOnlyList<string> Assign(IReadOnlyList<DisplayCandidate> monitors)
    {
        var ids = new string[monitors.Count];
        var byKey = new Dictionary<string, List<int>>();
        for (var i = 0; i < monitors.Count; i++)
        {
            var m = monitors[i];
            var key = string.IsNullOrWhiteSpace(m.DevicePath) ? From(m.Rect.X, m.Rect.Y, m.Rect.Width, m.Rect.Height) : FromDevice(m.DevicePath);
            ids[i] = key;
            if (!byKey.TryGetValue(key, out var list)) byKey[key] = list = [];
            list.Add(i);
        }
        foreach (var (key, indexes) in byKey.Where(p => p.Value.Count > 1))
        {
            var ordered = indexes.OrderBy(i => monitors[i].Rect.X).ThenBy(i => monitors[i].Rect.Y).ToList();
            for (var n = 1; n < ordered.Count; n++) ids[ordered[n]] = $"{key}#{n + 1}";
        }
        return ids;
    }
}
