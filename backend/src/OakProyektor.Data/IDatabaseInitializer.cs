namespace OakProyektor.Data;

public interface IDatabaseInitializer
{
    /// <summary>Crea la BD si no existe, activa WAL y aplica las migraciones pendientes. Idempotente.</summary>
    Task InitializeAsync(CancellationToken cancellationToken);
}
