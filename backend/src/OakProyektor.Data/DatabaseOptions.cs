namespace OakProyektor.Data;

/// <summary>Configuración de la base de datos SQLite.</summary>
public sealed record DatabaseOptions(string DatabasePath)
{
    public const string FileName = "oak-proyektor.db";

    /// <summary>Carpeta de TODAS las copias de seguridad (diarias, previas a migraciones y a reimportaciones, anteriores a una restauración): <c>backups</c> junto a la base de datos.</summary>
    public string BackupDirectory => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(DatabasePath))!, "backups");

    /// <summary>Opciones para <c>{dataDirectory}/oak-proyektor.db</c>.</summary>
    public static DatabaseOptions ForDataDirectory(string dataDirectory) =>
        new(Path.Combine(dataDirectory, FileName));
}