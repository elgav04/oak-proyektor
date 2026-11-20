using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace OakProyektor.App.Cli;

/// <summary>
/// Punto de entrada de las órdenes de consola. El ejecutable es de tipo WinExe (sin consola propia): aquí se engancha a la consola del proceso que lo lanzó
/// (<c>AttachConsole</c>) o usa la salida estándar si ya viene redirigida (<c>dotnet run</c>, una tubería, un archivo). Nunca crea ventanas.
/// </summary>
internal static partial class CliHost
{
    private const int AttachParentProcess = -1;
    private const int StdOutputHandle = -11;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetStdHandle(int standardHandle);

    /// <summary>
    /// Ejecuta la orden y devuelve el código de salida. Síncrono a propósito: es parte del único punto bloqueante documentado (<c>Main</c>), y una orden de
    /// consola no necesita el hilo STA ni ninguna ventana.
    /// </summary>
    public static int Run(string[] args)
    {
        var (output, error) = AttachStreams();
        var (command, problem) = CliArguments.Parse(args);
        if (command is null)
        {
            error.WriteLine("Error: " + problem);
            return CliRunner.BadArguments;
        }
        if (command.Kind == CliCommandKind.Help)
        {
            output.WriteLine(CliArguments.HelpText());
            return CliRunner.Ok;
        }

        ApplicationPaths paths;
        try { paths = ApplicationPaths.Resolve(); }
        catch (Exception ex) { error.WriteLine("Error: no se pudieron determinar las carpetas de la aplicación: " + ex.Message); return CliRunner.Failed; }

        // Escribir en la BD mientras la aplicación gráfica está abierta con la misma carpeta de datos (o lanzar dos importaciones) es lo que la instancia única evita.
        // Las órdenes de solo lectura (mostrar, listar, verificar) no la necesitan.
        SingleInstanceGuard? guard = null;
        if (command.Kind == CliCommandKind.ImportBible)
        {
            guard = SingleInstanceGuard.TryAcquire(SingleInstanceGuard.NameFor(paths.DataDirectory));
            if (guard is null)
            {
                error.WriteLine("Error: Oak-Proyektor (o otra importación) está abierto con esta carpeta de datos. Ciérralo y vuelve a intentarlo.");
                return CliRunner.Failed;
            }
        }

        using (guard)
        {
            var temporaryLogs = Path.Combine(Path.GetTempPath(), ApplicationPaths.ProductFolderName, "logs");
            using var setup = LoggerSetup.Create([paths.LogsDirectory, temporaryLogs]);
            using var loggers = new Serilog.Extensions.Logging.SerilogLoggerFactory(setup.Log, dispose: false);
            return new CliRunner(paths, loggers, output, error).RunAsync(command, CancellationToken.None).GetAwaiter().GetResult();
        }
    }

    private static (TextWriter Output, TextWriter Error) AttachStreams()
    {
        try
        {
            var handle = GetStdHandle(StdOutputHandle);
            if (handle == 0 || handle == -1) AttachConsole(AttachParentProcess);   // sin salida estándar heredada: usar la consola del padre, si la hay
        }
        catch (Exception) { /* sin consola: se escribe donde se pueda */ }

        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        try { Console.OutputEncoding = utf8; } catch (IOException) { /* sin consola real (redirigido): no hace falta */ }
        return (new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true },
                new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });
    }
}
