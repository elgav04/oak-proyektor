using System.Globalization;
using Microsoft.Extensions.Logging;
using OakProyektor.Core.Scripture;
using OakProyektor.Data;
using OakProyektor.Data.Scripture;

namespace OakProyektor.App.Cli;

/// <summary>
/// Ejecuta las órdenes de consola sobre la BD de la carpeta de datos, SIN abrir ventanas. Escribe en <paramref name="output"/> y los errores en <paramref name="error"/>.
/// Códigos de salida: 0 correcto; 1 falló; 2 argumentos incorrectos; 3 importó pero el índice de búsqueda falló su comprobación.
/// Ningún informe lleva texto bíblico; solo <c>--show-verse</c> lo muestra (es su propósito) y solo en <paramref name="output"/>, nunca en el log.
/// </summary>
internal sealed class CliRunner(ApplicationPaths paths, ILoggerFactory loggers, TextWriter output, TextWriter error)
{
    public const int Ok = 0, Failed = 1, BadArguments = 2, FtsFailed = 3;

    public async Task<int> RunAsync(CliCommand command, CancellationToken cancellationToken)
    {
        try
        {
            if (command.Kind == CliCommandKind.Help) { output.WriteLine(CliArguments.HelpText()); return Ok; }

            var options = DatabaseOptions.ForDataDirectory(paths.DataDirectory);
            var connections = new SqliteConnectionFactory(options);
            var readiness = new DatabaseReadiness(loggers.CreateLogger<DatabaseReadiness>());
            var initializer = new DatabaseInitializer(options, connections, loggers.CreateLogger<DatabaseInitializer>(), loggers);
            await readiness.StartAsync(initializer, cancellationToken).ConfigureAwait(false);
            if (readiness.Status.State == OakProyektor.Core.Database.DatabaseState.Failed)
            {
                error.WriteLine("Error: la base de datos no se pudo preparar: " + readiness.Status.Message);
                if (readiness.Status.BackupPath is { } backup) error.WriteLine("Copia previa a la migración: " + backup);
                return Failed;
            }

            return command.Kind switch
            {
                CliCommandKind.ImportBible => await ImportAsync(command, connections, readiness, options.BackupDirectory, cancellationToken).ConfigureAwait(false),
                CliCommandKind.VerifyBible => await VerifyAsync(command, connections, readiness, cancellationToken).ConfigureAwait(false),
                CliCommandKind.ShowVerse => await ShowVerseAsync(command, connections, readiness, cancellationToken).ConfigureAwait(false),
                CliCommandKind.ListBibles => await ListAsync(connections, readiness, cancellationToken).ConfigureAwait(false),
                _ => BadArguments,
            };
        }
        catch (BibleException ex)
        {
            error.WriteLine($"Error [{ex.Code}]: {ex.Message}");
            return Failed;
        }
        catch (DatabaseUnavailableException ex)
        {
            error.WriteLine("Error: " + ex.Message);
            return Failed;
        }
        catch (OperationCanceledException)
        {
            error.WriteLine("Cancelado. No se cambió nada.");
            return Failed;
        }
        catch (Exception ex)
        {
            loggers.CreateLogger<CliRunner>().LogError(ex, "Falló la orden de consola {Command}", command.Kind);
            error.WriteLine("Error inesperado: " + ex.GetType().Name + ". Revisa el registro en " + paths.LogsDirectory);
            return Failed;
        }
    }

    private async Task<int> ImportAsync(CliCommand c, ISqliteConnectionFactory connections, IDatabaseReadiness readiness, string backupDirectory, CancellationToken ct)
    {
        var request = new BibleImportRequest(
            Path.GetFullPath(c.Get("import-bible")!), c.Get("abbr")!, c.Get("name")!, c.Get("lang")!,
            c.Get("format"), c.Get("numbering"), c.Get("attribution"), c.Has("allow-large-deletion"), c.Has("allow-large-change"), backupDirectory);
        output.WriteLine($"Importando «{request.Abbreviation}» desde {request.Path}");
        output.WriteLine($"Carpeta de datos: {paths.DataDirectory}");

        var importer = new BibleImporter(connections, readiness, loggers.CreateLogger<BibleImporter>());
        var report = await importer.ImportAsync(request, new ConsoleProgress(output), ct).ConfigureAwait(false);
        CliReportWriter.Write(report, output);
        return report.FtsIntegrityOk ? Ok : FtsFailed;
    }

    private async Task<int> VerifyAsync(CliCommand c, ISqliteConnectionFactory connections, IDatabaseReadiness readiness, CancellationToken ct)
    {
        var checker = new BibleCrossChecker(connections, readiness);
        var report = await checker.CompareAsync(c.Get("abbr")!, Path.GetFullPath(c.Get("against")!), c.Get("against-format"), BibleValidator.MaxListedReferences, ct).ConfigureAwait(false);
        CliReportWriter.Write(report, output);
        return Ok;
    }

    /// <summary>Muestra el texto de un pasaje en la consola (para comparar con la Biblia impresa). No lo anota en el historial ni lo escribe en el log.</summary>
    private async Task<int> ShowVerseAsync(CliCommand c, ISqliteConnectionFactory connections, IDatabaseReadiness readiness, CancellationToken ct)
    {
        var passage = await new BibleQueryService(connections, readiness).LookupAsync(c.Get("show-verse")!, c.Get("bible"), recordHistory: false, ct).ConfigureAwait(false);
        output.WriteLine($"{passage.Reference} — {passage.Bible}");
        foreach (var verse in passage.Verses)
            output.WriteLine($"{verse.Chapter.ToString(CultureInfo.InvariantCulture)}:{verse.VerseNumber.ToString(CultureInfo.InvariantCulture)}  {verse.Text}");
        if (!string.IsNullOrWhiteSpace(passage.Attribution)) output.WriteLine($"[{passage.Attribution}]");
        return Ok;
    }

    private async Task<int> ListAsync(ISqliteConnectionFactory connections, IDatabaseReadiness readiness, CancellationToken ct)
    {
        var bibles = await new BibleQueryService(connections, readiness).ListBiblesAsync(ct).ConfigureAwait(false);
        if (bibles.Count == 0) { output.WriteLine("No hay Biblias instaladas."); return Ok; }
        foreach (var b in bibles)
            output.WriteLine($"{b.Abbreviation}\t{b.Name}\t{b.Language}\t{b.Verses} versículos\tperfil: {b.NumberingProfile ?? "none"}\taviso de derechos: {(b.Attribution.Length > 0 ? "sí" : "vacío")}");
        return Ok;
    }

    /// <summary>Avance en la consola: una línea al empezar cada fase y luego cada 10 %; solo fase y conteos.</summary>
    private sealed class ConsoleProgress(TextWriter output) : IProgress<ImportProgress>
    {
        private string _phase = "";
        private int _lastDecile = -1;

        public void Report(ImportProgress p)
        {
            var decile = p.Total <= 0 ? 10 : p.Done * 10 / p.Total;
            if (p.Phase != _phase) { _phase = p.Phase; _lastDecile = -1; }
            if (decile <= _lastDecile) return;
            _lastDecile = decile;
            output.WriteLine($"  [{Label(p.Phase)}] {p.Done}/{p.Total}");
        }

        private static string Label(string phase) => phase switch
        {
            "reading" => "leyendo", "cleaning" => "limpiando", "saving" => "guardando", "validating" => "validando", _ => phase,
        };
    }
}
