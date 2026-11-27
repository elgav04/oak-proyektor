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
    /// <summary>
    /// Abre/mueve/reanuda la salida. Lanza <see cref="IpcException"/> si no puede (el panel recibe <c>command.failed</c>).
    /// <paramref name="order"/> es el número de llegada de la orden (<see cref="OrderClock"/>; 0 = ahora): si el operador pidió una pausa
    /// DESPUÉS de esta orden, la salida termina en pausa aunque esa pausa se haya procesado antes.
    /// </summary>
    void Open(string? displayId, long order = 0);
    /// <summary>Cierra la salida. Lanza <see cref="IpcException"/> si la ventana no se pudo cerrar.</summary>
    void Close();
    void SetMaster(MasterMode master);
    /// <summary>Cambia el fondo (ya validado por el handler). No toca el contenido, el modo maestro ni la ventana: sigue igual aunque la salida esté en pausa.</summary>
    void SetBackground(BackgroundLayer background);
    /// <summary>«Reintentar»: vuelve a pedir a la salida el medio de fondo actual (sube su contador de reintento). Con un fondo que no es un medio no hace nada.</summary>
    void RetryBackground();
    /// <summary>Muestra un slide (ya validado). No cambia el modo maestro: si está en Black/Clear/Logo no se ve hasta volver a Live.</summary>
    /// <summary><paramref name="origin"/>: de qué elemento de una lista viene (solo ids), o nulo si no viene de una lista.</summary>
    void ShowContent(IReadOnlyList<string> blocks, TextTheme theme, int transitionMs, SlideOrigin? origin = null);
    /// <summary>Quita el slide del estado (queda solo el fondo).</summary>
    void ClearContent(int transitionMs);
    /// <summary>
    /// Muestra una alerta (ya validada y normalizada por el handler; <paramref name="durationSec"/> 0 = «hasta quitarla», con tope). Solo toca memoria: nunca espera a la BD ni al disco.
    /// Devuelve false si se IGNORÓ porque el operador quitó la alerta DESPUÉS de pedir esta (ver <see cref="AlertEngine"/>). Con la salida en pausa lanza <see cref="IpcException"/>
    /// (una alerta no sobrevive a una pausa). <paramref name="order"/> = número de llegada (0 = ahora).
    /// </summary>
    bool ShowAlert(string text, AlertMode mode, int durationSec, long order = 0);
    /// <summary>Quita la alerta (idempotente). Actúa sin pasar por el panel (atajo global). <paramref name="order"/> = número de llegada (0 = ahora).</summary>
    void HideAlert(long order = 0);
    /// <summary>
    /// Video en primer plano (v18). Carga y, si procede, arranca (<see cref="ForegroundRequest"/>, ya validado por el handler; con la salida oculta o en pausa se carga en PAUSA). Las órdenes de
    /// reproducción se ordenan por su número de llegada (<paramref name="order"/>; 0 = ahora): la última intención del operador gana aunque viajen por carriles distintos.
    /// </summary>
    void PlayForeground(ForegroundRequest request, long order = 0);
    void PauseForeground(long order = 0);
    /// <summary>Reanuda. Lanza <see cref="IpcException"/> (<c>media.outputHidden</c>) si la salida no lo mostraría (Negro, Texto limpio, Logo, en pausa o cerrada) y <c>media.nothingPlaying</c> si no hay video.</summary>
    void ResumeForeground(long order = 0);
    void SeekForeground(long positionMs);
    void SetForegroundVolume(int? clipVolumePct, bool? muted);
    /// <summary>El volumen maestro cambió: recalcula el volumen efectivo del video actual.</summary>
    void ApplyMasterVolume();
    void StopForeground(long order = 0);
    /// <summary>«Silencio»: pausa el video en primer plano al instante (atajo global; no pasa por el panel). Nunca reanuda.</summary>
    void SilenceForeground(long order = 0);
    /// <summary>La SALIDA informa de la posición y el estado reales del video (≈ 4 por segundo).</summary>
    void ReportForegroundProgress(MediaReportProgressPayload report);
    /// <summary>La salida informa de que no pudo reproducir su fondo. Si <paramref name="mediaUrl"/> ya no es el fondo actual se ignora; si lo es, se avisa al panel.</summary>
    void ReportBackgroundFailure(string mediaUrl, BackgroundFailureReason reason, string? detail);
    /// <summary>
    /// Minimiza la salida y le quita topmost, sin cerrarla ni perder su estado. Pensado para emergencias: NO espera a ninguna
    /// operación en curso (p. ej. una apertura lenta) y actúa por Win32 directamente sobre la ventana, sin esperar al hilo de UI.
    /// La pausa es ATÓMICA: una vez pedida no la deshace ninguna carrera (solo la levanta una orden posterior del operador).
    /// <paramref name="reason"/> debe ser <c>Manual</c> o <c>Emergency</c>; <paramref name="order"/> = número de llegada (0 = ahora).
    /// </summary>
    void EmergencyPause(OutputPauseReason reason, long order = 0);
    /// <summary>La configuración cambió: republica el estado.</summary>
    void SettingsChanged();
    void SetEmergencyHotkey(string? description);
    /// <summary>Estado de los atajos globales de respaldo (Negro, Limpiar texto, Logo, recargar el panel): el panel avisa de los que no se pudieron registrar.</summary>
    void SetBackupHotkeys(IReadOnlyList<HotkeyStatus> hotkeys);
    /// <summary>
    /// Atajo global de un control maestro: INTERRUPTOR (<see cref="MasterShortcut"/>): si ya está activo ese modo vuelve a Live. Actúa sin pasar por el panel.
    /// </summary>
    void ToggleMaster(MasterMode requested);
    /// <summary>El JavaScript del panel dejó de dar señales (o volvió). La salida sigue proyectando; esto solo se informa (y, si el operador lo configuró, se muestra en la salida).</summary>
    void SetPanelUnresponsive(bool unresponsive);
    /// <summary>Cierra la ventana de salida al apagar la app. Se llama desde el hilo de UI.</summary>
    void CloseForShutdown();
}

/// <summary>
/// Dueño del <see cref="OutputState"/>, del ciclo de vida de la ventana de salida y de su política de seguridad.
/// Candados: <c>_lifecycle</c> serializa abrir/cerrar/pausar/reanudar (puede mantenerse mientras se espera al hilo de UI);
/// <c>_gate</c> protege los campos (incluido <c>_pause</c>) y NUNCA se mantiene mientras se espera al hilo de UI, porque los handlers de Photino
/// (que corren en ese hilo) lo toman. El hilo de UI jamás toma <c>_lifecycle</c>. El candado del publicador de snapshots
/// se toma ANTES que <c>_gate</c> y nunca espera al hilo de UI.
/// La pausa vive en <see cref="OutputPauseState"/> (máquina de estados pura); aquí solo se llama a sus transiciones BAJO <c>_gate</c>,
/// de modo que cada comparación-y-escritura es atómica, y se ejecutan los efectos (Win32) fuera del candado.
/// </summary>
internal sealed partial class OutputController : IOutputController
{
    private static readonly Size WindowedSize = new(960, 540);
    private const int MaxRecentDisplays = 8;

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
    private readonly OrderClock _clock;
    private readonly ILogger<OutputController> _logger;
    private readonly SequencedSnapshotPublisher<OutputSnapshot> _publisher;
    private readonly IOutbox _outbox;
    private long _slideCounter;

    private readonly object _gate = new();
    private readonly object _lifecycle = new();
    private OutputState _state = OutputState.Initial;
    private PhotinoWindow? _window;
    private nint _hwnd;
    private string? _displayId;          // monitor asignado a la salida (en modo ventana, donde se colocó)
    private bool _windowed;
    private readonly OutputPauseState _pause = new();   // solo bajo _gate
    private ScreenRect? _appliedArea;    // rectángulo del monitor al que se ajustó la ventana a pantalla completa (solo bajo _gate); null en ventana normal
    private string? _hotkey;
    private IReadOnlyList<HotkeyStatus> _backupHotkeys = [];
    private bool _panelUnresponsive;
    // Antirrebote y tope de oscilaciones de la política (función pura con estado mínimo). Solo bajo _lifecycle.
    private readonly OutputSafetyEngine _engine = new();
    private readonly List<PausedRestoreGuard> _restoreGuards = [];   // referencias fuertes: el delegado nativo no puede recolectarse mientras exista la ventana

    public OutputController(
        PhotinoWindow main, WindowRegistry windows, IUiDispatcher ui, IDisplayManager displays, InboundQueue inbound,
        IOutbox outbox, ApplicationPaths paths, WebViewProfile profile, ISettingsStore settings, ScreenWakeLock wakeLock, OrderClock clock, ILogger<OutputController> logger)
    {
        _main = main; _windows = windows; _ui = ui; _displays = displays; _inbound = inbound;
        _outbox = outbox; _paths = paths; _profile = profile; _settings = settings; _wakeLock = wakeLock; _clock = clock; _logger = logger;
        _publisher = new SequencedSnapshotPublisher<OutputSnapshot>(
            outbox, IpcTypes.OutputStateChanged, Targets.All, (seq, s) => new OutputStateChangedPayload(seq, s.State, s.Window));
        _displays.Polled += OnPolled;
    }

    // ───────────────────────────── estado ─────────────────────────────

    private OutputSnapshot BuildSnapshot()
    {
        lock (_gate)
        {
            // La pausa solo se informa si hay ventana: una pausa pedida sin ventana queda registrada (por si hay una apertura en curso) pero no se muestra.
            var paused = _window is not null && _pause.IsPaused;
            return new OutputSnapshot(_state,
                new OutputWindowStatus(_window is not null, _window is null ? null : _displayId, _windowed, paused, paused ? _pause.Reason : null, _settings.Current.AutoRestoreOutput, _hotkey,
                    _settings.Current.PreferredDisplayId, _settings.Current.OnDisplayLost,
                    _panelUnresponsive, _settings.Current.ShowPanelAlertOnOutput, _backupHotkeys));
        }
    }

    public OutputStateChangedPayload Snapshot() => _publisher.Read(BuildSnapshot, (seq, s) => new OutputStateChangedPayload(seq, s.State, s.Window));

    /// <summary>Toma el snapshot y lo encola bajo un mismo candado; no publica nada si no cambió. Nunca llamar con <c>_gate</c> tomado.</summary>
    private void Publish()
    {
        _publisher.Publish(BuildSnapshot);
        DropAlertIfPaused();   // solo LEE la pausa que el snapshot acaba de informar (ver OutputController.Alerts.cs); no la modifica
        PauseForegroundIfHidden();   // idem para el video en primer plano (ver OutputController.Foreground.cs)
    }

    public void SetMaster(MasterMode master)
    {
        lock (_gate) _state = _state.WithMaster(master);
        Publish();
    }

    public void SetBackground(BackgroundLayer background)
    {
        lock (_gate) _state = _state.WithBackground(background);
        Publish();
    }

    public void RetryBackground()
    {
        lock (_gate) _state = _state.WithBackgroundRetry();
        Publish();
    }

    public void ShowContent(IReadOnlyList<string> blocks, TextTheme theme, int transitionMs, SlideOrigin? origin = null)
    {
        var slide = new Slide($"s{Interlocked.Increment(ref _slideCounter)}", [.. blocks.Select(b => new TextBlock(b))], theme, origin);
        lock (_gate) _state = _state.WithSlide(slide, transitionMs);
        Publish();
    }

    public void ClearContent(int transitionMs)
    {
        lock (_gate) _state = _state.WithoutSlide(transitionMs);
        Publish();
    }

    public void ReportBackgroundFailure(string mediaUrl, BackgroundFailureReason reason, string? detail)
    {
        string? fileName;
        lock (_gate)
        {
            // Un informe de un fondo que ya no es el actual (el operador cambió mientras tanto) no debe asustar al panel.
            if (_state.Background.MediaUrl is null || !string.Equals(_state.Background.MediaUrl, mediaUrl, StringComparison.Ordinal)) return;
            fileName = _state.Background.FileName;
        }
        var safeDetail = detail is null ? null : detail.Length > 200 ? detail[..200] : detail;
        _logger.LogWarning("La salida no pudo reproducir el fondo {FileName}: {Reason} ({Detail})", fileName, reason, safeDetail);
        _outbox.Publish(IpcEnvelope.Create(IpcKind.Event, IpcTypes.OutputBackgroundFailed,
            new OutputBackgroundFailedPayload(reason, BackgroundFailures.Describe(reason, fileName), fileName)), Targets.Control);
    }

    public void SettingsChanged() => Publish();

    public void SetEmergencyHotkey(string? description)
    {
        lock (_gate) _hotkey = description;
        Publish();
    }

    public void SetBackupHotkeys(IReadOnlyList<HotkeyStatus> hotkeys)
    {
        lock (_gate) _backupHotkeys = hotkeys;
        Publish();
    }

    public void ToggleMaster(MasterMode requested)
    {
        lock (_gate) _state = _state.WithMaster(MasterShortcut.Next(_state.Master, requested));
        Publish();
    }

    public void SetPanelUnresponsive(bool unresponsive)
    {
        lock (_gate) _panelUnresponsive = unresponsive;
        Publish();
    }

    // ─────────────────────────── abrir / cerrar ───────────────────────────

    public void Open(string? displayId, long order = 0)
    {
        if (order <= 0) order = _clock.Next();   // sin número (llamada interna): cuenta como «ahora», posterior a cualquier pausa ya pedida
        lock (_lifecycle) OpenLocked(displayId, order);
    }

    public void Close()
    {
        lock (_lifecycle) CloseLocked();
    }

    private void OpenLocked(string? displayId, long order)
    {
        _displays.Refresh();   // la UI puede tener una lista vieja
        _engine.Reset();       // una orden del operador empieza de cero el antirrebote y el conteo de oscilaciones
        var resolution = _displays.Resolve(displayId, _settings.Current.PreferredDisplayId);

        // I-B: un ID explícito que no está conectado NO se sustituye por otro monitor. Se falla ANTES de tocar nada: la ventana que ya exista
        // (por ejemplo la que está en pausa porque el proyector se desconectó) se conserva tal cual, no se cierra para abrir otra encima del panel.
        if (resolution.Kind == DisplayResolutionKind.NotFound)
        {
            _logger.LogWarning("Se pidió abrir la salida en el monitor {Display}, que no está conectado; no se hizo nada", displayId);
            Publish();
            throw new IpcException("output.displayNotFound",
                "El monitor elegido para la salida no está conectado. La salida no se movió: conéctalo de nuevo o elige otro monitor.");
        }
        var target = resolution.Display;
        // Pantalla completa solo en un monitor que NO aloja el panel; en cualquier otro caso, ventana normal que no lo tape.
        var windowed = target is null || target.HostsPanel;

        // El operador eligió este monitor (explícitamente o por el automático): se recuerda como «último usado»; si fue una elección explícita, además
        // pasa a ser el PREFERIDO, al que la salida siempre intenta volver. Un monitor que aloja el panel (ventana normal) no se guarda.
        if (target is not null && !target.HostsPanel) RememberDisplay(target.Id, makePreferred: displayId is not null);

        PhotinoWindow? existing; string? currentId; bool currentWindowed, paused;
        lock (_gate) { existing = _window; currentId = _displayId; currentWindowed = _windowed; paused = _pause.IsPaused; }

        if (existing is not null)
        {
            // «Reanudar salida» pide el MISMO monitor que ya tiene la salida: es una reanudación, NUNCA una reapertura. Si el monitor es el del panel
            // (o ya no sirve) la ventana sigue en pausa y se explica por qué; cerrarla y abrir otra la dejaba como ventana normal (hallazgo de las pruebas con el proyector).
            if (paused && displayId is not null && displayId == currentId)
            {
                TryResumeLocked(order);
                return;
            }
            if (currentWindowed == windowed && currentId == target?.Id)
            {
                if (paused) TryResumeLocked(order); else Publish();
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
            _ui.Invoke(() => CreateWindow(windowed, target, order));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo abrir la salida");
            Publish();
            throw new IpcException("output.openFailed", $"No se pudo abrir la ventana de salida: {ex.Message}", ex);
        }

        _wakeLock.Acquire();
        _logger.LogInformation("Salida abierta: monitor {Display}, {Mode}", target?.Id, windowed ? "ventana" : "pantalla completa");
        Publish();
    }

    /// <summary>Se ejecuta en el hilo de UI. La ventana se construye Y arranca ahí (verificado: en otro hilo hay interbloqueo).</summary>
    private void CreateWindow(bool windowed, DisplayInfo? target, long order)
    {
        PhotinoWindow? w = null;
        try
        {
            w = new PhotinoWindow(_main);
            // Se registra en cuanto existe (M11): si algo falla a mitad, el estado sabe de ella y se puede limpiar.
            // BeginWindow: la ventana nace corriendo, salvo que el operador haya pedido una pausa DESPUÉS de esta orden de abrir.
            bool bornPaused;
            lock (_gate)
            {
                _window = w; _hwnd = 0; _windowed = windowed; _displayId = target?.Id;
                _appliedArea = !windowed && target is not null ? ScreenRect.Of(target) : null;
                bornPaused = _pause.BeginWindow(order);
            }
            if (bornPaused) _logger.LogWarning("Se pidió una pausa después de esta orden de abrir: la salida nacerá en pausa");
            _windows.SetOutput(w);

            w.SetTitle("Oak-Proyektor — Salida")
             .SetDevToolsEnabled(false)
             .SetContextMenuEnabled(false)
             .SetUseOsDefaultLocation(false)
             .SetUseOsDefaultSize(false);
            _profile.ApplyToOutput(w);   // perfil PROPIO de la salida (otro entorno de WebView2 que el del panel): ver ApplicationHost, hallazgo I8
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
            // Ya en el hilo de UI (dueño de la ventana): se traga la restauración desde la barra de tareas mientras esté en pausa (evita el parpadeo).
            if (hwnd != 0)
            {
                var guard = new PausedRestoreGuard(() => { lock (_gate) return _pause.IsPaused; }, m => _logger.LogWarning("{Message}", m));
                lock (_restoreGuards) _restoreGuards.Add(guard);   // el delegado nativo debe vivir más que la ventana (DestroyWindow aún lo invoca); es un objeto diminuto por apertura
                if (!guard.Install(hwnd)) _logger.LogWarning("Guardia de restauración no instalado: la salida en pausa se re-minimizará por sondeo (≤ 500 ms)");
            }
            // La ventana ya existe. Asignar el handle y leer la pausa va en el MISMO bloque bajo candado, y EmergencyPause pide la pausa y lee el
            // handle en otro bloque igual: o la pausa ve el handle y minimiza ella misma, o aquí se ve la pausa y se minimiza. Nunca ninguna de las dos.
            bool minimizeNow;
            lock (_gate)
            {
                if (!ReferenceEquals(_window, w)) return;   // se cerró mientras tanto
                _hwnd = hwnd;
                minimizeNow = _pause.MarkWindowReady();
            }
            if (minimizeNow)
            {
                if (hwnd != 0) NativeMethods.MinimizeAndDropTopmost(hwnd);   // ya estamos en el hilo de UI: no hay nada que esperar
                else _logger.LogError("Hay una pausa pendiente pero la salida no tiene handle: no se puede minimizar");
                _logger.LogWarning("Salida en pausa desde su creación");
            }
        }
        catch
        {
            if (w is not null)
            {
                var orphan = w;
                lock (_gate) { if (ReferenceEquals(_window, orphan)) { _window = null; _hwnd = 0; _displayId = null; _windowed = false; _appliedArea = null; _pause.WindowClosed(); } }
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
            if (changed) { _window = null; _hwnd = 0; _displayId = null; _windowed = false; _appliedArea = null; _pause.WindowClosed(); }   // conserva el número de la última pausa del operador
        }
        if (!changed) return;
        _windows.SetOutput(null);
        _wakeLock.Release();
        _logger.LogInformation("Salida cerrada");
        Publish();
    }

    // ───────────────────── política de seguridad (pausa / reanudación) ─────────────────────

    public void EmergencyPause(OutputPauseReason reason, long order = 0)
    {
        if (order <= 0) order = _clock.Next();
        nint hwnd; PauseRequestResult result;
        // Pedir la pausa y leer el handle va en el MISMO bloque bajo candado (ver CreateWindow): o aquí se ve el handle y se minimiza, o
        // CreateWindow ve la pausa pendiente al terminar. La pausa queda REGISTRADA aunque no haya ventana (una apertura ya encolada debe respetarla).
        lock (_gate)
        {
            hwnd = _hwnd;
            result = _pause.RequestPause(reason, order);
        }

        switch (result.Phase)
        {
            case WindowPhase.None:
                _logger.LogInformation("Pausa {Reason} solicitada sin salida abierta; queda registrada por si hay una apertura en curso", reason);
                return;

            case WindowPhase.Creating:
                // La ventana aún se está creando: el estado y el aviso al panel cambian YA; CreateWindow la minimiza en cuanto exista.
                _logger.LogWarning("Salida en pausa ({Reason}) mientras se crea la ventana: se minimizará en cuanto exista", reason);
                Publish();
                return;
        }

        if (hwnd == 0)
        {
            _logger.LogError("Pausa {Reason} solicitada pero la salida no tiene handle: NO se pudo minimizar", reason);
            Publish();
            return;
        }

        // Sin _lifecycle, sin cola de UI y SIN esperar al hilo de UI (ShowWindowAsync / SWP_ASYNCWINDOWPOS): debe funcionar aunque haya una apertura
        // lenta en curso o el hilo de UI esté colgado. Se minimiza siempre, aunque ya estuviera en pausa: la ventana pudo restaurarse.
        try { NativeMethods.MinimizeAndDropTopmostAsync(hwnd); }
        catch (Exception ex) { _logger.LogError(ex, "Falló la minimización de emergencia"); }
        if (result.Changed) _logger.LogWarning("Salida en pausa: {Reason}", reason);
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
        PhotinoWindow? w; nint hwnd; string? targetId; bool windowed, paused; OutputPauseReason? reason; ScreenRect? applied;
        lock (_gate) { w = _window; hwnd = _hwnd; targetId = _displayId; windowed = _windowed; paused = _pause.IsPaused; reason = _pause.Reason; applied = _appliedArea; }

        if (w is not null && !windowed && targetId is not null && hwnd != 0)
        {
            var rect = paused ? default : NativeMethods.GetRect(hwnd);
            if (paused || rect is not null)
            {
                var settings = _settings.Current;
                // La decisión final: política + antirrebote (reajuste, monitor perdido, reanudar/mover estables ~500 ms) + tope de oscilaciones.
                var decision = _engine.Step(new OutputSafetyInput(
                    rect ?? default, !paused && NativeMethods.IsTopmost(hwnd), displays, targetId, paused, reason, settings.AutoRestoreOutput, applied,
                    settings.PreferredDisplayId, settings.OnDisplayLost, settings.RecentDisplayIds, _displays.DuplicateMode), DateTimeOffset.UtcNow);

                switch (decision.Kind)
                {
                    case SafetyActionKind.Pause:
                        if (decision.Reason == OutputPauseReason.Unstable)
                            _logger.LogError("La salida cambiaba de pausa a reanudación sin parar (posible fallo de pantalla): se queda en pausa hasta que el operador la reanude");
                        PauseLocked(decision.Reason ?? OutputPauseReason.PanelCrossed);
                        break;
                    case SafetyActionKind.Move:
                        MoveLocked(hwnd, decision, paused, reason);
                        break;
                    case SafetyActionKind.Resume:
                        if (reason is { } evaluated && displays.FirstOrDefault(d => d.Id == targetId) is { } display)
                            ResumeAutoLocked(hwnd, ScreenRect.Of(display), evaluated);
                        break;
                    case SafetyActionKind.Refit:
                        RefitLocked(hwnd, decision.Area ?? default);
                        break;
                    case SafetyActionKind.SetTopmost:
                        _logger.LogInformation("Topmost de la salida: {Topmost}", decision.Topmost);
                        _ui.Invoke(() => NativeMethods.SetTopmost(hwnd, decision.Topmost));
                        break;
                }
            }
        }

        // Aunque no haya cambiado nada más: una salida en pausa no puede estar visible (se restauró desde la barra de tareas, Alt+Tab, o
        // una restauración se cruzó con la pausa). También cubre la ventana en modo ventana normal.
        if (w is not null && hwnd != 0) EnforcePausedLocked(hwnd);

        Publish();   // algo visible puede haber cambiado sin acción (p. ej. el monitor dejó de estar conectado); el publicador no repite lo que no cambió
    }

    /// <summary>
    /// El mismo monitor cambió de resolución/posición: la ventana (a pantalla completa) se reajusta a su rectángulo nuevo, sin pausa ni avisos al operador.
    /// Si una pausa llegó mientras tanto, no se toca la ventana (está minimizada).
    /// </summary>
    private void RefitLocked(nint hwnd, ScreenRect area)
    {
        lock (_gate) { if (_pause.IsPaused) return; }
        _logger.LogInformation("El monitor de la salida cambió de resolución o posición: se reajusta a {X},{Y} {Width}x{Height}", area.X, area.Y, area.Width, area.Height);
        try
        {
            _ui.Invoke(() => NativeMethods.MoveTopmostTo(hwnd, area));
            lock (_gate) _appliedArea = area;   // solo si funcionó: si falla se reintenta en el próximo sondeo
        }
        catch (Exception ex) { _logger.LogError(ex, "Falló el reajuste de la salida"); }
    }

    /// <summary>
    /// La salida pasa a OTRO monitor (nunca el del panel): su monitor desapareció y la política es «mover», o el monitor preferido volvió. Si estaba en pausa
    /// automática, además se reanuda en él. Se hace solo si el estado sigue siendo el evaluado (una pausa del operador que llegue entre medias gana).
    /// </summary>
    private void MoveLocked(nint hwnd, OutputSafetyDecision decision, bool wasPaused, OutputPauseReason? evaluatedReason)
    {
        if (decision.DisplayId is not { } id || decision.Area is not { } area) return;
        lock (_gate)
        {
            if (wasPaused)
            {
                if (evaluatedReason is not { } r || !_pause.TryAutoResume(r))
                {
                    _logger.LogInformation("Movimiento automático cancelado: el estado de pausa cambió desde que se evaluó");
                    return;
                }
            }
            else if (_pause.IsPaused) return;   // una pausa llegó entre medias
            _displayId = id;
            _appliedArea = area;
        }
        _logger.LogWarning("La salida pasa al monitor {Display} ({Reason})", id, wasPaused ? "reanudación" : "monitor anterior no disponible o monitor preferido de vuelta");
        RememberDisplay(id, makePreferred: false);

        if (wasPaused) RestoreWindow(hwnd, area);   // restaura, la coloca y comprueba que no llegó otra pausa
        else
        {
            try { _ui.Invoke(() => NativeMethods.MoveTopmostTo(hwnd, area)); }
            catch (Exception ex) { _logger.LogError(ex, "Falló el movimiento de la salida"); }
        }
        Publish();
    }

    /// <summary>Guarda en la configuración local (no en la BD de contenido) el monitor usado y, si <paramref name="makePreferred"/>, lo marca como preferido.</summary>
    private void RememberDisplay(string id, bool makePreferred)
    {
        var current = _settings.Current;
        var recentNow = current.RecentDisplayIds ?? [];
        if ((!makePreferred || current.PreferredDisplayId == id) && recentNow.Count > 0 && recentNow[0] == id) return;   // nada que cambiar: sin escribir el disco

        _settings.UpdateAsync(s => s with
        {
            PreferredDisplayId = makePreferred ? id : s.PreferredDisplayId,
            RecentDisplayIds = [.. (s.RecentDisplayIds ?? []).Where(x => x != id).Prepend(id).Take(MaxRecentDisplays)],
        }, CancellationToken.None).ContinueWith(_ =>
        {
            try { Publish(); }   // el panel muestra cuál es el monitor preferido
            catch (Exception ex) { _logger.LogWarning(ex, "No se pudo publicar el cambio de monitor preferido"); }
        }, TaskScheduler.Default);
    }

    /// <summary>Pausa automática (monitor perdido / cruce con el panel). No rebaja una pausa más fuerte: si ya estaba en pausa por Manual o Emergency, no hace nada.</summary>
    private void PauseLocked(OutputPauseReason reason)
    {
        nint hwnd; bool changed;
        lock (_gate) { hwnd = _hwnd; changed = _pause.RequestPause(reason).Changed; }
        if (!changed) return;

        try { _ui.Invoke(() => NativeMethods.MinimizeAndDropTopmost(hwnd)); }
        catch (Exception ex) { _logger.LogError(ex, "Falló la minimización de la salida"); }
        _logger.LogWarning("Salida en pausa: {Reason}", reason);
        Publish();
    }

    /// <summary>Reanudación automática: solo si la salida SIGUE en pausa por el mismo motivo que se evaluó (si llegó una emergencia entre medias, no se reanuda).</summary>
    private void ResumeAutoLocked(nint hwnd, ScreenRect area, OutputPauseReason evaluated)
    {
        bool resumed;
        lock (_gate) resumed = _pause.TryAutoResume(evaluated);
        if (!resumed)
        {
            _logger.LogInformation("Reanudación automática cancelada: el estado de pausa cambió desde que se evaluó ({Reason})", evaluated);
            return;
        }
        RestoreWindow(hwnd, area);
        _logger.LogInformation("Salida reanudada");
        Publish();
    }

    /// <summary>Restaura la ventana (en su sitio y topmost si se da <paramref name="area"/>). Si una pausa llegó mientras tanto, vuelve a minimizarla.</summary>
    private void RestoreWindow(nint hwnd, ScreenRect? area)
    {
        if (area is { } applied) lock (_gate) _appliedArea = applied;   // el monitor puede haber cambiado de resolución mientras estaba en pausa
        try
        {
            _ui.Invoke(() =>
            {
                if (area is { } a) NativeMethods.RestoreTopmostAt(hwnd, a);
                else NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
            });
        }
        catch (Exception ex) { _logger.LogError(ex, "Falló la restauración de la salida"); }

        // La reanudación escribió «corriendo» ANTES de restaurar. Si el atajo de emergencia llegó justo entonces, su minimización pudo ejecutarse
        // ANTES que esta restauración y quedar deshecha: se comprueba el estado DESPUÉS y, si hay pausa, se minimiza otra vez.
        bool pausedAgain;
        lock (_gate) pausedAgain = _pause.IsPaused;
        if (pausedAgain)
        {
            _logger.LogWarning("Una pausa llegó mientras se restauraba la salida: se vuelve a minimizar");
            try { NativeMethods.MinimizeAndDropTopmostAsync(hwnd); }
            catch (Exception ex) { _logger.LogError(ex, "Falló la minimización tras la restauración"); }
        }
    }

    /// <summary>Si está en pausa pero la ventana no está minimizada, la minimiza. Sin esperar al hilo de UI.</summary>
    private void EnforcePausedLocked(nint hwnd)
    {
        bool paused;
        lock (_gate) paused = _pause.IsPaused;
        if (!paused || !NativeMethods.IsWindow(hwnd) || NativeMethods.IsIconic(hwnd)) return;

        _logger.LogWarning("La salida está en pausa pero se restauró (barra de tareas, Alt+Tab…): se vuelve a minimizar");
        try { NativeMethods.MinimizeAndDropTopmostAsync(hwnd); }
        catch (Exception ex) { _logger.LogError(ex, "Falló la minimización de una salida en pausa que se restauró"); }
    }

    /// <summary>
    /// El operador pidió abrir/reanudar con la salida en pausa (<paramref name="order"/> = número de llegada de esa orden). Se reanuda si su
    /// monitor está y no aloja el panel, Y si el operador no pidió una pausa DESPUÉS de esta orden (en ese caso sigue en pausa, sin error).
    /// </summary>
    private void TryResumeLocked(long order)
    {
        nint hwnd; string? targetId; bool windowed, superseded;
        lock (_gate) { hwnd = _hwnd; targetId = _displayId; windowed = _windowed; superseded = _pause.IsSupersededBy(order); }

        if (superseded)
        {
            _logger.LogWarning("Se pidió una pausa después de esta orden de reanudar: la salida sigue en pausa");
            Publish();
            return;
        }

        ScreenRect? area = null;
        if (!windowed)
        {
            var display = targetId is null ? null : _displays.Current.FirstOrDefault(d => d.Id == targetId);
            if (display is null || display.HostsPanel)
            {
                _logger.LogWarning("No se puede reanudar la salida: su monitor {Display} no está disponible o aloja el panel", targetId);
                Publish();
                // La ventana se queda en pausa tal cual (no se cierra ni se reabre como ventana normal) y se dice por qué.
                throw new IpcException("output.cannotResume", display is null
                    ? "No se puede reanudar la salida: el monitor donde proyectaba no está conectado. Sigue en pausa."
                    : "No se puede reanudar la salida: el panel de control está en el monitor de la salida y proyectar a pantalla completa lo taparía. Sigue en pausa: mueve el panel a otro monitor, o elige otro monitor y pulsa «Abrir en el monitor elegido».");
            }
            area = ScreenRect.Of(display);
        }

        // Comparar y escribir de forma atómica: entre la lectura de arriba y esta línea pudo llegar una pausa.
        OperatorResumeResult result;
        lock (_gate) result = _pause.TryOperatorResume(order);
        if (result != OperatorResumeResult.Resumed)
        {
            if (result == OperatorResumeResult.Superseded) _logger.LogWarning("Una pausa llegó mientras se reanudaba: la salida sigue en pausa");
            Publish();
            return;
        }

        RestoreWindow(hwnd, area);
        _logger.LogInformation("Salida reanudada por el operador");
        Publish();
    }
}
