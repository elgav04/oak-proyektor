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

    public ApplicationHost(ApplicationPaths paths, Serilog.ILogger serilog)
    {
        _paths = paths;
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
        services.AddSingleton<Outbox>();
        services.AddSingleton<DisplayManager>();
        services.AddSingleton<IDisplayManager>(sp => sp.GetRequiredService<DisplayManager>());
        services.AddSingleton<OutputController>();
        services.AddSingleton<IOutputController>(sp => sp.GetRequiredService<OutputController>());
        services.AddSingleton<IIpcHandler, Handlers.DisplayListHandler>();
        services.AddSingleton<IIpcHandler, Handlers.OutputGetStateHandler>();
        services.AddSingleton<IIpcHandler, Handlers.OutputOpenHandler>();
        services.AddSingleton<IIpcHandler, Handlers.OutputCloseHandler>();
        services.AddSingleton<IIpcHandler, Handlers.OutputSetMasterHandler>();
        services.AddSingleton<IpcDispatcher>();
        services.AddSingleton<IpcPump>();
        services.AddSingleton(new DatabaseOptions(Path.Combine(paths.DataDirectory, DatabaseOptions.FileName)));
        services.AddSingleton<ISqliteConnectionFactory, SqliteConnectionFactory>();
        services.AddSingleton<IDatabaseInitializer, DatabaseInitializer>();
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
        var polling = _services.GetRequiredService<DisplayManager>().StartPolling(ct);
        var db = Task.Run(async () =>
        {
            try { await _services.GetRequiredService<IDatabaseInitializer>().InitializeAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* apagado */ }
            catch (Exception ex) { _logger.LogError(ex, "No se pudo inicializar la base de datos"); }   // el panel y la salida no dependen de la BD todavía
        }, CancellationToken.None);
        _background = [.. pump, polling, db];
    }

    private void OnMainClosing()
    {
        _shutdown.Cancel();
        _services.GetRequiredService<InboundQueue>().Complete();
        _services.GetRequiredService<Outbox>().Complete();
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

