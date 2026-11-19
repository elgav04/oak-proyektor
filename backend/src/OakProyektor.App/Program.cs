namespace OakProyektor.App;

internal static class Program
{
    // WebView2 exige un hilo STA: sin [STAThread] la ventana queda negra sin error (verificado con un spike).
    // Main NO puede ser `async`: Roslyn no copia [STAThread] al punto de entrada sintetizado y el hilo quedaría MTA.
    // Por eso este es el ÚNICO punto bloqueante del programa (GetResult); todo lo demás es asíncrono. La parte síncrona de
    // RunAsync (hasta su primer await) sigue en el hilo STA, que es donde corre el bucle de ventanas.
    [STAThread]
    private static int Main() => RunAsync().GetAwaiter().GetResult();

    private static async Task<int> RunAsync()
    {
        var temporaryLogs = Path.Combine(Path.GetTempPath(), ApplicationPaths.ProductFolderName, "logs");

        // M14: la resolución de rutas va dentro de un try con respaldo; si falla, queda registro en una carpeta temporal y un aviso.
        ApplicationPaths paths;
        try
        {
            paths = ApplicationPaths.Resolve();
        }
        catch (Exception ex)
        {
            using var fallback = LoggerSetup.Create([temporaryLogs]);
            fallback.Log.Fatal(ex, "No se pudieron determinar las carpetas de la aplicación");
            StartupFailure.Show("No se pudieron determinar las carpetas de la aplicación: " + ex.Message, fallback.Directory);
            return 1;
        }

        // I11: instancia única (por carpeta de datos). Va ANTES de crear el log: abrirlo chocaría con el archivo de la primera instancia.
        using var guard = SingleInstanceGuard.TryAcquire(SingleInstanceGuard.NameFor(paths.DataDirectory));
        if (guard is null)
        {
            await ExistingInstance.TryBringToFrontAsync(ApplicationHost.ControlWindowTitle, Environment.ProcessPath).ConfigureAwait(false);
            return 0;
        }

        // M14: el logger se crea con respaldo (carpeta de datos → carpeta temporal → silencioso); nunca impide arrancar.
        using var setup = LoggerSetup.Create([paths.LogsDirectory, temporaryLogs]);
        var log = setup.Log;

        // Un hilo de fondo con excepción no controlada termina el proceso en .NET y no se puede evitar aquí:
        // por eso todo trabajo de fondo y todo callback de Photino captura sus excepciones. Esto solo deja constancia.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            log.Fatal(e.ExceptionObject as Exception, "Excepción no controlada (terminating={Terminating})", e.IsTerminating);
            setup.Dispose();   // vaciar el archivo antes de que el proceso muera
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            log.Error(e.Exception, "Excepción de tarea no observada");
            e.SetObserved();   // registrar y seguir: no tumbar la proyección
        };

        try
        {
            await using var host = new ApplicationHost(paths, log);
            return host.Run();   // bloquea (en el hilo STA) hasta que se cierra el panel; después se apaga de forma asíncrona
        }
        catch (Exception ex)
        {
            log.Fatal(ex, "Fallo al arrancar la aplicación");
            StartupFailure.Show("La aplicación no pudo iniciar: " + ex.Message, setup.Directory);
            return 1;
        }
    }
}