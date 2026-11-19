using Serilog;
using Serilog.Events;

namespace OakProyektor.App;

/// <summary>Resultado de crear el logger. <see cref="Directory"/> es null si no se pudo escribir en ninguna carpeta (log silencioso).</summary>
internal sealed record LoggerSetup(ILogger Log, IDisposable? Resource, string? Directory, IReadOnlyList<string> Failures) : IDisposable
{
    public void Dispose() => Resource?.Dispose();

    /// <summary>
    /// Crea el logger a archivo rotativo en la primera carpeta de <paramref name="candidateDirectories"/> donde se pueda escribir
    /// (hallazgo M14): la de datos y, de respaldo, una temporal. Si ninguna sirve, devuelve un logger silencioso: la aplicación arranca
    /// igualmente, porque un fallo del log no debe impedir la proyección. Nunca lanza.
    /// </summary>
    public static LoggerSetup Create(IReadOnlyList<string> candidateDirectories)
    {
        var failures = new List<string>();
        foreach (var directory in candidateDirectories)
        {
            try
            {
                // Serilog.Sinks.File NO lanza si la carpeta no es escribible: abre el archivo al primer mensaje y el error se pierde en silencio.
                // Por eso se comprueba la escritura de verdad (y se crea la carpeta) ANTES de confiar en esta carpeta.
                WritableDirectory.Ensure(directory);
                var logger = new LoggerConfiguration()
                    .MinimumLevel.Information()
                    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                    .WriteTo.File(Path.Combine(directory, "oak-proyektor-.log"),
                        rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14,
                        fileSizeLimitBytes: 10_000_000, rollOnFileSizeLimit: true,
                        flushToDiskInterval: TimeSpan.FromSeconds(1))
                    .CreateLogger();
                if (failures.Count > 0)
                    logger.Warning("No se pudo escribir el log en {Failed}; se usa {Used}", string.Join(" | ", failures), directory);
                return new LoggerSetup(logger, logger, directory, failures);
            }
            catch (Exception ex)
            {
                failures.Add($"{directory}: {ex.Message}");
            }
        }
        return new LoggerSetup(Serilog.Core.Logger.None, null, null, failures);
    }

}
