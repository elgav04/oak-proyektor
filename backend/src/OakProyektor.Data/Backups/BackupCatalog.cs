using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace OakProyektor.Data.Backups;

/// <summary>
/// Clases de copia de seguridad de la base de datos. Cada una tiene su propio prefijo de nombre y su PROPIA retención: nunca se mezclan (una tanda de copias diarias no desplaza a la
/// copia previa a una migración, y al revés).
/// </summary>
public enum BackupKind
{
    /// <summary>Una al día, hecha al arrancar la aplicación (<c>oak-proyektor.daily.AAAA-MM-DD.db</c>).</summary>
    Daily,
    /// <summary>Antes de aplicar migraciones (<c>oak-proyektor.v{versión}.{sello}.db</c>; la hace <c>MigrationRunner</c>).</summary>
    Migration,
    /// <summary>Antes de una reimportación de Biblia que modifique o borre algo (<c>oak-proyektor.bible-import.{sello}.{abreviatura}.db</c>).</summary>
    BibleImport,
    /// <summary>La base que había antes de restaurar otra (<c>oak-proyektor.before-restore.{sello}.db</c>).</summary>
    BeforeRestore,
}

/// <param name="Name">Nombre del archivo.</param>
/// <param name="SortKey">Fecha o sello sacado del NOMBRE (no de la fecha del archivo, que cambia si se copia): ordena de más antiguo a más reciente.</param>
public sealed record BackupInfo(BackupKind Kind, string Name, string Path, long Bytes, DateTime LastWriteLocal, string SortKey);

/// <summary>Qué copias hay en la carpeta de respaldos, de qué clase son y cuántas se conservan de cada una.</summary>
public static partial class BackupCatalog
{
    public const string DailyPrefix = "oak-proyektor.daily.";
    public const string BibleImportPrefix = "oak-proyektor.bible-import.";
    public const string BeforeRestorePrefix = "oak-proyektor.before-restore.";

    /// <summary>Copias que se conservan de cada clase (las más recientes; las demás se borran al hacer una nueva de esa clase).</summary>
    public static int Retention(BackupKind kind) => kind switch
    {
        BackupKind.Daily => 14,          // dos semanas de copias diarias
        BackupKind.Migration => 20,      // rara vez hay más de una por cambio de esquema: en la práctica se conservan todas
        BackupKind.BibleImport => 5,
        BackupKind.BeforeRestore => 10,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static string Describe(BackupKind kind) => kind switch
    {
        BackupKind.Daily => "diaria (al arrancar)",
        BackupKind.Migration => "antes de una migración",
        BackupKind.BibleImport => "antes de reimportar una Biblia",
        BackupKind.BeforeRestore => "base anterior a una restauración",
        _ => kind.ToString(),
    };

    [GeneratedRegex(@"^oak-proyektor\.v\d+\.", RegexOptions.IgnoreCase)]
    private static partial Regex MigrationPrefix();

    // El orden entre copias de una misma clase sale de la fecha o del sello del nombre: «2026-11-24» (diaria) o «20261124T031500123Z».
    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}|\d{8}T\d{6,9}Z")]
    private static partial Regex StampInName();

    /// <summary>La clase de una copia por su nombre de archivo, o null si no es una de las nuestras (cualquier otro archivo de la carpeta se deja en paz).</summary>
    public static BackupKind? Classify(string fileName)
    {
        if (!fileName.EndsWith(".db", StringComparison.OrdinalIgnoreCase)) return null;
        if (fileName.StartsWith(DailyPrefix, StringComparison.OrdinalIgnoreCase)) return BackupKind.Daily;
        if (fileName.StartsWith(BibleImportPrefix, StringComparison.OrdinalIgnoreCase)) return BackupKind.BibleImport;
        if (fileName.StartsWith(BeforeRestorePrefix, StringComparison.OrdinalIgnoreCase)) return BackupKind.BeforeRestore;
        if (MigrationPrefix().IsMatch(fileName)) return BackupKind.Migration;
        return null;
    }

    public static string DailyName(DateOnly day) => $"{DailyPrefix}{day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.db";

    /// <summary>Todas las copias de la carpeta, de la más reciente a la más antigua dentro de cada clase (las clases en el orden de <see cref="BackupKind"/>).</summary>
    public static IReadOnlyList<BackupInfo> List(string directory)
    {
        if (!Directory.Exists(directory)) return [];
        var result = new List<BackupInfo>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.db"))
        {
            var info = new FileInfo(file);
            if (Classify(info.Name) is not { } kind) continue;
            var key = StampInName().Match(info.Name) is { Success: true } m ? m.Value : "";
            result.Add(new BackupInfo(kind, info.Name, info.FullName, info.Length, info.LastWriteTime, key));
        }
        return [.. result.OrderBy(b => b.Kind).ThenByDescending(b => b.SortKey, StringComparer.Ordinal).ThenByDescending(b => b.LastWriteLocal).ThenByDescending(b => b.Name, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Borra las copias MÁS ANTIGUAS de una clase, dejando las <paramref name="keep"/> más recientes (por defecto, <see cref="Retention"/>). Solo toca archivos de esa clase.
    /// Un fallo al borrar (el archivo está abierto…) se registra y no impide seguir. Devuelve los nombres borrados.
    /// </summary>
    public static IReadOnlyList<string> Prune(string directory, BackupKind kind, ILogger? logger = null, int? keep = null)
    {
        var deleted = new List<string>();
        foreach (var old in List(directory).Where(b => b.Kind == kind).Skip(keep ?? Retention(kind)))
        {
            try
            {
                File.Delete(old.Path);
                deleted.Add(old.Name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger?.LogWarning(ex, "No se pudo borrar la copia antigua {Path}", old.Path);
            }
        }
        return deleted;
    }

    /// <summary>
    /// Resuelve lo que escribió el operador: el nombre de un archivo de la carpeta de respaldos (con o sin «.db») o una ruta completa. Lanza <see cref="BackupException"/> si no hay
    /// exactamente una copia que coincida.
    /// </summary>
    public static string Resolve(string directory, string nameOrPath)
    {
        var text = nameOrPath.Trim().Trim('"');
        if (text.Length == 0) throw new BackupException("Indica el nombre de la copia (usa --list-backups para verlas) o su ruta completa.");
        if (Path.IsPathRooted(text))
        {
            if (!File.Exists(text)) throw new BackupException($"No existe el archivo «{text}».");
            return Path.GetFullPath(text);
        }
        var exact = Path.Combine(directory, text);
        if (File.Exists(exact)) return Path.GetFullPath(exact);
        if (File.Exists(exact + ".db")) return Path.GetFullPath(exact + ".db");
        var matches = List(directory).Where(b => b.Name.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 1) return matches[0].Path;
        throw new BackupException(matches.Count == 0
            ? $"No hay ninguna copia que se llame «{text}» en {directory}. Usa --list-backups para verlas."
            : $"«{text}» coincide con {matches.Count} copias; escribe el nombre completo (--list-backups).");
    }
}

/// <summary>Un fallo esperado de copias o restauración. El mensaje es apto para el operador y nunca lleva texto bíblico.</summary>
public sealed class BackupException(string message, Exception? inner = null) : Exception(message, inner);
