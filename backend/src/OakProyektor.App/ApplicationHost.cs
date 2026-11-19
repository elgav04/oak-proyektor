using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OakProyektor.Data;
using OakProyektor.Ipc;
using Photino.NET;
using Serilog;

namespace OakProyektor.App;

/// <summary>Composición (DI), ventana principal y apagado ordenado.</summary>
internal sealed class ApplicationHost : IDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ServiceProvider _services;
    private readonly ILogger<ApplicationHost> _logger;
    private readonly ApplicationPaths _paths;
    private Task[] _background = [];

    /// <param name="configureServices">Gancho opcional (pruebas) para sustituir servicios, p. ej. <see cref="IMonitorProvider"/>.</param>
    public ApplicationHost(ApplicationPaths paths, Serilog.ILogger serilog, Action<IServiceCollection>? configureServices = null)
    {
        _paths = paths;

        // AISLAMIENTO ENTRE PANEL Y SALIDA (hallazgo I8) — verificado A MANO, no por tests automáticos:
        //   Fecha: 2026-11-18. Equipo: Windows 11 con proyector real.
        //   Versiones: Photino.NET 4.0.16 (Photino.Native 4.0.22), WebView2 Runtime 157.0.4341.60.
        //   Prueba: con la salida abierta y proyectando, se abrió DevTools en el panel (compilación Debug) y se ejecutó un
        //   bucle infinito en la consola. Resultado: la ventana de control se congeló, la proyección siguió funcionando.
        //   Por eso el panel y la salida se dejan en el MISMO entorno de WebView2 (el predeterminado de Photino):
        //   NO se les asigna un entorno propio ni se cambia su relación (ver CLAUDE.md y docs/REVISION_FASE1.md, I8).
        //   REPETIR esta prueba tras actualizar Photino.NET, Photino.Native o el runtime de WebView2: el aislamiento
        //   depende de cómo WebView2 reparte los procesos de renderizado, y eso puede cambiar entre versiones.
        MainWindow = new PhotinoWindow()
            .SetTitle("Oak-Proyektor — Control")
            .SetSize(1100, 720)
            .Center()
#if DEBUG
            .SetDevToolsEnabled(true);
#else
            .SetDevToolsEnabled(false);
#endif

        var services = new ServiceCollection();
        services.AddLogging(b => b.ClearProviders().AddSerilog(serilog, dispose: false));
        services.AddSingleton(paths);
        services.AddSingleton(MainWindow);
        services.AddSingleton(new WindowRegistry(MainWindow));
        services.AddSingleton<IUiDispatcher, PhotinoUiDispatcher>();
        services.AddSingleton<InboundQueue>();
        services.AddSingleton<IOutbox, Outbox>();
        services.AddSingleton<IMonitorProvider, PhotinoMonitorProvider>();
        services.AddSingleton<ISettingsStore, SettingsStore>();
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

        _logger.LogInformation("Arrancando. Datos: {Data}; web: {Web}", _paths.DataDirectory, _paths.WebRoot);
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

    public void Dispose()
    {
        _shutdown.Cancel();
        try { Task.WaitAll(_background, TimeSpan.FromSeconds(3)); } catch (Exception ex) { _logger.LogWarning(ex, "Tareas de fondo con error al apagar"); }
        _services.Dispose();
        _shutdown.Dispose();
    }
}








