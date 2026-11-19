using System.Drawing;
using OakProyektor.Core.Output;
using Photino.NET;

namespace OakProyektor.App;

internal sealed record MonitorArea(ScreenRect Area, double Scale);

/// <summary>Monitores y posición del panel en un instante. Coordenadas en píxeles físicos.</summary>
internal sealed record MonitorSnapshot(IReadOnlyList<MonitorArea> Monitors, int PanelCenterX, int PanelCenterY);

/// <summary>Fuente de monitores. Existe para poder sustituirla en pruebas y simular un proyector desconectado.</summary>
internal interface IMonitorProvider
{
    /// <summary>Solo válido en el hilo de UI y tras crear la ventana principal.</summary>
    MonitorSnapshot Read();
}

internal sealed class PhotinoMonitorProvider(PhotinoWindow main) : IMonitorProvider
{
    public MonitorSnapshot Read()
    {
        var monitors = main.Monitors.Select(m => new MonitorArea(ToRect(m.MonitorArea), m.Scale)).ToList();
        var location = main.Location;
        var size = main.Size;
        return new MonitorSnapshot(monitors, location.X + size.Width / 2, location.Y + size.Height / 2);
    }

    private static ScreenRect ToRect(Rectangle r) => new(r.X, r.Y, r.Width, r.Height);
}
