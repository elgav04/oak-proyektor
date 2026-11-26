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
        //   ACTUALIZADO 2026-11-19: el panel y la salida tienen perfiles de WebView2 DISTINTOS (carpetas distintas = navegadores distintos). Con un perfil
        //   compartido, un Load() del panel (incluso sano) lo dejaba en el MISMO renderizador que la salida y un bucle infinito congelaba también la
        //   proyección (medido con tools/i8-reload-isolation-test.ps1). La prueba pasa ahora de forma automatizada: ver CLAUDE.md («PRUEBA OBLIGATORIA»).
        //   El perfil se movió de %LOCALAPPDATA%\Photino a <datos>\cache\WebView2 (hallazgo I10). Comprobado el 2026-11-19 con perfil compartido (estructura anterior):
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
        services.AddSingleton<GlobalHotkeys>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<PanelWatchdog>();
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
        services.AddSingleton<Core.Output.IMediaFileSystem, Core.Output.PhysicalMediaFileSystem>();
        services.AddSingleton<IIpcHandler, Handlers.OutputSetBackgroundHandler>();
        services.AddSingleton<IIpcHandler, Handlers.OutputShowContentHandler>();
        services.AddSingleton<IIpcHandler, Handlers.OutputClearContentHandler>();
        services.AddSingleton<IIpcHandler, Handlers.OutputReportBackgroundFailureHandler>();
        services.AddSingleton<IIpcHandler, Handlers.OutputRetryBackgroundHandler>();
        services.AddSingleton<IIpcHandler, Handlers.SettingsSetAutoRestoreHandler>();
        services.AddSingleton<IIpcHandler, Handlers.SettingsSetDisplayLostPolicyHandler>();
        services.AddSingleton<IIpcHandler, Handlers.SettingsSetPanelAlertOnOutputHandler>();
        services.AddSingleton<IIpcHandler, Handlers.DatabaseGetStatusHandler>();
        services.AddSingleton<IIpcHandler, Handlers.SettingsGetStatusHandler>();
        services.AddSingleton<IpcDispatcher>();
        services.AddSingleton<InboundProcessor>();
        services.AddSingleton<IpcPump>();
        services.AddSingleton(new DatabaseOptions(Path.Combine(paths.DataDirectory, DatabaseOptions.FileName)));
        services.AddSingleton<ISqliteConnectionFactory, SqliteConnectionFactory>();
        services.AddSingleton<IDatabaseInitializer, DatabaseInitializer>();
        services.AddSingleton<DatabaseReadiness>();
        services.AddSingleton<IDatabaseReadiness>(sp => sp.GetRequiredService<DatabaseReadiness>());
        services.AddSingleton<DatabaseStatusReporter>();
        services.AddSingleton<Data.Backups.DatabaseBackupService>();
        services.AddSingleton<Data.Backups.IBackupStatus>(sp => sp.GetRequiredService<Data.Backups.DatabaseBackupService>());

        // Biblias (protocolo v6). Los límites y la Biblia predeterminada se leen de settings.json en cada consulta.
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<ISettingsStore>();
            return new Data.Scripture.BibleQueryOptions(() => settings.Current.DefaultBible, () => settings.Current.CitationHistoryLimit);
        });
        // El historial de citas se anota en segundo plano: el carril en vivo no espera al bloqueo de escritura de SQLite (H5).
        services.AddSingleton<Data.Scripture.BackgroundCitationRecorder>();
        services.AddSingleton<Data.Scripture.ICitationRecorder>(sp => sp.GetRequiredService<Data.Scripture.BackgroundCitationRecorder>());
        services.AddSingleton<Data.Scripture.IBibleQueryService>(sp => new Data.Scripture.BibleQueryService(
            sp.GetRequiredService<ISqliteConnectionFactory>(), sp.GetRequiredService<IDatabaseReadiness>(),
            sp.GetRequiredService<Data.Scripture.BibleQueryOptions>(), sp.GetRequiredService<Data.Scripture.ICitationRecorder>()));
        services.AddSingleton<Data.Scripture.BibleImporter>();
        services.AddSingleton<BibleImportService>();
        services.AddSingleton<IIpcHandler, Handlers.BibleListHandler>();
        services.AddSingleton<IIpcHandler, Handlers.BibleLookupHandler>();
        services.AddSingleton<IIpcHandler, Handlers.BibleChapterHandler>();
        services.AddSingleton<IIpcHandler, Handlers.BibleSearchHandler>();
        services.AddSingleton<IIpcHandler, Handlers.BibleHistoryHandler>();
        services.AddSingleton<IIpcHandler, Handlers.BibleSuggestHandler>();
        services.AddSingleton<IIpcHandler, Handlers.BibleSetAttributionHandler>();
        services.AddSingleton<IIpcHandler, Handlers.BibleImportHandler>();
        services.AddSingleton<IIpcHandler, Handlers.BibleGetSettingsHandler>();
        services.AddSingleton<IIpcHandler, Handlers.BibleSetSettingsHandler>();

        // Canciones (protocolo v12): secciones, orden, copias previas y búsqueda. Las escrituras van por el carril lento; ningún registro lleva letra.
        services.AddSingleton<Data.Repositories.ISongRepository, Data.Songs.SongRepository>();
        services.AddSingleton<IIpcHandler, Handlers.SongSearchHandler>();
        services.AddSingleton<IIpcHandler, Handlers.SongListHandler>();
        services.AddSingleton<IIpcHandler, Handlers.SongGetHandler>();
        services.AddSingleton<IIpcHandler, Handlers.SongParseTextHandler>();
        services.AddSingleton<IIpcHandler, Handlers.SongHistoryHandler>();
        services.AddSingleton<IIpcHandler, Handlers.SongGetSettingsHandler>();
        services.AddSingleton<IIpcHandler, Handlers.SongSetSettingsHandler>();
        services.AddSingleton<IIpcHandler, Handlers.SongSaveHandler>();
        services.AddSingleton<IIpcHandler, Handlers.SongRestoreRevisionHandler>();
        services.AddSingleton<IIpcHandler, Handlers.SongDeleteHandler>();
        services.AddSingleton<Data.Repositories.IPlaylistRepository, Data.Playlists.PlaylistRepository>();
        services.AddSingleton<IIpcHandler, Handlers.PlaylistListHandler>();
        services.AddSingleton<IIpcHandler, Handlers.PlaylistGetHandler>();
        services.AddSingleton<IIpcHandler, Handlers.PlaylistGetSettingsHandler>();
        services.AddSingleton<IIpcHandler, Handlers.PlaylistSetSettingsHandler>();
        services.AddSingleton<IIpcHandler, Handlers.PlaylistCreateHandler>();
        services.AddSingleton<IIpcHandler, Handlers.PlaylistUpdateHandler>();
        services.AddSingleton<IIpcHandler, Handlers.PlaylistAddItemHandler>();
        services.AddSingleton<IIpcHandler, Handlers.PlaylistMoveItemHandler>();
        services.AddSingleton<IIpcHandler, Handlers.PlaylistRemoveItemHandler>();
        services.AddSingleton<IIpcHandler, Handlers.PlaylistUpdateItemHandler>();
        services.AddSingleton<IIpcHandler, Handlers.PlaylistDuplicateHandler>();
        services.AddSingleton<IIpcHandler, Handlers.PlaylistDeleteHandler>();
        // Alertas rápidas (protocolo v15): mostrar y quitar solo tocan memoria (carril de PRIORIDAD); el uso se anota en segundo plano; guardar y borrar van por el carril lento.
        services.AddSingleton<Data.Alerts.IAlertRepository, Data.Alerts.AlertRepository>();
        services.AddSingleton<Data.Alerts.BackgroundAlertRecorder>();
        services.AddSingleton<Data.Alerts.IAlertRecorder>(sp => sp.GetRequiredService<Data.Alerts.BackgroundAlertRecorder>());
        services.AddSingleton<IIpcHandler, Handlers.AlertShowHandler>();
        services.AddSingleton<IIpcHandler, Handlers.AlertHideHandler>();
        services.AddSingleton<IIpcHandler, Handlers.AlertGetSettingsHandler>();
        services.AddSingleton<IIpcHandler, Handlers.AlertSetSettingsHandler>();
        services.AddSingleton<IIpcHandler, Handlers.AlertListHandler>();
        services.AddSingleton<IIpcHandler, Handlers.AlertSaveHandler>();
        services.AddSingleton<IIpcHandler, Handlers.AlertDeleteHandler>();
        // Biblioteca local de medios (protocolo v17): los archivos se COPIAN a <datos>\media (reproducir nunca lee de un USB ni de la red); todo lo que toca disco o BD va por el carril lento.
        services.AddSingleton(new Media.MediaStorage(Path.Combine(paths.DataDirectory, "media")));
        services.AddSingleton<Media.IFreeSpaceProvider, Media.DriveFreeSpaceProvider>();
        services.AddSingleton<Media.IMediaSourceOpener, Media.FileSourceOpener>();
        services.AddSingleton<Data.Media.IMediaRepository, Data.Media.MediaRepository>();
        services.AddSingleton<Media.MediaImportService>();
        services.AddSingleton<Media.MediaLibraryMaintenance>();
        services.AddSingleton<IIpcHandler, Handlers.MediaListHandler>();
        services.AddSingleton<IIpcHandler, Handlers.MediaGetHandler>();
        services.AddSingleton<IIpcHandler, Handlers.MediaImportHandler>();
        services.AddSingleton<IIpcHandler, Handlers.MediaCancelImportHandler>();
        services.AddSingleton<IIpcHandler, Handlers.MediaRenameHandler>();
        services.AddSingleton<IIpcHandler, Handlers.MediaMoveHandler>();
        services.AddSingleton<IIpcHandler, Handlers.MediaDeleteHandler>();
        services.AddSingleton<IIpcHandler, Handlers.MediaCreateFolderHandler>();
        services.AddSingleton<IIpcHandler, Handlers.MediaRenameFolderHandler>();
        services.AddSingleton<IIpcHandler, Handlers.MediaDeleteFolderHandler>();
        services.AddSingleton<IIpcHandler, Handlers.MediaReportProbeHandler>();
        services.AddSingleton<IIpcHandler, Handlers.MediaSetThumbnailHandler>();
        services.AddSingleton<IIpcHandler, Handlers.MediaVerifyLibraryHandler>();
        services.AddSingleton<IIpcHandler, Handlers.MediaSetBackgroundHandler>();
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
        var watchdog = _services.GetRequiredService<PanelWatchdog>();
        MainWindow.RegisterWebMessageReceivedHandler((_, message) =>
        {
            try
            {
                watchdog.Touch();             // el JS del panel está vivo (cualquier mensaje cuenta, aunque luego se descarte)
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
                _services.GetRequiredService<PanelWatchdog>().Start();

                // Atajos globales: NO pasan por el JavaScript del panel, así que funcionan aunque esté atascado. Cada uno se registra por separado;
                // los que no se puedan registrar (otra aplicación usa la combinación) se informan y el panel avisa.
                var statuses = _services.GetRequiredService<GlobalHotkeys>().Start(
                [
                    new HotkeyBinding("emergency", 0x4D, "Ctrl+Alt+Shift+M", () => output.EmergencyPause(OakProyektor.Core.Output.OutputPauseReason.Emergency)),
                    new HotkeyBinding("black", 0x42, "Ctrl+Alt+Shift+B", () => output.ToggleMaster(OakProyektor.Core.Output.MasterMode.Black)),
                    new HotkeyBinding("clearText", 0x43, "Ctrl+Alt+Shift+C", () => output.ToggleMaster(OakProyektor.Core.Output.MasterMode.ClearText)),
                    new HotkeyBinding("logo", 0x4C, "Ctrl+Alt+Shift+L", () => output.ToggleMaster(OakProyektor.Core.Output.MasterMode.Logo)),
                    new HotkeyBinding("reloadPanel", 0x52, "Ctrl+Alt+Shift+R", ReloadPanel),
                    // Solo QUITA la alerta, nunca la muestra (un interruptor podría resucitar un aviso viejo). Actúa en el controlador sin pasar por el panel.
                    new HotkeyBinding("hideAlert", 0x41, "Ctrl+Alt+Shift+A", () => output.HideAlert()),
                ]);
                output.SetEmergencyHotkey(statuses.FirstOrDefault(s => s.Action == "emergency" && s.Registered)?.Keys);   // null = no se pudo registrar; el panel lo avisa
                output.SetBackupHotkeys([.. statuses.Where(s => s.Action != "emergency")]);

                await _services.GetRequiredService<DisplayManager>().StartPolling(ct).ConfigureAwait(false);   // dura hasta el apagado
            }
            catch (OperationCanceledException) { /* apagado */ }
            catch (Exception ex) { _logger.LogError(ex, "Falló el arranque de los servicios de fondo"); }
        }, CancellationToken.None);
        // La BD se inicializa en segundo plano, pero su resultado es una SEÑAL (IDatabaseReadiness): los repositorios la esperan y un
        // fallo llega al panel como evento (DatabaseStatusReporter). StartAsync no lanza: el fallo queda en el estado.
        _ = _services.GetRequiredService<DatabaseStatusReporter>();   // se suscribe antes de que cambie el estado
        var db = _services.GetRequiredService<DatabaseReadiness>().StartAsync(_services.GetRequiredService<IDatabaseInitializer>(), ct);
        // Respaldo diario: en segundo plano, tras una espera, sin lanzar nunca (todo fallo queda en el log y como aviso del panel).
        var backup = Task.Run(() => _services.GetRequiredService<Data.Backups.DatabaseBackupService>().RunDailyAsync(ct), CancellationToken.None);
        // Biblioteca de medios: al arrancar borra copias a medias y archivos huérfanos y marca los que faltan (en segundo plano, nunca retrasa nada; solo conteos en el registro).
        var media = Task.Run(() => _services.GetRequiredService<Media.MediaLibraryMaintenance>().RunAtStartupAsync(ct), CancellationToken.None);
        _background = [.. pump, startup, db, backup, media];
    }

    private void OnMainClosing()
    {
        _shutdown.Cancel();
        _services.GetRequiredService<InboundQueue>().Complete();
        _services.GetRequiredService<IOutbox>().Complete();
        _services.GetRequiredService<GlobalHotkeys>().Dispose();
        _services.GetRequiredService<IOutputController>().CloseForShutdown();   // sin esperar a las tareas: estamos en el hilo de UI
    }

    private long _lastReloadTick;

    /// <summary>
    /// Atajo global «recargar el panel»: vuelve a cargar la página de control SIN cerrar la salida ni la aplicación. Corre en el hilo de los atajos, así que
    /// la navegación se lanza aparte y al hilo de UI. Sin el candado de ciclo de vida: no toca la salida. Ver CLAUDE.md sobre qué pasa si el renderizador está atascado.
    /// </summary>
    private void ReloadPanel()
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Exchange(ref _lastReloadTick, now) < 1000) return;   // una pulsación repetida no encadena recargas
        if (!File.Exists(_paths.ControlPage)) { _logger.LogError("No se puede recargar el panel: no existe {Page}", _paths.ControlPage); return; }

        var watchdog = _services.GetRequiredService<PanelWatchdog>();
        var unresponsive = watchdog.IsUnresponsive;   // ANTES de Reset(): Reset reinicia el silencio y siempre daría «sano»
        _logger.LogWarning("Recarga del panel pedida por el atajo global (panel {State})", unresponsive ? "SIN RESPUESTA" : "sano");
        _services.GetRequiredService<WindowRegistry>().ResetControlReady();   // no se le envía nada hasta que la página nueva hable (como al arrancar)
        watchdog.Reset();
        _ = Task.Run(() =>
        {
            try
            {
                // Con el renderizador del panel ATASCADO, Load() solo no lo recupera: hay que terminar ese proceso (y solo ese: el panel y la salida tienen
                // perfiles de WebView2 distintos, así que el renderizador del panel se identifica por su carpeta de perfil). Con el panel sano basta recargar.
                if (unresponsive)
                {
                    if (_profile.Directory is { } profile) _logger.LogWarning("Panel sin respuesta: se termina su renderizador ({Count} proceso(s)) y se recarga", PanelRecovery.KillRenderersOf(profile, _logger));
                    else _logger.LogError("Panel sin respuesta, pero usa el perfil predeterminado de WebView2 y no se puede identificar su renderizador: se recarga a secas (puede no recuperarse)");
                }
                _services.GetRequiredService<IUiDispatcher>().Invoke(() => MainWindow.Load(_paths.ControlPage));
            }
            catch (Exception ex) { _logger.LogError(ex, "Falló la recarga del panel"); }
        });
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
