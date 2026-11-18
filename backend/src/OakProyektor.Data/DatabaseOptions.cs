namespace OakProyektor.Data;

/// <summary>Configuración de la base de datos SQLite.</summary>
public sealed record DatabaseOptions(string DatabasePath)
{
    public const string FileName = "oak-proyektor.db";

    /// <summary>Opciones para <c>{dataDirectory}/oak-proyektor.db</c>.</summary>
    public static DatabaseOptions ForDataDirectory(string dataDirectory) =>
        new(Path.Combine(dataDirectory, FileName));
}
