using Serilog;
using Serilog.Events;

namespace OakProyektor.App;

internal static class Program
{
    // WebView2 exige un hilo STA: sin [STAThread] la ventana queda negra sin error (verificado con un spike).
    [STAThread]
    private static int Main()
    {
        var paths = ApplicationPaths.Resolve();
        using var log = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .WriteTo.File(Path.Combine(paths.LogsDirectory, "oak-proyektor-.log"),
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14,
                fileSizeLimitBytes: 10_000_000, rollOnFileSizeLimit: true,
                flushToDiskInterval: TimeSpan.FromSeconds(1))
            .CreateLogger();

        // Un hilo de fondo con excepción no controlada termina el proceso en .NET y no se puede evitar aquí:
        // por eso todo trabajo de fondo y todo callback de Photino captura sus excepciones. Esto solo deja constancia.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            log.Fatal(e.ExceptionObject as Exception, "Excepción no controlada (terminating={Terminating})", e.IsTerminating);
            log.Dispose();   // vaciar el archivo antes de que el proceso muera
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            log.Error(e.Exception, "Excepción de tarea no observada");
            e.SetObserved();   // registrar y seguir: no tumbar la proyección
        };

        try
        {
            using var host = new ApplicationHost(paths, log);
            return host.Run();
        }
        catch (Exception ex)
        {
            log.Fatal(ex, "Fallo al arrancar la aplicación");
            return 1;
        }
    }
}
