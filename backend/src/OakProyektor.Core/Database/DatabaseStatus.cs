namespace OakProyektor.Core.Database;

/// <summary>Estado de la base de datos. Se serializa en camelCase: initializing, ready, failed.</summary>
public enum DatabaseState
{
    Initializing,
    Ready,
    Failed,
}

/// <param name="Message">Si falló: qué pasó, en texto para el operador. Null en los demás estados.</param>
/// <param name="BackupPath">Copia de seguridad hecha antes de migrar (si hubo migraciones sobre una BD con datos); null si no hubo.</param>
public sealed record DatabaseStatus(DatabaseState State, string? Message, string? BackupPath);