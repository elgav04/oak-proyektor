using Microsoft.Extensions.Logging;
using OakProyektor.Core.Display;
using OakProyektor.Ipc;
using OakProyektor.Ipc.Messages;

namespace OakProyektor.App;

internal interface IDisplayManager
{
    IReadOnlyList<DisplayInfo> Current { get; }
    /// <summary>Se dispara en cada ciclo de sondeo (cambie algo o no), con la lista recién leída.</summary>
    event Action<IReadOnlyList<DisplayInfo>>? Polled;
    /// <summary>Consulta los monitores ahora. Si cambiaron, publica <c>display.changed</c> con la secuencia siguiente.</summary>
    IReadOnlyList<DisplayInfo> Refresh();
    /// <summary>La lista actual con su secuencia (para el reply de <c>display.list</c>); publica antes si hay un cambio pendiente.</summary>
    DisplayListPayload Snapshot();
    /// <summary>Monitor elegido: el del ID indicado si existe; si no, el primero que no aloja el panel. Null si no hay candidato.</summary>
    DisplayInfo? Resolve(string? requestedId);
}

/// <summary>
/// Enumera monitores y detecta cambios sondeando cada 500 ms, porque Photino no expone ningún evento de cambio de pantallas.
/// Nunca cierra ni mueve la salida por sí mismo: la política de seguridad vive en <see cref="OutputController"/>.
/// </summary>
internal sealed class DisplayManager : IDisplayManager
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private sealed class ListComparer : IEqualityComparer<IReadOnlyList<DisplayInfo>>
    {
        public bool Equals(IReadOnlyList<DisplayInfo>? x, IReadOnlyList<DisplayInfo>? y) => x is not null && y is not null && x.SequenceEqual(y);
        public int GetHashCode(IReadOnlyList<DisplayInfo> obj) => obj.Count;
    }

    private readonly IMonitorProvider _provider;
    private readonly IUiDispatcher _ui;
    private readonly ILogger<DisplayManager> _logger;
    private readonly SequencedSnapshotPublisher<IReadOnlyList<DisplayInfo>> _publisher;
    private volatile IReadOnlyList<DisplayInfo> _current = [];
    private bool _firstRefreshDone;

    public DisplayManager(IMonitorProvider provider, IUiDispatcher ui, IOutbox outbox, ILogger<DisplayManager> logger)
    {
        _provider = provider; _ui = ui; _logger = logger;
        _publisher = new SequencedSnapshotPublisher<IReadOnlyList<DisplayInfo>>(
            outbox, IpcTypes.DisplayChanged, Targets.Control, (seq, list) => new DisplayListPayload(seq, list), new ListComparer());
    }

    public IReadOnlyList<DisplayInfo> Current => _current;

    public event Action<IReadOnlyList<DisplayInfo>>? Polled;

    public IReadOnlyList<DisplayInfo> Refresh()
    {
        MonitorSnapshot? snapshot = null;
        _ui.Invoke(() => snapshot = _provider.Read());   // solo válido en el hilo de UI
        var panelCenter = new System.Drawing.Point(snapshot!.PanelCenterX, snapshot.PanelCenterY);
        var result = snapshot.Monitors
            .Select(m => new DisplayInfo(DisplayId.From(m.Area.X, m.Area.Y, m.Area.Width, m.Area.Height), m.Area.X, m.Area.Y, m.Area.Width, m.Area.Height, m.Scale,
                IsPrimary: m.Area is { X: 0, Y: 0 },
                HostsPanel: panelCenter.X >= m.Area.X && panelCenter.X < m.Area.Right && panelCenter.Y >= m.Area.Y && panelCenter.Y < m.Area.Bottom))
            .ToList();

        _current = result;
        if (_publisher.Publish(() => result) && _firstRefreshDone)
            _logger.LogInformation("Cambio de monitores: {Displays}", Describe(result));
        _firstRefreshDone = true;
        return result;
    }

    public DisplayListPayload Snapshot() => _publisher.Read(() => _current, (seq, list) => new DisplayListPayload(seq, list));

    public DisplayInfo? Resolve(string? requestedId)
    {
        var list = _current;
        if (requestedId is not null && list.FirstOrDefault(d => d.Id == requestedId) is { } found) return found;
        return list.FirstOrDefault(d => !d.HostsPanel);
    }

    /// <summary>Arranca el sondeo. Llamar solo después de que la ventana principal esté creada.</summary>
    public Task StartPolling(CancellationToken ct) => Task.Run(async () =>
    {
        try
        {
            Refresh();
            _logger.LogInformation("Monitores detectados: {Displays}", Describe(_current));
            using var timer = new PeriodicTimer(PollInterval);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try { Polled?.Invoke(Refresh()); }
                catch (Exception ex) { _logger.LogWarning(ex, "Falló un ciclo de sondeo de monitores"); }   // el sondeo sigue
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* apagado */ }
        catch (Exception ex) { _logger.LogError(ex, "El sondeo de monitores terminó inesperadamente"); }
    }, CancellationToken.None);

    private static string Describe(IReadOnlyList<DisplayInfo> list) =>
        string.Join("; ", list.Select(d => $"[{d.Id}] {d.Width}x{d.Height} x{d.Scale}{(d.HostsPanel ? " (panel)" : "")}"));
}
