using System.Globalization;
using Microsoft.Extensions.Logging;
using OakProyektor.Core.Scripture;
using OakProyektor.Core.Songs;
using OakProyektor.Data;
using OakProyektor.Data.Backups;
using OakProyektor.Data.Scripture;
using OakProyektor.Data.Songs;

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
            // Los respaldos NO pasan por la inicialización de la BD: restaurar debe funcionar justo cuando la actual está dañada o no migra.
            if (command.Kind == CliCommandKind.ListBackups) return ListBackups(options);
            if (command.Kind == CliCommandKind.RestoreBackup) return await RestoreAsync(command, options, cancellationToken).ConfigureAwait(false);

            // Las órdenes de SOLO LECTURA nunca migran ni crean la base: si falta o es de otra versión, error claro y se abre en solo lectura.
            if (command.Kind is CliCommandKind.ListBibles or CliCommandKind.ShowVerse or CliCommandKind.VerifyBible)
            {
                if (await ReadOnlyDatabase.CheckAsync(options, paths.DataDirectory, cancellationToken).ConfigureAwait(false) is { } problem)
                {
                    error.WriteLine("Error: " + problem);
                    return Failed;
                }
                var readOnly = new ReadOnlySqliteConnectionFactory(options);
                var ready = new AlwaysReady();
                return command.Kind switch
                {
                    CliCommandKind.VerifyBible => await VerifyAsync(command, readOnly, ready, cancellationToken).ConfigureAwait(false),
                    CliCommandKind.ShowVerse => await ShowVerseAsync(command, readOnly, ready, cancellationToken).ConfigureAwait(false),
                    _ => await ListAsync(readOnly, ready, cancellationToken).ConfigureAwait(false),
                };
            }

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
                CliCommandKind.ImportSong => await ImportSongAsync(command, connections, readiness, cancellationToken).ConfigureAwait(false),
                CliCommandKind.VerifyBible => await VerifyAsync(command, connections, readiness, cancellationToken).ConfigureAwait(false),
                CliCommandKind.ShowVerse => await ShowVerseAsync(command, connections, readiness, cancellationToken).ConfigureAwait(false),
                CliCommandKind.ListBibles => await ListAsync(connections, readiness, cancellationToken).ConfigureAwait(false),
                _ => BadArguments,
            };
        }
        catch (BackupException ex)
        {
            error.WriteLine("Error: " + ex.Message);
            return Failed;
        }
        catch (BibleException ex)
        {
            error.WriteLine($"Error [{ex.Code}]: {ex.Message}");
            return Failed;
        }
        catch (SongException ex)
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

    private int ListBackups(DatabaseOptions options)
    {
        var backups = BackupCatalog.List(options.BackupDirectory);
        output.WriteLine($"Carpeta de respaldos: {options.BackupDirectory}");
        if (backups.Count == 0) { output.WriteLine("No hay copias."); return Ok; }
        foreach (var group in backups.GroupBy(b => b.Kind))
        {
            output.WriteLine($"{BackupCatalog.Describe(group.Key)} (se conservan las últimas {BackupCatalog.Retention(group.Key)}):");
            foreach (var b in group)
                output.WriteLine($"  {b.Name}\t{b.LastWriteLocal.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}\t{(b.Bytes / 1024.0 / 1024.0).ToString("0.0", CultureInfo.InvariantCulture)} MB");
        }
        output.WriteLine("Para restaurar: --restore-backup <nombre> (con la aplicación cerrada).");
        return Ok;
    }

    private async Task<int> RestoreAsync(CliCommand c, DatabaseOptions options, CancellationToken ct)
    {
        var source = BackupCatalog.Resolve(options.BackupDirectory, c.Get("restore-backup")!);
        output.WriteLine($"Restaurando {source}");
        output.WriteLine($"Base actual: {options.DatabasePath}");
        var result = await new DatabaseRestorer(options, logger: loggers.CreateLogger<DatabaseRestorer>()).RestoreAsync(source, ct).ConfigureAwait(false);
        output.WriteLine($"Copia comprobada (PRAGMA integrity_check: {result.Verification.Detail}; esquema {result.Verification.SchemaVersion}).");
        output.WriteLine(result.PreviousSavedAs is null ? "No había una base anterior." : $"La base anterior se guardó en: {result.PreviousSavedAs}");
        output.WriteLine("Restauración terminada. Al abrir la aplicación se aplicarán las migraciones pendientes, si las hay.");
        return Ok;
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

    /// <summary>
    /// Importa el .txt de una canción como una canción NUEVA. Muestra qué codificación se detectó, los avisos del parser y los conteos; NUNCA la letra (ni siquiera el título:
    /// solo el identificador). Si hay errores del parser (límites, orden vacío…) no guarda nada.
    /// </summary>
    private async Task<int> ImportSongAsync(CliCommand c, ISqliteConnectionFactory connections, IDatabaseReadiness readiness, CancellationToken ct)
    {
        var path = Path.GetFullPath(c.Get("import-song")!);
        output.WriteLine($"Importando una canción desde {path}");
        output.WriteLine($"Carpeta de datos: {paths.DataDirectory}");

        var decoded = await SongTextFile.ReadAsync(path, ct).ConfigureAwait(false);
        output.WriteLine($"Codificación detectada: {decoded.Encoding}{(decoded.FellBack ? " (el archivo no era UTF-8 válido)" : "")}");
        foreach (var warning in decoded.Warnings) output.WriteLine("  Aviso: " + warning);

        var parsed = SongTextParser.Parse(decoded.Text, new SongParseOptions(MergeIdenticalBlocks: !c.Has("no-merge")));
        output.WriteLine($"Formato del texto: {SongIpcFormat(parsed.Format)}; {parsed.Sections.Count} sección(es), orden de {parsed.Arrangement.Count} elemento(s).");
        foreach (var warning in parsed.Warnings) output.WriteLine("  Aviso: " + warning);
        if (parsed.Errors.Count > 0)
        {
            foreach (var problem in parsed.Errors) error.WriteLine("  Error: " + problem);
            error.WriteLine("No se guardó nada.");
            return Failed;
        }
        if (parsed.Sections.Count == 0)
        {
            error.WriteLine("Error: el archivo no contiene ninguna sección con texto. No se guardó nada.");
            return Failed;
        }

        var title = c.Get("title") ?? Path.GetFileNameWithoutExtension(path);
        var repository = new SongRepository(connections, readiness, loggers.CreateLogger<SongRepository>());
        var result = await repository.SaveAsync(new SongSaveRequest(
            null, null, title, c.Get("author"), c.Get("copyright"), c.Get("ccli"), null, null,
            [.. parsed.Sections.Select(s => new SongSectionInput(null, s.Label, s.Kind, s.Text))], parsed.Arrangement, SongSaveReason.Import), ct).ConfigureAwait(false);
        output.WriteLine($"Canción creada: id {result.SongId.ToString(CultureInfo.InvariantCulture)}.");
        return Ok;
    }

    private static string SongIpcFormat(SongTextFormat format) => format switch
    {
        SongTextFormat.Brackets => "etiquetas entre corchetes",
        SongTextFormat.AliasLines => "líneas con nombre de sección",
        SongTextFormat.Blocks => "bloques separados por líneas en blanco",
        _ => "vacío",
    };

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
