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
/// (que corren en ese hilo) lo toman. El hilo de UI jamás toma <c>_lifecycle</c>.
/// </summary>
internal sealed class OutputController : IOutputController
{
    private static readonly Size WindowedSize = new(960, 540);
    private const int LostTicksBeforePause = 2;   // ~1 s: un parpadeo de enumeración no debe dejar la proyección en negro

    private readonly PhotinoWindow _main;
    private readonly WindowRegistry _windows;
    private readonly IUiDispatcher _ui;
    private readonly IDisplayManager _displays;
    private readonly InboundQueue _inbound;
    private readonly Outbox _outbox;
    private readonly ApplicationPaths _paths;
    private readonly ISettingsStore _settings;
    private readonly ScreenWakeLock _wakeLock;
    private readonly ILogger<OutputController> _logger;

    private readonly object _gate = new();
    private readonly object _lifecycle = new();
    private OutputState _state = OutputState.Initial;
    private PhotinoWindow? _window;
    private nint _hwnd;
    private int? _displayIndex;          // solo se usa en modo ventana; en pantalla completa el índice se deriva del rectángulo
    private ScreenRect? _targetRect;     // identidad del monitor de la salida (null en modo ventana)
    private bool _windowed;
    private bool _paused;
    private OutputPauseReason? _reason;
    private string? _hotkey;
    private OutputWindowStatus? _lastStatus;
    private int _lostTicks;              // solo bajo _lifecycle

    public OutputController(
        PhotinoWindow main, WindowRegistry windows, IUiDispatcher ui, IDisplayManager displays, InboundQueue inbound,
        Outbox outbox, ApplicationPaths paths, ISettingsStore settings, ScreenWakeLock wakeLock, ILogger<OutputController> logger)
    {
        _main = main; _windows = windows; _ui = ui; _displays = displays; _inbound = inbound; _outbox = outbox;
        _paths = paths; _settings = settings; _wakeLock = wakeLock; _logger = logger;
        _displays.Polled += OnPolled;
    }

    // ───────────────────────────── estado ─────────────────────────────

    public OutputStateChangedPayload Snapshot()
    {
        var list = _displays.Current;
        lock (_gate)
        {
            int? index = null;
            if (_window is not null)
                index = _windowed ? _displayIndex : _targetRect is { } r ? list.FirstOrDefault(d => ScreenRect.Of(d) == r)?.Index : null;
            return new OutputStateChangedPayload(_state,
                new OutputWindowStatus(_window is not null, index, _windowed, _paused, _reason, _settings.Current.AutoRestoreOutput, _hotkey));
        }
    }

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

    public void Open(int? displayIndex)
    {
        lock (_lifecycle) OpenLocked(displayIndex);
    }

    public void Close()
    {
        lock (_lifecycle) CloseLocked();
    }

    private void OpenLocked(int? displayIndex)
    {
        _displays.Refresh();   // los índices de la UI pueden estar viejos
        var target = _displays.Resolve(displayIndex);
        // Pantalla completa solo en un monitor que NO aloja el panel; en cualquier otro caso, ventana normal que no lo tape.
        var windowed = target is null || target.HostsPanel;
        ScreenRect? wantedRect = windowed ? null : ScreenRect.Of(target!);

        PhotinoWindow? existing; ScreenRect? currentRect; int? currentIndex; bool currentWindowed, paused;
        lock (_gate) { existing = _window; currentRect = _targetRect; currentIndex = _displayIndex; currentWindowed = _windowed; paused = _paused; }

        if (existing is not null)
        {
            var same = currentWindowed == windowed && (windowed ? currentIndex == target?.Index : currentRect == wantedRect);
            if (same)
            {
                if (paused) TryResumeLocked(); else Publish();
                return;
            }
            CloseLocked();
            lock (_gate) existing = _window;
            if (existing is not null) { _logger.LogError("No se pudo cerrar la salida anterior; no se abre otra"); Publish(); return; }
        }

        if (!File.Exists(_paths.OutputPage))
        {
            _logger.LogError("No existe {Page}; ejecuta `npm run build` en frontend/", _paths.OutputPage);
            Publish();
            return;
        }

        try
        {
            _ui.Invoke(() => CreateWindow(windowed, target, wantedRect));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo abrir la salida");
            Publish();
            return;
        }

        _lostTicks = 0;
        _wakeLock.Acquire();
        _logger.LogInformation("Salida abierta: monitor {Display}, {Mode}", target?.Index, windowed ? "ventana" : "pantalla completa");
        Publish();
    }

    /// <summary>Se ejecuta en el hilo de UI. La ventana se construye Y arranca ahí (verificado: en otro hilo hay interbloqueo).</summary>
    private void CreateWindow(bool windowed, DisplayInfo? target, ScreenRect? wantedRect)
    {
        PhotinoWindow? w = null;
        try
        {
            w = new PhotinoWindow(_main);
            // Se registra en cuanto existe (M11): si algo falla a mitad, el estado sabe de ella y se puede limpiar.
            lock (_gate)
            {
                _window = w; _hwnd = 0; _windowed = windowed; _displayIndex = target?.Index; _targetRect = wantedRect;
                _paused = false; _reason = null;
            }
            _windows.SetOutput(w);

            w.SetTitle("Oak-Proyektor — Salida")
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
            var created = w;
            w.RegisterWebMessageReceivedHandler((_, message) => SafeEnqueue(created, message));
            w.RegisterWindowClosingHandler((_, _) =>
            {
                try { OnClosed(created); }
                catch (Exception ex) { _logger.LogError(ex, "Error en el cierre de la ventana de salida"); }   // M1: nunca cruzar a Photino
                return false;   // false = permitir el cierre
            });
            w.Load(_paths.OutputPage);
            w.WaitForClose();   // dentro de Invoke no bloquea: crea la ventana nativa y vuelve
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
                lock (_gate) { if (ReferenceEquals(_window, orphan)) { _window = null; _hwnd = 0; _targetRect = null; _windowed = false; _displayIndex = null; } }
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

        if (TryCloseNative(w, hwnd)) OnClosed(w);
        else
        {
            // M11: no se declara cerrada una ventana que sigue ahí; el panel la sigue mostrando como abierta y se puede reintentar.
            _logger.LogError("No se pudo cerrar la ventana de salida tras varios intentos; sigue abierta");
            Publish();
        }
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
            if (changed) { _window = null; _hwnd = 0; _displayIndex = null; _targetRect = null; _windowed = false; _paused = false; _reason = null; }
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
        PhotinoWindow? w; nint hwnd; ScreenRect? target; bool windowed, paused; OutputPauseReason? reason;
        lock (_gate) { w = _window; hwnd = _hwnd; target = _targetRect; windowed = _windowed; paused = _paused; reason = _reason; }

        if (w is not null && !windowed && target is { } targetRect && hwnd != 0)
        {
            var rect = paused ? default : NativeMethods.GetRect(hwnd);
            if (paused || rect is not null)
            {
                var decision = OutputSafetyPolicy.Evaluate(new OutputSafetyInput(
                    rect ?? default, !paused && NativeMethods.IsTopmost(hwnd), displays, targetRect, paused, reason, _settings.Current.AutoRestoreOutput));

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
                        ResumeLocked(hwnd, targetRect, topmost: true);
                        break;
                    case SafetyActionKind.SetTopmost:
                        _logger.LogInformation("Topmost de la salida: {Topmost}", decision.Topmost);
                        _ui.Invoke(() => NativeMethods.SetTopmost(hwnd, decision.Topmost));
                        break;
                }
            }
        }

        // Algo visible puede haber cambiado sin acción (p. ej. el índice del monitor): republicar solo si es distinto.
        OutputWindowStatus current;
        bool differs;
        lock (_gate) { current = Snapshot().Window; differs = current != _lastStatus; }
        if (differs) Publish();
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
        nint hwnd; ScreenRect? target; bool windowed;
        lock (_gate) { hwnd = _hwnd; target = _targetRect; windowed = _windowed; }

        if (windowed) { ResumeLocked(hwnd, null, topmost: false); return; }
        var display = target is { } t ? _displays.Current.FirstOrDefault(d => ScreenRect.Of(d) == t) : null;
        if (display is null || display.HostsPanel)
        {
            _logger.LogWarning("No se puede reanudar la salida: su monitor no está disponible o aloja el panel");
            Publish();
            return;
        }
        ResumeLocked(hwnd, target, topmost: true);
    }

    private void Publish()
    {
        var snapshot = Snapshot();
        lock (_gate) _lastStatus = snapshot.Window;
        _outbox.Publish(IpcEnvelope.Create(IpcKind.Event, IpcTypes.OutputStateChanged, snapshot), Targets.All);
    }
}


