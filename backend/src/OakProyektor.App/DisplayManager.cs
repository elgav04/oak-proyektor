using Microsoft.Extensions.Logging;
using OakProyektor.Core.Display;
using OakProyektor.Core.Output;
using OakProyektor.Ipc;
using OakProyektor.Ipc.Messages;

namespace OakProyektor.App;

internal interface IDisplayManager
{
    IReadOnlyList<DisplayInfo> Current { get; }
    /// <summary>Se dispara en cada ciclo de sondeo (cambie algo o no), con la lista recién leída.</summary>
    event Action<IReadOnlyList<DisplayInfo>>? Polled;
    /// <summary>Consulta los monitores ahora. No publica eventos.</summary>
    IReadOnlyList<DisplayInfo> Refresh();
    /// <summary>Monitor elegido: el indicado si existe; si no, el primero que no aloja el panel. Null si no hay candidato.</summary>
    DisplayInfo? Resolve(int? requestedIndex);
}

/// <summary>
/// Enumera monitores y detecta cambios sondeando cada 500 ms, porque Photino no expone ningún evento de cambio de pantallas.
/// Nunca cierra ni mueve la salida por sí mismo: la política de seguridad vive en <see cref="OutputController"/>.
/// </summary>
internal sealed class DisplayManager(IMonitorProvider provider, IUiDispatcher ui, Outbox outbox, ILogger<DisplayManager> logger) : IDisplayManager
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private volatile IReadOnlyList<DisplayInfo> _current = [];

    public IReadOnlyList<DisplayInfo> Current => _current;

    public event Action<IReadOnlyList<DisplayInfo>>? Polled;

    public IReadOnlyList<DisplayInfo> Refresh()
    {
        MonitorSnapshot? snapshot = null;
        ui.Invoke(() => snapshot = provider.Read());   // solo válido en el hilo de UI
        var panelCenter = new System.Drawing.Point(snapshot!.PanelCenterX, snapshot.PanelCenterY);
        var result = snapshot.Monitors
            .Select((m, i) => new DisplayInfo(i, m.Area.X, m.Area.Y, m.Area.Width, m.Area.Height, m.Scale,
                IsPrimary: m.Area is { X: 0, Y: 0 },
                HostsPanel: panelCenter.X >= m.Area.X && panelCenter.X < m.Area.Right && panelCenter.Y >= m.Area.Y && panelCenter.Y < m.Area.Bottom))
            .ToList();
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
                    if (!before.SequenceEqual(after))
                    {
                        logger.LogInformation("Cambio de monitores: {Displays}", Describe(after));
                        outbox.Publish(IpcEnvelope.Create(IpcKind.Event, IpcTypes.DisplayChanged, new DisplayListPayload(after)), Targets.Control);
                    }
                    Polled?.Invoke(after);
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
