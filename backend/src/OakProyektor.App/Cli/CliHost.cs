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
    /// consola no necesita el hilo STA ni ninguna ventana. <b>Ctrl+C cancela</b> (no mata el proceso a la fuerza): la orden se interrumpe, la transacción se revierte y se
    /// muestra «Cancelado. No se cambió nada.».
    /// </summary>
    public static int Run(string[] args)
    {
        var (output, error) = AttachStreams();
        ApplicationPaths paths;
        try { paths = ApplicationPaths.Resolve(); }
        catch (Exception ex) { error.WriteLine("Error: no se pudieron determinar las carpetas de la aplicación: " + ex.Message); return CliRunner.Failed; }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;   // no terminar el proceso a la fuerza: dejar que la orden se interrumpa por sí sola y lo diga
            try { cancellation.Cancel(); } catch (ObjectDisposedException) { /* ya terminó */ }
        };
        Console.CancelKeyPress += onCancel;
        try { return Execute(args, paths, output, error, SingleInstanceGuard.TryAcquire, cancellation.Token); }
        finally { Console.CancelKeyPress -= onCancel; }
    }

    /// <summary>
    /// La orden completa sin tocar la consola ni las rutas reales (así se prueba, incluida la regla de la instancia única). <paramref name="tryAcquire"/> toma un
    /// candado con nombre (null = ya lo tiene otro proceso).
    /// </summary>
    internal static int Execute(string[] args, ApplicationPaths paths, TextWriter output, TextWriter error, Func<string, SingleInstanceGuard?> tryAcquire, CancellationToken cancellationToken)
    {
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

        // Escribir en la BD mientras la aplicación gráfica está abierta con la misma carpeta de datos (o lanzar dos importaciones) es lo que la instancia única evita.
        // Las órdenes de solo lectura (mostrar, listar, verificar) no la necesitan: abren la base en solo lectura.
        SingleInstanceGuard? guard = null, marker = null;
        if (command.Kind is CliCommandKind.ImportBible or CliCommandKind.RestoreBackup)
        {
            guard = tryAcquire(SingleInstanceGuard.NameFor(paths.DataDirectory));
            if (guard is null)
            {
                error.WriteLine("Error: Oak-Proyektor (u otra orden de escritura) está abierto con esta carpeta de datos. Ciérralo (o espera a que termine la otra orden) y vuelve a intentarlo. No se cambió nada.");
                return CliRunner.Failed;
            }
            // Marca visible para la aplicación gráfica: si se abre ahora, explica que hay una orden en curso en vez de decir que está colgada.
            marker = tryAcquire(SingleInstanceGuard.ConsoleMarkerNameFor(paths.DataDirectory));
        }

        using (guard)
        using (marker)
        {
            var temporaryLogs = Path.Combine(Path.GetTempPath(), ApplicationPaths.ProductFolderName, "logs");
            // Una orden de solo lectura sobre una carpeta de datos que NO existe (una carpeta equivocada) no la crea ni para dejar su registro: usa la carpeta temporal.
            var logDirectories = Directory.Exists(paths.DataDirectory) ? new[] { paths.LogsDirectory, temporaryLogs } : new[] { temporaryLogs };
            using var setup = LoggerSetup.Create(logDirectories);
            using var loggers = new Serilog.Extensions.Logging.SerilogLoggerFactory(setup.Log, dispose: false);
            return new CliRunner(paths, loggers, output, error).RunAsync(command, cancellationToken).GetAwaiter().GetResult();
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
