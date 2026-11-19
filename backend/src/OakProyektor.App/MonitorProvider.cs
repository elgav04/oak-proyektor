using System.Drawing;
using OakProyektor.Core.Output;
using Photino.NET;

namespace OakProyektor.App;

/// <param name="DevicePath">Ruta de dispositivo del monitor físico (identidad estable), o null si no se pudo leer.</param>
internal sealed record MonitorArea(ScreenRect Area, double Scale, string? DevicePath = null);

/// <summary>Monitores y posición del panel en un instante. Coordenadas en píxeles físicos.</summary>
/// <param name="Duplicated">Windows está en modo «Duplicar» (varias salidas físicas comparten una fuente).</param>
internal sealed record MonitorSnapshot(IReadOnlyList<MonitorArea> Monitors, int PanelCenterX, int PanelCenterY, bool Duplicated = false);

/// <summary>Fuente de monitores. Existe para poder sustituirla en pruebas y simular un proyector desconectado.</summary>
internal interface IMonitorProvider
{
    /// <summary>Solo válido en el hilo de UI y tras crear la ventana principal.</summary>
    MonitorSnapshot Read();
}

/// <summary>
/// Centro del panel para saber en qué monitor está (hallazgo M10). Con el panel minimizado Windows lo aparca en (-32000, -32000), fuera de
/// todos los monitores: sin esto ninguno «alojaría el panel» y la salida podría abrirse a pantalla completa sobre el monitor del operador.
/// Mientras esté minimizado (o fuera de pantalla) se devuelve la ÚLTIMA posición en que se vio.
/// </summary>
internal sealed class PanelCenterTracker
{
    /// <summary>Windows aparca las ventanas minimizadas en (-32000, -32000); cualquier valor por debajo de esto no es una posición real.</summary>
    private const int OffScreenThreshold = -30000;

    private (int X, int Y)? _lastKnown;

    /// <returns>El centro real, o el último conocido si ahora está minimizado/fuera de pantalla; null si nunca se conoció.</returns>
    public (int X, int Y)? Update(int x, int y, int width, int height, bool isMinimized)
    {
        var offScreen = isMinimized || x <= OffScreenThreshold || y <= OffScreenThreshold;
        if (!offScreen) _lastKnown = (x + width / 2, y + height / 2);
        return _lastKnown;
    }
}

internal sealed class PhotinoMonitorProvider(PhotinoWindow main) : IMonitorProvider
{
    private readonly PanelCenterTracker _panel = new();

    // Última ruta conocida por rectángulo: si una lectura de Win32 falla un instante, el ID no debe saltar a «por rectángulo» y parecer otro monitor.
    private readonly Dictionary<ScreenRect, string> _lastDevices = [];

    public MonitorSnapshot Read()
    {
        var devices = MonitorDevices.Read();
        if (devices.Count > 0)
        {
            _lastDevices.Clear();
            foreach (var (rect, path) in devices) _lastDevices[rect] = path;
        }
        // Si Win32 no devolvió NADA (fallo transitorio) se reutiliza la última lectura buena; si devolvió algo, manda esa lectura.
        var monitors = main.Monitors.Select(m => { var rect = ToRect(m.MonitorArea); return new MonitorArea(rect, m.Scale, _lastDevices.GetValueOrDefault(rect)); }).ToList();
        var location = main.Location;
        var size = main.Size;
        var handle = main.WindowHandle;
        var minimized = handle != 0 && NativeMethods.IsIconic(handle);

        // Si nunca se vio una posición real (arrancó minimizada), se asume el monitor principal: allí se crea la ventana (Center()).
        var center = _panel.Update(location.X, location.Y, size.Width, size.Height, minimized)
                     ?? PrimaryCenter(monitors);
        return new MonitorSnapshot(monitors, center.X, center.Y, MonitorDevices.ReadDuplicateMode());
    }

    private static (int X, int Y) PrimaryCenter(IReadOnlyList<MonitorArea> monitors)
    {
        var primary = monitors.FirstOrDefault(m => m.Area is { X: 0, Y: 0 }) ?? monitors.FirstOrDefault();
        return primary is null ? (0, 0) : (primary.Area.X + primary.Area.Width / 2, primary.Area.Y + primary.Area.Height / 2);
    }

    private static ScreenRect ToRect(Rectangle r) => new(r.X, r.Y, r.Width, r.Height);
}