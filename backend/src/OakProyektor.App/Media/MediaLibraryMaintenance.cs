using Microsoft.Extensions.Logging;
using OakProyektor.Core.Media;
using OakProyektor.Data.Media;

namespace OakProyektor.App.Media;

public sealed record MediaVerifyResult(int Checked, int Missing, int Restored, int OrphanFilesDeleted);

/// <summary>
/// Mantenimiento de la biblioteca: (1) borra los <c>tmp\*.partial</c> de una copia que se cortó; (2) borra los archivos de <c>files\</c> y <c>thumbs\</c> que no tienen fila (un cierre inesperado a mitad de
/// una importación o un borrado que no pudo quitar el archivo); (3) marca como <c>Missing</c> los elementos cuyo archivo ya no está y devuelve a <c>Pending</c> (para que el panel los vuelva a verificar)
/// los que reaparecieron. Corre al arrancar (en segundo plano) y con «Verificar biblioteca». Toma la misma puerta que la importación: nunca corren a la vez. Registra solo conteos.
/// </summary>
public sealed class MediaLibraryMaintenance(IMediaRepository repository, MediaStorage storage, ILogger<MediaLibraryMaintenance> logger)
{
    public async Task<MediaVerifyResult> VerifyAsync(CancellationToken ct)
    {
        await storage.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            storage.EnsureDirectories();
            foreach (var partial in Directory.EnumerateFiles(storage.TmpDir, "*.partial")) TryDelete(partial);

            var items = await repository.AllAsync(ct).ConfigureAwait(false);
            var known = new HashSet<string>(items.Select(i => storage.FilePathFor(i)), StringComparer.OrdinalIgnoreCase);
            var knownThumbs = new HashSet<string>(items.Where(i => i.HasThumbnail).Select(i => storage.ThumbPathFor(i.Id)), StringComparer.OrdinalIgnoreCase);

            var orphans = 0;
            foreach (var file in Directory.EnumerateFiles(storage.FilesDir, "*", SearchOption.AllDirectories))
                if (!known.Contains(file) && TryDelete(file)) orphans++;
            foreach (var file in Directory.EnumerateFiles(storage.ThumbsDir, "*.jpg"))
                if (!knownThumbs.Contains(file) && TryDelete(file)) orphans++;

            var missing = new List<long>(); var restored = new List<long>();
            foreach (var item in items)
            {
                var exists = File.Exists(storage.FilePathFor(item));
                if (!exists && item.Status != MediaStatus.Missing) missing.Add(item.Id);
                else if (exists && item.Status == MediaStatus.Missing) restored.Add(item.Id);
            }
            if (missing.Count > 0) await repository.SetStatusAsync(missing, MediaStatus.Missing, MediaErrorCodes.MissingFile, ct).ConfigureAwait(false);
            if (restored.Count > 0) await repository.SetStatusAsync(restored, MediaStatus.Pending, null, ct).ConfigureAwait(false);
            var totalMissing = items.Count(i => i.Status == MediaStatus.Missing) + missing.Count - restored.Count;
            logger.LogInformation("Biblioteca verificada: {Checked} elementos, {Missing} faltan, {Restored} reaparecieron, {Orphans} archivos huérfanos borrados", items.Count, totalMissing, restored.Count, orphans);
            return new MediaVerifyResult(items.Count, Math.Max(0, totalMissing), restored.Count, orphans);
        }
        finally { storage.Gate.Release(); }
    }

    /// <summary>Al arrancar: nunca lanza ni retrasa nada (todo fallo queda en el registro).</summary>
    public async Task RunAtStartupAsync(CancellationToken ct)
    {
        try { await VerifyAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* apagado */ }
        catch (Exception ex) { logger.LogWarning(ex, "No se pudo verificar la biblioteca de medios al arrancar"); }
    }

    private static bool TryDelete(string path)
    {
        try { File.Delete(path); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
