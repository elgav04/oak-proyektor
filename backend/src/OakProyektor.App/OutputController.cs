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
    /// <summary>El snapshot actual con su secuencia. Publica antes cualquier cambio pendiente, para que reply y eventos no se contradigan.</summary>
    OutputStateChangedPayload Snapshot();
    /// <summary>Abre/mueve/reanuda la salida. Lanza <see cref="IpcException"/> si no puede (el panel recibe <c>command.failed</c>).</summary>
    void Open(string? displayId);
    /// <summary>Cierra la salida. Lanza <see cref="IpcException"/> si la ventana no se pudo cerrar.</summary>
    void Close();
    void SetMaster(MasterMode master);
    /// <summary>
    /// Minimiza la salida y le quita topmost, sin cerrarla ni perder su estado. Pensado para emergencias: NO espera a ninguna
    /// operación en curso (p. ej. una apertura lenta) y actúa por Win32 directamente sobre la ventana.
    /// </summary>
    void EmergencyPause(OutputPauseReason reason);
    /// <summary>La configuración cambió: republica el estado.</summary>
    void SettingsChanged();
    void SetEmergencyHotkey(string? description);
    /// <summary>Cierra la ventana de salida al apagar la app. Se llama desde el hilo de UI.</summary>
    void CloseForShutdown();
}

/// <summary>
/// Dueño del <see cref="OutputState"/>, del ciclo de vida de la ventana de salida y de su política de seguridad.
/// Candados: <c>_lifecycle</c> serializa abrir/cerrar/pausar/reanudar (puede mantenerse mientras se espera al hilo de UI);
/// <c>_gate</c> protege los campos y NUNCA se mantiene mientras se espera al hilo de UI, porque los handlers de Photino
/// (que corren en ese hilo) lo toman. El hilo de UI jamás toma <c>_lifecycle</c>. El candado del publicador de snapshots
/// se toma ANTES que <c>_gate</c> y nunca espera al hilo de UI.
/// </summary>
internal sealed class OutputController : IOutputController
{
    private static readonly Size WindowedSize = new(960, 540);
    private const int LostTicksBeforePause = 2;   // ~1 s: un parpadeo de enumeración no debe dejar la proyección en negro

    private sealed record OutputSnapshot(OutputState State, OutputWindowStatus Window);

    private readonly PhotinoWindow _main;
    private readonly WindowRegistry _windows;
    private readonly IUiDispatcher _ui;
    private readonly IDisplayManager _displays;
    private readonly InboundQueue _inbound;
    private readonly ApplicationPaths _paths;
    private readonly ISettingsStore _settings;
    private readonly ScreenWakeLock _wakeLock;
    private readonly WebViewProfile _profile;
    private readonly ILogger<OutputController> _logger;
    private readonly SequencedSnapshotPublisher<OutputSnapshot> _publisher;

    private readonly object _gate = new();
    private readonly object _lifecycle = new();
    private OutputState _state = OutputState.Initial;
    private PhotinoWindow? _window;
    private nint _hwnd;
    private string? _displayId;          // monitor asignado a la salida (en modo ventana, donde se colocó)
    private bool _windowed;
    private bool _paused;
    private OutputPauseReason? _reason;
    private string? _hotkey;
    private int _lostTicks;              // solo bajo _lifecycle

    public OutputController(
        PhotinoWindow main, WindowRegistry windows, IUiDispatcher ui, IDisplayManager displays, InboundQueue inbound,
        IOutbox outbox, ApplicationPaths paths, WebViewProfile profile, ISettingsStore settings, ScreenWakeLock wakeLock, ILogger<OutputController> logger)
    {
        _main = main; _windows = windows; _ui = ui; _displays = displays; _inbound = inbound;
        _paths = paths; _profile = profile; _settings = settings; _wakeLock = wakeLock; _logger = logger;
        _publisher = new SequencedSnapshotPublisher<OutputSnapshot>(
            outbox, IpcTypes.OutputStateChanged, Targets.All, (seq, s) => new OutputStateChangedPayload(seq, s.State, s.Window));
        _displays.Polled += OnPolled;
    }

    // ───────────────────────────── estado ─────────────────────────────

    private OutputSnapshot BuildSnapshot()
    {
        lock (_gate)
            return new OutputSnapshot(_state,
                new OutputWindowStatus(_window is not null, _window is null ? null : _displayId, _windowed, _paused, _reason, _settings.Current.AutoRestoreOutput, _hotkey));
    }

    public OutputStateChangedPayload Snapshot() => _publisher.Read(BuildSnapshot, (seq, s) => new OutputStateChangedPayload(seq, s.State, s.Window));

    /// <summary>Toma el snapshot y lo encola bajo un mismo candado; no publica nada si no cambió. Nunca llamar con <c>_gate</c> tomado.</summary>
    private void Publish() => _publisher.Publish(BuildSnapshot);

    public void SetMaster(MasterMode master)
    {
        lock (_gate) _state = _state.WithMaster(master);
        Publish();
    }

    public void SettingsChanged() => Publish();

    public void SetEmergencyHotkey(string? description)
    {
        lock (_gate) _hotkey = description;
        Publish();
    }

    // ─────────────────────────── abrir / cerrar ───────────────────────────

    public void Open(string? displayId)
    {
        lock (_lifecycle) OpenLocked(displayId);
    }

    public void Close()
    {
        lock (_lifecycle) CloseLocked();
    }

    private void OpenLocked(string? displayId)
    {
        _displays.Refresh();   // la UI puede tener una lista vieja
        var target = _displays.Resolve(displayId);
        // Pantalla completa solo en un monitor que NO aloja el panel; en cualquier otro caso, ventana normal que no lo tape.
        var windowed = target is null || target.HostsPanel;

        PhotinoWindow? existing; string? currentId; bool currentWindowed, paused;
        lock (_gate) { existing = _window; currentId = _displayId; currentWindowed = _windowed; paused = _paused; }

        if (existing is not null)
        {
            if (currentWindowed == windowed && currentId == target?.Id)
            {
                if (paused) TryResumeLocked(); else Publish();
                return;
            }
            CloseLocked();   // lanza si no pudo cerrarla: no se abre otra encima
        }

        if (!File.Exists(_paths.OutputPage))
        {
            _logger.LogError("No existe {Page}; ejecuta `npm run build` en frontend/", _paths.OutputPage);
            Publish();
            throw new IpcException("output.pageMissing", "No se encontró la página de la salida (output.html). Compila el frontend con «npm run build».");
        }

        try
        {
            _ui.Invoke(() => CreateWindow(windowed, target));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo abrir la salida");
            Publish();
            throw new IpcException("output.openFailed", $"No se pudo abrir la ventana de salida: {ex.Message}", ex);
        }

        _lostTicks = 0;
        _wakeLock.Acquire();
        _logger.LogInformation("Salida abierta: monitor {Display}, {Mode}", target?.Id, windowed ? "ventana" : "pantalla completa");
        Publish();
    }

    /// <summary>Se ejecuta en el hilo de UI. La ventana se construye Y arranca ahí (verificado: en otro hilo hay interbloqueo).</summary>
    private void CreateWindow(bool windowed, DisplayInfo? target)
    {
        PhotinoWindow? w = null;
        try
        {
            w = new PhotinoWindow(_main);
            // Se registra en cuanto existe (M11): si algo falla a mitad, el estado sabe de ella y se puede limpiar.
            lock (_gate)
            {
                _window = w; _hwnd = 0; _windowed = windowed; _displayId = target?.Id;
                _paused = false; _reason = null;
            }
            _windows.SetOutput(w);

            w.SetTitle("Oak-Proyektor — Salida")
             .SetDevToolsEnabled(false)
             .SetContextMenuEnabled(false)
             .SetUseOsDefaultLocation(false)
             .SetUseOsDefaultSize(false);
            _profile.ApplyTo(w);   // la MISMA carpeta de perfil que el panel: comparten el entorno de WebView2 (ver ApplicationHost, hallazgo I8)
            if (!windowed && target is not null)
            {
                w.SetChromeless(true).SetResizable(false).SetTopMost(true)
                 .SetLocation(new Point(target.X, target.Y)).SetSize(target.Width, target.Height);
            }
            else
            {
                w.SetResizable(true).SetSize(WindowedSize).Center();
            }
            var created = w;
            w.RegisterWebMessageReceivedHandler((_, message) => SafeEnqueue(created, message));
            w.RegisterWindowClosingHandler((_, _) =>
            {
                try { OnClosed(created); }
                catch (Exception ex) { _logger.LogError(ex, "Error en el cierre de la ventana de salida"); }   // M1: nunca cruzar a Photino
                return false;   // false = permitir el cierre
            });
            w.Load(_paths.OutputPage);
            // ADVERTENCIA (hallazgo I7): esta línea depende de un comportamiento NO documentado de Photino. WaitForClose() es lo que crea
            // la ventana nativa; llamado dentro de main.Invoke (hilo de UI, bucle ya en marcha) crea la ventana y vuelve, pero llamado desde el
            // handler WindowCreated de la ventana principal BLOQUEA hasta que esa ventana se cierra (verificado en el spike, Photino.NET 4.0.16).
            // Una versión distinta de Photino podría bloquear aquí y congelar las DOS ventanas. Por eso Photino.NET y Photino.Native están
            // fijados a versión exacta (OakProyektor.App.csproj; PhotinoPinTests falla si cambian) y tras actualizarlos es OBLIGATORIO repetir
            // la prueba manual de CLAUDE.md (abrir la salida sin que el panel se congele, y el bucle infinito en el panel).
            w.WaitForClose();
            nint hwnd = 0;
            try { hwnd = w.WindowHandle; }
            catch (Exception ex) { _logger.LogError(ex, "No se pudo obtener el handle de la salida: la política de seguridad no podrá actuar sobre ella"); }
            lock (_gate) { if (ReferenceEquals(_window, w)) _hwnd = hwnd; }
        }
        catch
        {
            if (w is not null)
            {
                var orphan = w;
                lock (_gate) { if (ReferenceEquals(_window, orphan)) { _window = null; _hwnd = 0; _displayId = null; _windowed = false; } }
                _windows.SetOutput(null);
                try { orphan.Close(); } catch { /* no llegó a crearse la ventana nativa */ }
            }
            throw;
        }
    }

    private void CloseLocked()
    {
        PhotinoWindow? w; nint hwnd;
        lock (_gate) { w = _window; hwnd = _hwnd; }
        if (w is null) { Publish(); return; }

        if (TryCloseNative(w, hwnd)) { OnClosed(w); return; }

        // M11: no se declara cerrada una ventana que sigue ahí; el panel la sigue mostrando como abierta y se puede reintentar.
        _logger.LogError("No se pudo cerrar la ventana de salida tras varios intentos; sigue abierta");
        Publish();
        throw new IpcException("output.closeFailed", "No se pudo cerrar la ventana de salida; sigue abierta. Inténtalo de nuevo.");
    }

    /// <summary>Cierra con reintentos y, si Photino falla, con WM_CLOSE. Devuelve true solo si la ventana ya no existe.</summary>
    private bool TryCloseNative(PhotinoWindow w, nint hwnd)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try { _ui.Invoke(w.Close); }
            catch (Exception ex) { _logger.LogWarning(ex, "Falló el cierre de la salida (intento {Attempt})", attempt); }
            if (WaitUntilGone(hwnd, 300)) return true;

            if (hwnd != 0) NativeMethods.PostMessage(hwnd, NativeMethods.WM_CLOSE, 0, 0);
            if (WaitUntilGone(hwnd, 300)) return true;
        }
        return false;
    }

    private static bool WaitUntilGone(nint hwnd, int milliseconds)
    {
        if (hwnd == 0) return true;   // nunca llegó a crearse la ventana nativa
        for (var waited = 0; waited <= milliseconds; waited += 25)
        {
            if (!NativeMethods.IsWindow(hwnd)) return true;
            Thread.Sleep(25);
        }
        return !NativeMethods.IsWindow(hwnd);
    }

    public void CloseForShutdown()
    {
        PhotinoWindow? w;
        lock (_gate) w = _window;
        if (w is null) return;
        try { w.Close(); } catch (Exception ex) { _logger.LogWarning(ex, "No se pudo cerrar la salida al apagar"); }
    }

    private void SafeEnqueue(PhotinoWindow source, string message)
    {
        try
        {
            _windows.MarkOutputReady(source);   // la página ya habló: desde ahora se le puede enviar
            _inbound.Enqueue(WindowKind.Output, message);
        }
        catch (Exception ex) { _logger.LogError(ex, "Error al encolar un mensaje de la salida"); }   // nunca propagar a Photino
    }

    private void OnClosed(PhotinoWindow w)
    {
        bool changed;
        lock (_gate)
        {
            changed = ReferenceEquals(_window, w);
            if (changed) { _window = null; _hwnd = 0; _displayId = null; _windowed = false; _paused = false; _reason = null; }
        }
        if (!changed) return;
        _windows.SetOutput(null);
        _wakeLock.Release();
        _logger.LogInformation("Salida cerrada");
        Publish();
    }

    // ───────────────────── política de seguridad (pausa / reanudación) ─────────────────────

    public void EmergencyPause(OutputPauseReason reason)
    {
        PhotinoWindow? w; nint hwnd;
        lock (_gate) { w = _window; hwnd = _hwnd; }
        if (w is null || hwnd == 0) { _logger.LogInformation("Pausa de emergencia solicitada, pero no hay salida abierta"); return; }

        // Sin _lifecycle ni cola de UI: debe funcionar aunque haya una apertura lenta en curso.
        try { NativeMethods.MinimizeAndDropTopmost(hwnd); }
        catch (Exception ex) { _logger.LogError(ex, "Falló la minimización de emergencia"); }
        lock (_gate) { _paused = true; _reason = reason; }
        _logger.LogWarning("Salida en pausa: {Reason}", reason);
        Publish();
    }

    private void OnPolled(IReadOnlyList<DisplayInfo> displays)
    {
        if (!System.Threading.Monitor.TryEnter(_lifecycle)) return;   // hay una operación en curso: se evalúa en el próximo ciclo
        try { EvaluateLocked(displays); }
        catch (Exception ex) { _logger.LogError(ex, "Falló la evaluación de seguridad de la salida"); }
        finally { System.Threading.Monitor.Exit(_lifecycle); }
    }

    private void EvaluateLocked(IReadOnlyList<DisplayInfo> displays)
    {
        PhotinoWindow? w; nint hwnd; string? targetId; bool windowed, paused; OutputPauseReason? reason;
        lock (_gate) { w = _window; hwnd = _hwnd; targetId = _displayId; windowed = _windowed; paused = _paused; reason = _reason; }

        if (w is not null && !windowed && targetId is not null && hwnd != 0)
        {
            var rect = paused ? default : NativeMethods.GetRect(hwnd);
            if (paused || rect is not null)
            {
                var decision = OutputSafetyPolicy.Evaluate(new OutputSafetyInput(
                    rect ?? default, !paused && NativeMethods.IsTopmost(hwnd), displays, targetId, paused, reason, _settings.Current.AutoRestoreOutput));

                if (decision is { Kind: SafetyActionKind.Pause, Reason: OutputPauseReason.DisplayLost })
                {
                    if (++_lostTicks < LostTicksBeforePause) decision = OutputSafetyDecision.None;
                }
                else _lostTicks = 0;

                switch (decision.Kind)
                {
                    case SafetyActionKind.Pause:
                        PauseLocked(decision.Reason ?? OutputPauseReason.PanelCrossed);
                        break;
                    case SafetyActionKind.Resume:
                        if (displays.FirstOrDefault(d => d.Id == targetId) is { } display)
                            ResumeLocked(hwnd, ScreenRect.Of(display), topmost: true);
                        break;
                    case SafetyActionKind.SetTopmost:
                        _logger.LogInformation("Topmost de la salida: {Topmost}", decision.Topmost);
                        _ui.Invoke(() => NativeMethods.SetTopmost(hwnd, decision.Topmost));
                        break;
                }
            }
        }

        Publish();   // algo visible puede haber cambiado sin acción (p. ej. el monitor dejó de estar conectado); el publicador no repite lo que no cambió
    }

    private void PauseLocked(OutputPauseReason reason)
    {
        nint hwnd;
        lock (_gate) hwnd = _hwnd;
        try { _ui.Invoke(() => NativeMethods.MinimizeAndDropTopmost(hwnd)); }
        catch (Exception ex) { _logger.LogError(ex, "Falló la minimización de la salida"); }
        lock (_gate) { _paused = true; _reason = reason; }
        _logger.LogWarning("Salida en pausa: {Reason}", reason);
        Publish();
    }

    private void ResumeLocked(nint hwnd, ScreenRect? area, bool topmost)
    {
        try
        {
            _ui.Invoke(() =>
            {
                if (area is { } a && topmost) NativeMethods.RestoreTopmostAt(hwnd, a);
                else NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
            });
        }
        catch (Exception ex) { _logger.LogError(ex, "Falló la restauración de la salida"); }
        lock (_gate) { _paused = false; _reason = null; }
        _lostTicks = 0;
        _logger.LogInformation("Salida reanudada");
        Publish();
    }

    /// <summary>El operador pidió abrir/reanudar con la salida en pausa: se reanuda si su monitor está y no aloja el panel.</summary>
    private void TryResumeLocked()
    {
        nint hwnd; string? targetId; bool windowed;
        lock (_gate) { hwnd = _hwnd; targetId = _displayId; windowed = _windowed; }

        if (windowed) { ResumeLocked(hwnd, null, topmost: false); return; }
        var display = targetId is null ? null : _displays.Current.FirstOrDefault(d => d.Id == targetId);
        if (display is null || display.HostsPanel)
        {
            _logger.LogWarning("No se puede reanudar la salida: su monitor no está disponible o aloja el panel");
            Publish();
            throw new IpcException("output.cannotResume", "No se puede reanudar la salida: su monitor no está conectado o aloja el panel.");
        }
        ResumeLocked(hwnd, ScreenRect.Of(display), topmost: true);
    }
}
