using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OakProyektor.Data;
using OakProyektor.Ipc;
using Photino.NET;
using Serilog;

namespace OakProyektor.App;

/// <summary>Composición (DI), ventana principal y apagado ordenado.</summary>
internal sealed class ApplicationHost : IAsyncDisposable
{
    /// <summary>Título de la ventana de control. La segunda instancia lo usa para encontrar a la primera.</summary>
    public const string ControlWindowTitle = "Oak-Proyektor — Control";

    private readonly CancellationTokenSource _shutdown = new();
    private readonly ServiceProvider _services;
    private readonly ILogger<ApplicationHost> _logger;
    private readonly ApplicationPaths _paths;
    private readonly WebViewProfile _profile;
    private Task[] _background = [];

    /// <param name="configureServices">Gancho opcional (pruebas) para sustituir servicios, p. ej. <see cref="IMonitorProvider"/>.</param>
    public ApplicationHost(ApplicationPaths paths, Serilog.ILogger serilog, Action<IServiceCollection>? configureServices = null)
    {
        _paths = paths;
        _profile = WebViewProfile.Choose(paths, Path.GetTempPath());

        // AISLAMIENTO ENTRE PANEL Y SALIDA (hallazgo I8) — verificado A MANO, no por tests automáticos:
        //   Fecha: 2026-11-18. Equipo: Windows 11 con proyector real.
        //   Versiones: Photino.NET 4.0.16 (Photino.Native 4.0.22), WebView2 Runtime 157.0.4341.60.
        //   Prueba: con la salida abierta y proyectando, se abrió DevTools en el panel (compilación Debug) y se ejecutó un
        //   bucle infinito en la consola. Resultado: la ventana de control se congeló, la proyección siguió funcionando.
        //   Por eso el panel y la salida se dejan en el MISMO entorno de WebView2 (la misma carpeta de perfil, _profile):
        //   NO se les asigna un entorno propio ni se cambia su relación (ver CLAUDE.md y docs/REVISION_FASE1.md, I8).
        //   El perfil se movió de %LOCALAPPDATA%\Photino a <datos>\cache\WebView2 (hallazgo I10) en AMBAS ventanas. Comprobado el 2026-11-19:
        //   la estructura de procesos de WebView2 con las dos ventanas abiertas es idéntica antes y después (1 navegador, 2 renderizadores,
        //   GPU, crashpad, 2 utilidades). Si se tocara el perfil de una sola ventana, dejarían de compartir entorno: no lo hagas.
        //   REPETIR esta prueba tras actualizar Photino.NET, Photino.Native o el runtime de WebView2: el aislamiento
        //   depende de cómo WebView2 reparte los procesos de renderizado, y eso puede cambiar entre versiones.
        MainWindow = _profile.ApplyTo(new PhotinoWindow().SetTitle(ControlWindowTitle).SetSize(1100, 720).Center())
#if DEBUG
            .SetDevToolsEnabled(true);
#else
            .SetDevToolsEnabled(false);
#endif

        var services = new ServiceCollection();
        services.AddLogging(b => b.ClearProviders().AddSerilog(serilog, dispose: false));
        services.AddSingleton(paths);
        services.AddSingleton(_profile);
        services.AddSingleton(MainWindow);
        services.AddSingleton(new WindowRegistry(MainWindow));
        services.AddSingleton<IUiDispatcher, PhotinoUiDispatcher>();
        services.AddSingleton<InboundQueue>();
        services.AddSingleton<IOutbox, Outbox>();
        services.AddSingleton<IMonitorProvider, PhotinoMonitorProvider>();
        services.AddSingleton<ISettingsStore, SettingsStore>();
        services.AddSingleton<OrderClock>();
        services.AddSingleton<ScreenWakeLock>();
        services.AddSingleton<EmergencyHotkey>();
        services.AddSingleton<DisplayManager>();
        services.AddSingleton<IDisplayManager>(sp => sp.GetRequiredService<DisplayManager>());
        services.AddSingleton<OutputController>();
        services.AddSingleton<IOutputController>(sp => sp.GetRequiredService<OutputController>());
        services.AddSingleton<IIpcHandler, Handlers.DisplayListHandler>();
        services.AddSingleton<IIpcHandler, Handlers.OutputGetStateHandler>();
        services.AddSingleton<IIpcHandler, Handlers.OutputOpenHandler>();
        services.AddSingleton<IIpcHandler, Handlers.OutputCloseHandler>();
        services.AddSingleton<IIpcHandler, Handlers.OutputSetMasterHandler>();
        services.AddSingleton<IIpcHandler, Handlers.OutputRecoverHandler>();
        services.AddSingleton<IIpcHandler, Handlers.SettingsSetAutoRestoreHandler>();
        services.AddSingleton<IIpcHandler, Handlers.SettingsSetDisplayLostPolicyHandler>();
        services.AddSingleton<IIpcHandler, Handlers.DatabaseGetStatusHandler>();
        services.AddSingleton<IpcDispatcher>();
        services.AddSingleton<InboundProcessor>();
        services.AddSingleton<IpcPump>();
        services.AddSingleton(new DatabaseOptions(Path.Combine(paths.DataDirectory, DatabaseOptions.FileName)));
        services.AddSingleton<ISqliteConnectionFactory, SqliteConnectionFactory>();
        services.AddSingleton<IDatabaseInitializer, DatabaseInitializer>();
        services.AddSingleton<DatabaseReadiness>();
        services.AddSingleton<IDatabaseReadiness>(sp => sp.GetRequiredService<DatabaseReadiness>());
        services.AddSingleton<DatabaseStatusReporter>();
        configureServices?.Invoke(services);
        _services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        _logger = _services.GetRequiredService<ILogger<ApplicationHost>>();
    }

    internal PhotinoWindow MainWindow { get; }
    internal IServiceProvider Services => _services;

    /// <summary>Bloquea hasta que se cierra la ventana principal (panel). Devuelve el código de salida.</summary>
    public int Run()
    {
        if (!File.Exists(_paths.ControlPage))
        {
            _logger.LogError("No existe {Page}; ejecuta `npm run build` en frontend/", _paths.ControlPage);
            MainWindow.LoadRawString("<h2 style=\"font-family:sans-serif\">Falta el frontend compilado. Ejecuta <code>npm run build</code> en <code>frontend/</code>.</h2>");
        }
        else
        {
            MainWindow.Load(_paths.ControlPage);
        }

        var inbound = _services.GetRequiredService<InboundQueue>();
        var windows = _services.GetRequiredService<WindowRegistry>();
        MainWindow.RegisterWebMessageReceivedHandler((_, message) =>
        {
            try
            {
                windows.MarkControlReady();   // la página ya habló: desde ahora se le puede enviar
                inbound.Enqueue(WindowKind.Control, message);
            }
            catch (Exception ex) { _logger.LogError(ex, "Error al encolar un mensaje del panel"); }   // jamás propagar a Photino
        });
        MainWindow.RegisterWindowCreatedHandler((_, _) => Guard(OnMainCreated, "creación de la ventana principal"));
        MainWindow.RegisterWindowClosingHandler((_, _) => { Guard(OnMainClosing, "cierre de la ventana principal"); return false; });

        _logger.LogInformation("Arrancando. Datos: {Data} (origen: {Origin}); WebView2: {Profile}; web: {Web}",
            _paths.DataDirectory, _paths.Origin, _profile.Directory ?? "(predeterminado de Photino)", _paths.WebRoot);
        if (_profile.Note is not null) _logger.LogWarning("{Note}", _profile.Note);
        MainWindow.WaitForClose();
        _logger.LogInformation("Ventana principal cerrada; apagando");
        return 0;
    }

    private void OnMainCreated()
    {
        var ct = _shutdown.Token;
        var pump = _services.GetRequiredService<IpcPump>().Start(ct);
        var startup = Task.Run(async () =>
        {
            try
            {
                var settings = _services.GetRequiredService<ISettingsStore>();
                await settings.LoadAsync(ct).ConfigureAwait(false);   // antes de que la política use AutoRestore

                var output = _services.GetRequiredService<IOutputController>();
                var hotkey = _services.GetRequiredService<EmergencyHotkey>();
                hotkey.Start(() => output.EmergencyPause(OakProyektor.Core.Output.OutputPauseReason.Emergency));
                output.SetEmergencyHotkey(hotkey.Description);   // null = no se pudo registrar; el panel lo avisa

                await _services.GetRequiredService<DisplayManager>().StartPolling(ct).ConfigureAwait(false);   // dura hasta el apagado
            }
            catch (OperationCanceledException) { /* apagado */ }
            catch (Exception ex) { _logger.LogError(ex, "Falló el arranque de los servicios de fondo"); }
        }, CancellationToken.None);
        // La BD se inicializa en segundo plano, pero su resultado es una SEÑAL (IDatabaseReadiness): los repositorios la esperan y un
        // fallo llega al panel como evento (DatabaseStatusReporter). StartAsync no lanza: el fallo queda en el estado.
        _ = _services.GetRequiredService<DatabaseStatusReporter>();   // se suscribe antes de que cambie el estado
        var db = _services.GetRequiredService<DatabaseReadiness>().StartAsync(_services.GetRequiredService<IDatabaseInitializer>(), ct);
        _background = [.. pump, startup, db];
    }

    private void OnMainClosing()
    {
        _shutdown.Cancel();
        _services.GetRequiredService<InboundQueue>().Complete();
        _services.GetRequiredService<IOutbox>().Complete();
        _services.GetRequiredService<EmergencyHotkey>().Dispose();
        _services.GetRequiredService<IOutputController>().CloseForShutdown();   // sin esperar a las tareas: estamos en el hilo de UI
    }

    /// <summary>Todo callback que Photino invoca desde código nativo pasa por aquí: una excepción no debe cruzar esa frontera.</summary>
    private void Guard(Action action, string what)
    {
        try { action(); }
        catch (Exception ex) { _logger.LogError(ex, "Excepción en {What}", what); }
    }

    /// <summary>
    /// Apagado asíncrono (hallazgo M2): cancela el trabajo de fondo y espera, con un límite de 3 s, a que termine, sin bloquear ningún hilo.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        try { await Task.WhenAll(_background).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Tareas de fondo con error o demasiado lentas al apagar"); }
        await _services.DisposeAsync().ConfigureAwait(false);
        _shutdown.Dispose();
    }
}
