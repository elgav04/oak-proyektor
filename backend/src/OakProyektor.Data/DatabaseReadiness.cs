using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using OakProyektor.Core.Database;

namespace OakProyektor.Data;

/// <summary>La BD no está disponible porque su inicialización falló (o se canceló). El mensaje es apto para el operador.</summary>
public sealed class DatabaseUnavailableException(string message, Exception? inner = null) : InvalidOperationException(message, inner);

/// <summary>
/// Disponibilidad de la BD (hallazgo I9). La inicialización corre en segundo plano; todo repositorio debe esperar a esta señal
/// antes de consultar (<see cref="DatabaseReadinessExtensions.OpenWhenReadyAsync"/>), y quien quiera avisar al operador se suscribe a
/// <see cref="Changed"/>.
/// </summary>
public interface IDatabaseReadiness
{
    DatabaseStatus Status { get; }

    /// <summary>Se dispara en cada cambio de <see cref="Status"/> (Initializing → Ready | Failed).</summary>
    event Action<DatabaseStatus>? Changed;

    /// <summary>Termina cuando la BD está lista. Lanza <see cref="DatabaseUnavailableException"/> si la inicialización falló.</summary>
    Task WhenReadyAsync(CancellationToken cancellationToken);
}

public sealed class DatabaseReadiness(ILogger<DatabaseReadiness> logger) : IDatabaseReadiness
{
    private readonly object _lock = new();
    // La señal se completa SIEMPRE con un resultado (nunca con una tarea fallida): así no puede haber una excepción de tarea no observada
    // aunque nadie espere, y quien espere recibe el fallo como DatabaseUnavailableException.
    private readonly TaskCompletionSource<(DatabaseStatus Status, Exception? Failure)> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DatabaseStatus _status = new(DatabaseState.Initializing, null, null);

    public DatabaseStatus Status { get { lock (_lock) return _status; } }

    public event Action<DatabaseStatus>? Changed;

    public async Task WhenReadyAsync(CancellationToken cancellationToken)
    {
        var (status, failure) = await _ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (status.State == DatabaseState.Failed)
            throw new DatabaseUnavailableException($"La base de datos no está disponible: {status.Message}", failure);
    }

    /// <summary>
    /// Ejecuta la inicialización y publica el resultado. NO lanza si la BD falla (se refleja en <see cref="Status"/> y en
    /// <see cref="WhenReadyAsync"/>): quien la lanza en segundo plano no tiene que capturar nada.
    /// </summary>
    public async Task StartAsync(IDatabaseInitializer initializer, CancellationToken cancellationToken)
    {
        try
        {
            var result = await initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);
            var ready = new DatabaseStatus(DatabaseState.Ready, null, result.BackupPath);
            Set(ready);
            _ready.TrySetResult((ready, null));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ready.TrySetCanceled(cancellationToken);   // apagado: no es un fallo de la BD
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "No se pudo inicializar la base de datos");
            var backup = (ex as DatabaseInitializationException)?.BackupPath;
            var failed = new DatabaseStatus(DatabaseState.Failed, ex.Message, backup);
            Set(failed);
            _ready.TrySetResult((failed, ex));
        }
    }

    private void Set(DatabaseStatus status)
    {
        lock (_lock) _status = status;
        try { Changed?.Invoke(status); }
        catch (Exception ex) { logger.LogError(ex, "Falló un suscriptor del estado de la base de datos"); }
    }
}

public static class DatabaseReadinessExtensions
{
    /// <summary>Espera a que la BD esté lista y entonces abre una conexión. Es la forma de abrir conexiones para los repositorios.</summary>
    public static async Task<SqliteConnection> OpenWhenReadyAsync(this ISqliteConnectionFactory connections, IDatabaseReadiness readiness, CancellationToken cancellationToken)
    {
        await readiness.WhenReadyAsync(cancellationToken).ConfigureAwait(false);
        return await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
    }
}