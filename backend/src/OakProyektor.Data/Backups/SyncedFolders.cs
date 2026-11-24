namespace OakProyektor.Data.Backups;

/// <summary>
/// ¿Una ruta está dentro de una carpeta que un servicio de nube sincroniza (OneDrive, Dropbox, Google Drive, iCloud)? Un archivo SQLite ABIERTO (con su <c>-wal</c> y <c>-shm</c>) en una
/// carpeta sincronizada puede dar problemas: el cliente de sincronización lo bloquea o lo sube a medias, y puede dejar copias «en conflicto» o dañarlo. Función pura: las raíces de
/// sincronización (variables de entorno <c>OneDrive</c>…) se le pasan.
/// </summary>
public static class SyncedFolders
{
    private static readonly string[] ProviderFolderNames = ["Dropbox", "Google Drive", "GoogleDrive", "iCloudDrive"];

    /// <summary>El nombre del servicio (p. ej. «OneDrive») si la ruta está dentro de una carpeta sincronizada; null si no.</summary>
    /// <param name="roots">Raíces conocidas (valores de las variables <c>OneDrive</c>, <c>OneDriveConsumer</c>, <c>OneDriveCommercial</c>…); las nulas o vacías se ignoran.</param>
    public static string? Find(string path, IEnumerable<string?> roots)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            string normalized;
            try { normalized = Path.GetFullPath(root).TrimEnd('\\', '/'); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            if (full.Equals(normalized, StringComparison.OrdinalIgnoreCase) || full.StartsWith(normalized + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return "OneDrive";
        }

        foreach (var segment in full.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase)) return "OneDrive";
            var provider = ProviderFolderNames.FirstOrDefault(p => segment.Equals(p, StringComparison.OrdinalIgnoreCase));
            if (provider is not null) return provider.StartsWith("Google", StringComparison.OrdinalIgnoreCase) ? "Google Drive" : provider;
        }
        return null;
    }

    /// <summary>Las raíces de OneDrive del entorno actual.</summary>
    public static IEnumerable<string?> EnvironmentRoots() =>
        [Environment.GetEnvironmentVariable("OneDrive"), Environment.GetEnvironmentVariable("OneDriveConsumer"), Environment.GetEnvironmentVariable("OneDriveCommercial")];
}
