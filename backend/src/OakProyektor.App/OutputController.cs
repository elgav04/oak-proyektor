using System.Drawing;
using Microsoft.Extensions.Logging;
using OakProyektor.Core.Display;
using OakProyektor.Core.Output;
using OakProyektor.Ipc;
using OakProyektor.Ipc.Messages;
using Photino.NET;

namespace OakProyektor.App;

internal interface IOutputController
{
    OutputStateChangedPayload Snapshot();
    void Open(int? displayIndex);
    void Close();
    void SetMaster(MasterMode master);
    /// <summary>Cierra la ventana de salida al apagar la app. Se llama desde el hilo de UI.</summary>
    void CloseForShutdown();
}

/// <summary>
/// Dueño del <see cref="OutputState"/> y del ciclo de vida de la ventana de salida.
/// Los comandos llegan en serie desde la cola de entrada; solo <c>OnClosed</c> (hilo de UI) corre en paralelo,
/// por eso el estado compartido va bajo <c>_gate</c> y NUNCA se mantiene el candado mientras se espera al hilo de UI.
/// </summary>
internal sealed class OutputController(
    PhotinoWindow main, WindowRegistry windows, IUiDispatcher ui, IDisplayManager displays, InboundQueue inbound,
    Outbox outbox, ApplicationPaths paths, ILogger<OutputController> logger) : IOutputController
{
    private static readonly Size WindowedSize = new(960, 540);

    private readonly object _gate = new();
    private OutputState _state = OutputState.Initial;
    private PhotinoWindow? _window;
    private int? _displayIndex;
    private bool _windowed;

    public OutputStateChangedPayload Snapshot()
    {
        lock (_gate) return new OutputStateChangedPayload(_state, new OutputWindowStatus(_window is not null, _displayIndex, _windowed));
    }

    public void SetMaster(MasterMode master)
    {
        lock (_gate) _state = _state.WithMaster(master);
        Publish();
    }

    public void Open(int? displayIndex)
    {
        displays.Refresh();   // los índices de la UI pueden estar viejos
        var target = displays.Resolve(displayIndex);
        // Pantalla completa solo en un monitor que NO aloja el panel; en cualquier otro caso, ventana normal que no lo tape.
        var windowed = target is null || target.HostsPanel;

        PhotinoWindow? existing;
        int? currentIndex; bool currentWindowed;
        lock (_gate) { existing = _window; currentIndex = _displayIndex; currentWindowed = _windowed; }
        if (existing is not null)
        {
            if (currentIndex == target?.Index && currentWindowed == windowed) { Publish(); return; }
            Close();
        }

        if (!File.Exists(paths.OutputPage))
        {
            logger.LogError("No existe {Page}; ejecuta `npm run build` en frontend/", paths.OutputPage);
            Publish();
            return;
        }

        ui.Invoke(() =>
        {
            // La ventana se construye Y arranca en el hilo de UI (verificado: en otro hilo hay interbloqueo).
            var w = new PhotinoWindow(main)
                .SetTitle("Oak-Proyektor — Salida")
                .SetDevToolsEnabled(false)
                .SetContextMenuEnabled(false)
                .SetUseOsDefaultLocation(false)
                .SetUseOsDefaultSize(false);
            if (!windowed && target is not null)
            {
                w.SetChromeless(true).SetResizable(false).SetTopMost(true)
                 .SetLocation(new Point(target.X, target.Y)).SetSize(target.Width, target.Height);
            }
            else
            {
                w.SetResizable(true).SetSize(WindowedSize).Center();
            }
            w.RegisterWebMessageReceivedHandler((_, message) => SafeEnqueue(w, message));
            w.RegisterWindowClosingHandler((_, _) => { OnClosed(w); return false; });   // false = permitir el cierre
            w.Load(paths.OutputPage);
            w.WaitForClose();   // dentro de Invoke no bloquea: crea la ventana nativa y vuelve
            // Se registra aquí, en el hilo de UI y antes de volver al bucle: así el primer mensaje de la página
            // (que también se procesa en ese hilo) siempre encuentra la ventana registrada.
            lock (_gate) { _window = w; _displayIndex = target?.Index; _windowed = windowed; }
            windows.SetOutput(w);
        });
        logger.LogInformation("Salida abierta: monitor {Display}, {Mode}", target?.Index, windowed ? "ventana" : "pantalla completa");
        Publish();
    }

    public void Close()
    {
        PhotinoWindow? w;
        lock (_gate) w = _window;
        if (w is null) { Publish(); return; }

        try { ui.Invoke(w.Close); }
        catch (Exception ex) { logger.LogWarning(ex, "Falló el cierre de la ventana de salida"); }
        OnClosed(w);   // idempotente: si el handler de cierre ya corrió, no hace nada
    }

    public void CloseForShutdown()
    {
        PhotinoWindow? w;
        lock (_gate) w = _window;
        if (w is null) return;
        try { w.Close(); } catch (Exception ex) { logger.LogWarning(ex, "No se pudo cerrar la salida al apagar"); }
    }

    private void SafeEnqueue(PhotinoWindow source, string message)
    {
        try
        {
            windows.MarkOutputReady(source);   // la página ya habló: desde ahora se le puede enviar
            inbound.Enqueue(WindowKind.Output, message);
        }
        catch (Exception ex) { logger.LogError(ex, "Error al encolar un mensaje de la salida"); }   // nunca propagar a Photino
    }

    private void OnClosed(PhotinoWindow w)
    {
        bool changed;
        lock (_gate)
        {
            changed = ReferenceEquals(_window, w);
            if (changed) { _window = null; _displayIndex = null; _windowed = false; }
        }
        if (!changed) return;
        windows.SetOutput(null);
        logger.LogInformation("Salida cerrada");
        Publish();
    }

    private void Publish() =>
        outbox.Publish(IpcEnvelope.Create(IpcKind.Event, IpcTypes.OutputStateChanged, Snapshot()), Targets.All);
}


