using System.Drawing;
using Microsoft.Extensions.Logging;
using OakProyektor.Core.Display;
using OakProyektor.Ipc;
using OakProyektor.Ipc.Messages;
using Photino.NET;

namespace OakProyektor.App;

internal interface IDisplayManager
{
    IReadOnlyList<DisplayInfo> Current { get; }
    /// <summary>Consulta los monitores ahora. No publica eventos.</summary>
    IReadOnlyList<DisplayInfo> Refresh();
    /// <summary>Monitor elegido: el indicado si existe; si no, el primero que no aloja el panel. Null si no hay candidato.</summary>
    DisplayInfo? Resolve(int? requestedIndex);
}

/// <summary>
/// Enumera monitores con la API de Photino (<c>PhotinoWindow.Monitors</c>) y detecta cambios sondeando,
/// porque Photino no expone ningún evento de cambio de pantallas. Nunca cierra ni mueve la salida por sí mismo.
/// </summary>
internal sealed class DisplayManager(PhotinoWindow main, IUiDispatcher ui, Outbox outbox, ILogger<DisplayManager> logger) : IDisplayManager
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1500);
    private volatile IReadOnlyList<DisplayInfo> _current = [];

    public IReadOnlyList<DisplayInfo> Current => _current;

    public IReadOnlyList<DisplayInfo> Refresh()
    {
        IReadOnlyList<DisplayInfo> result = [];
        // Monitors y Location/Size solo son válidos en el hilo de UI y tras crear la ventana principal.
        ui.Invoke(() =>
        {
            var monitors = main.Monitors;
            var location = main.Location;
            var size = main.Size;
            var panelCenter = new Point(location.X + size.Width / 2, location.Y + size.Height / 2);
            result = monitors
                .Select((m, i) => new DisplayInfo(i, m.MonitorArea.X, m.MonitorArea.Y, m.MonitorArea.Width, m.MonitorArea.Height, m.Scale,
                    IsPrimary: m.MonitorArea is { X: 0, Y: 0 }, HostsPanel: m.MonitorArea.Contains(panelCenter)))
                .ToList();
        });
        _current = result;
        return result;
    }

    public DisplayInfo? Resolve(int? requestedIndex)
    {
        var list = _current;
        if (requestedIndex is { } i && i >= 0 && i < list.Count) return list[i];
        return list.FirstOrDefault(d => !d.HostsPanel);
    }

    /// <summary>Arranca el sondeo. Llamar solo después de que la ventana principal esté creada.</summary>
    public Task StartPolling(CancellationToken ct) => Task.Run(async () =>
    {
        try
        {
            Refresh();
            logger.LogInformation("Monitores detectados: {Displays}", Describe(_current));
            using var timer = new PeriodicTimer(PollInterval);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    var before = _current;
                    var after = Refresh();
                    if (before.SequenceEqual(after)) continue;
                    logger.LogInformation("Cambio de monitores: {Displays}", Describe(after));
                    outbox.Publish(IpcEnvelope.Create(IpcKind.Event, IpcTypes.DisplayChanged, new DisplayListPayload(after)), Targets.Control);
                }
                catch (Exception ex) { logger.LogWarning(ex, "Falló un ciclo de sondeo de monitores"); }   // el sondeo sigue
            }
        }
        catch (OperationCanceledException) { /* apagado */ }
        catch (Exception ex) { logger.LogError(ex, "El sondeo de monitores terminó inesperadamente"); }
    }, CancellationToken.None);

    private static string Describe(IReadOnlyList<DisplayInfo> list) =>
        string.Join("; ", list.Select(d => $"#{d.Index} {d.Width}x{d.Height}@({d.X},{d.Y}) x{d.Scale}{(d.HostsPanel ? " [panel]" : "")}"));
}
