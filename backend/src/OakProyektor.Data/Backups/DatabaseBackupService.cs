using System.Globalization;
using Dapper;
using Microsoft.Extensions.Logging;

namespace OakProyektor.Data.Backups;

/// <summary>Lo que el panel necesita saber de los respaldos: avisos para mostrar y una señal de que el trabajo de arranque terminó (para no responder «sin avisos» demasiado pronto).</summary>
public interface IBackupStatus
{
    IReadOnlyList<string> Warnings { get; }
    Task Finished { get; }
}

public enum DailyBackupOutcome { NotRun, Created, AlreadyDone, Failed }

/// <param name="Path">La copia hecha (o la que ya existía hoy).</param>
public sealed record DailyBackupResult(DailyBackupOutcome Outcome, string? Path, BackupVerification? Verification, IReadOnlyList<string> Pruned);

/// <summary>
/// Respaldo diario automático de la base de datos. Al arrancar la aplicación y COMO MUCHO UNA VEZ AL DÍA hace una copia consistente (<c>VACUUM INTO</c>) en la carpeta de respaldos
/// (<see cref="DatabaseOptions.BackupDirectory"/>: <c>data/backups</c> en desarrollo y <c>%LOCALAPPDATA%\Oak-Proyektor\backups</c> en producción), la comprueba con
/// <c>PRAGMA integrity_check</c> y conserva las últimas 14 (<see cref="BackupCatalog.Retention"/>), cada clase de copia con su propia retención.
/// <para><b>Nunca bloquea ni tumba nada</b>: corre en segundo plano (tras una pequeña espera para no competir con el arranque de las ventanas), usa su propia conexión (en modo WAL no
/// bloquea a los escritores), captura todas sus excepciones, las registra y las convierte en avisos para el panel. Si una copia no pasa la comprobación se BORRA (parecería una copia
/// válida y no lo es) y el día no queda cubierto: se intenta de nuevo en el siguiente arranque.</para>
/// </summary>
public sealed class DatabaseBackupService(ISqliteConnectionFactory connections, DatabaseOptions options, IDatabaseReadiness readiness, TimeProvider time, ILogger<DatabaseBackupService> logger) : IBackupStatus
{
    /// <summary>Espera antes de copiar, para que el arranque de la interfaz y de la salida no compita con el disco.</summary>
    public static readonly TimeSpan DefaultStartDelay = TimeSpan.FromSeconds(10);

    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<string> _warnings = [];
    private readonly object _gate = new();

    public IBackupVerifier Verifier { get; init; } = new SqliteBackupVerifier();
    public TimeSpan StartDelay { get; init; } = DefaultStartDelay;
    /// <summary>Raíces de carpetas sincronizadas (por defecto, las variables de entorno de OneDrive); se sustituye en las pruebas.</summary>
    public Func<IEnumerable<string?>> SyncRoots { get; init; } = SyncedFolders.EnvironmentRoots;

    public DailyBackupResult LastResult { get; private set; } = new(DailyBackupOutcome.NotRun, null, null, []);
    public Task Finished => _finished.Task;
    public IReadOnlyList<string> Warnings { get { lock (_gate) return [.. _warnings]; } }

    private void Warn(string message)
    {
        lock (_gate) _warnings.Add(message);
    }

    /// <summary>Hace el respaldo del día (si falta) en segundo plano. NUNCA lanza: todo fallo queda registrado y como aviso.</summary>
    public async Task RunDailyAsync(CancellationToken cancellationToken)
    {
        try
        {
            CheckSyncedFolders();   // barato y antes de la espera: el panel pide los avisos nada más cargar
            if (StartDelay > TimeSpan.Zero) await Task.Delay(StartDelay, time, cancellationToken).ConfigureAwait(false);
            LastResult = await BackUpAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* apagado: no es un fallo */ }
        catch (Exception ex)
        {
            // Lo que no se esperaba (disco lleno, permisos, ruta imposible…): se registra y se avisa; la aplicación sigue.
            logger.LogError(ex, "Falló el respaldo diario de la base de datos");
            Warn($"No se pudo hacer el respaldo diario de la base de datos ({ex.Message}). Revisa el espacio en disco y los permisos de {options.BackupDirectory}.");
            LastResult = new DailyBackupResult(DailyBackupOutcome.Failed, null, null, []);
        }
        finally { _finished.TrySetResult(); }
    }

    private void CheckSyncedFolders()
    {
        try
        {
            var roots = SyncRoots().ToList();
            var data = Path.GetDirectoryName(Path.GetFullPath(options.DatabasePath))!;
            if (SyncedFolders.Find(data, roots) is { } provider)
            {
                logger.LogWarning("La carpeta de datos {Data} está dentro de una carpeta sincronizada ({Provider})", data, provider);
                Warn($"La carpeta de datos ({data}) está dentro de una carpeta sincronizada por {provider}. Un archivo SQLite abierto y sincronizado puede dañarse o dejar copias en conflicto: " +
                     "muévela fuera de la sincronización (variable OAK_DATA_DIR) o excluye la carpeta de datos de la sincronización. Los respaldos diarios ayudan, pero no sustituyen a eso.");
            }
        }
        catch (Exception ex) { logger.LogWarning(ex, "No se pudo comprobar si la carpeta de datos está sincronizada"); }
    }

    private async Task<DailyBackupResult> BackUpAsync(CancellationToken ct)
    {
        var directory = options.BackupDirectory;
        var day = DateOnly.FromDateTime(time.GetLocalNow().DateTime);
        var final = Path.Combine(directory, BackupCatalog.DailyName(day));
        if (File.Exists(final))
        {
            logger.LogInformation("El respaldo de hoy ya existe: {Path}", final);
            return new DailyBackupResult(DailyBackupOutcome.AlreadyDone, final, null, PruneAll(directory));
        }

        Directory.CreateDirectory(directory);
        var partial = final + ".partial";
        DeleteQuietly(partial);   // restos de un intento anterior interrumpido

        try
        {
            // La conexión espera a que la base esté lista (y falla con un mensaje claro si la inicialización falló: entonces no hay nada fiable que copiar).
            await using (var connection = await connections.OpenWhenReadyAsync(readiness, ct).ConfigureAwait(false))
                await connection.ExecuteAsync(new CommandDefinition("VACUUM INTO @path;", new { path = partial }, cancellationToken: ct)).ConfigureAwait(false);

            var verification = await Verifier.VerifyAsync(partial, ct).ConfigureAwait(false);
            if (!verification.Ok)
            {
                DeleteQuietly(partial);
                logger.LogError("El respaldo diario NO pasó PRAGMA integrity_check: {Detail}", verification.Detail);
                Warn($"El respaldo diario no pasó la comprobación de integridad (PRAGMA integrity_check): {verification.Detail}. La base de datos podría estar dañada: haz una copia de la carpeta de datos antes de seguir.");
                return new DailyBackupResult(DailyBackupOutcome.Failed, null, verification, []);
            }

            File.Move(partial, final, overwrite: false);   // solo con la copia ya verificada aparece con su nombre definitivo
            logger.LogInformation("Respaldo diario hecho y verificado: {Path} ({Bytes} bytes)", final, new FileInfo(final).Length);
            return new DailyBackupResult(DailyBackupOutcome.Created, final, verification, PruneAll(directory));
        }
        catch (Exception)
        {
            DeleteQuietly(partial);   // una copia a medias no debe quedar
            throw;
        }
    }

    /// <summary>Aplica la retención de CADA clase (la de las diarias es 14; las demás tienen la suya y no se mezclan).</summary>
    private List<string> PruneAll(string directory)
    {
        var pruned = new List<string>();
        foreach (var kind in Enum.GetValues<BackupKind>()) pruned.AddRange(BackupCatalog.Prune(directory, kind, logger));
        if (pruned.Count > 0) logger.LogInformation("Respaldos antiguos eliminados por la retención: {Count}", pruned.Count);
        return pruned;
    }

    private void DeleteQuietly(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger.LogWarning(ex, "No se pudo borrar {Path}", path); }
    }

    /// <summary>Una descripción corta de la fecha del respaldo de hoy, para los mensajes de consola.</summary>
    public static string DayLabel(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
