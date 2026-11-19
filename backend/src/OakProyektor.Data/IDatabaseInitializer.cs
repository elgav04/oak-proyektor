using OakProyektor.Data.Migrations;

namespace OakProyektor.Data;

public interface IDatabaseInitializer
{
    /// <summary>
    /// Crea la BD si no existe, activa WAL y aplica las migraciones pendientes (con copia previa). Idempotente.
    /// Lanza <see cref="DatabaseInitializationException"/> si falla.
    /// </summary>
    Task<MigrationRunResult> InitializeAsync(CancellationToken cancellationToken);
}