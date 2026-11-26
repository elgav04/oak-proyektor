using OakProyektor.Core.Media;

namespace OakProyektor.App.Media;

/// <summary>Espacio del volumen que contiene <paramref name="path"/>. <c>Free</c> = −1 si no se sabe (entonces no se bloquea: se registra y se sigue).</summary>
public readonly record struct VolumeSpace(long Free, long Total);

public interface IFreeSpaceProvider
{
    VolumeSpace Of(string path);
}

public sealed class DriveFreeSpaceProvider : IFreeSpaceProvider
{
    public VolumeSpace Of(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return new VolumeSpace(-1, -1);
            var drive = new DriveInfo(root);
            return new VolumeSpace(drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException) { return new VolumeSpace(-1, -1); }
    }
}

/// <summary>Cómo se abre el archivo de ORIGEN al importar (se sustituye en los tests por un flujo lento o que se queda colgado).</summary>
public interface IMediaSourceOpener
{
    Stream OpenRead(string path);
}

public sealed class FileSourceOpener : IMediaSourceOpener
{
    public Stream OpenRead(string path) => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan);
}

/// <summary>
/// Disposición de la biblioteca en disco. <c>files\&lt;2 primeros del hash&gt;\&lt;sha256&gt;&lt;ext&gt;</c> (siempre ASCII), <c>thumbs\&lt;id&gt;.jpg</c> y <c>tmp\*.partial</c> (copias en curso). El hash como nombre resuelve de una vez
/// los nombres con caracteres raros, los reservados de Windows y las URL <c>file://</c> mal escapadas. <see cref="Gate"/> impide que el mantenimiento (borrar huérfanos) corra a la vez que una importación.
/// </summary>
public sealed class MediaStorage(string root)
{
    public string Root { get; } = root;
    public string FilesDir => Path.Combine(Root, "files");
    public string ThumbsDir => Path.Combine(Root, "thumbs");
    public string TmpDir => Path.Combine(Root, "tmp");
    public SemaphoreSlim Gate { get; } = new(1, 1);

    public string FilePathFor(string sha256, string ext) => Path.Combine(FilesDir, MediaRules.ShardOf(sha256), MediaRules.StoredFileName(sha256, ext));
    public string FilePathFor(MediaItem item) => FilePathFor(item.Sha256, item.Ext);
    public string ThumbPathFor(long id) => Path.Combine(ThumbsDir, $"{id.ToString(System.Globalization.CultureInfo.InvariantCulture)}.jpg");
    public string NewPartialPath() => Path.Combine(TmpDir, Guid.NewGuid().ToString("N") + ".partial");

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(FilesDir);
        Directory.CreateDirectory(ThumbsDir);
        Directory.CreateDirectory(TmpDir);
    }

    /// <summary>Reserva de disco al importar: el mayor de 1 GiB y el 5 % del volumen.</summary>
    public static long ReserveFor(VolumeSpace space) => Math.Max(MediaLimits.MinReserveBytes, space.Total > 0 ? space.Total / 100 * MediaLimits.ReservePercent : 0);
}
