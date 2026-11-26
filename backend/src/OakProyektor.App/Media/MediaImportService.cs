using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using OakProyektor.Core.Media;
using OakProyektor.Core.Output;
using OakProyektor.Data;
using OakProyektor.Data.Media;
using OakProyektor.Ipc;
using OakProyektor.Ipc.Messages;

namespace OakProyektor.App.Media;

/// <summary>
/// Importa archivos a la biblioteca: UNA a la vez, en un hilo de fondo (NUNCA en el carril en vivo ni en el hilo de UI), cancelable, con avance y resultado hacia el panel. Nada de lo que se publica
/// o se registra lleva nombres ni rutas: solo ids y bytes. Orden: validar la fuente → espacio en disco → copia en bloques de 1 MiB a <c>tmp\*.partial</c> calculando el SHA-256 →
/// sonda de cabeceras (rechazo temprano de lo no soportado; HEVC se acepta con aviso) → duplicado por contenido → renombrado atómico → alta en la BD.
/// <para><b>Aborto por bloqueo</b>: si pasan <see cref="StallTimeout"/> (15 s) sin leer ni un byte (USB retirado, red caída) se aborta con <c>media.sourceStalled</c>. La lectura del sistema operativo es síncrona
/// e incancelable, así que el hilo de copia puede quedarse colgado: se ABANDONA (no bloquea a nadie), se libera la importación y el parcial se borra cuando ese hilo vuelva (o en el próximo arranque).</para>
/// </summary>
internal sealed class MediaImportService(
    IMediaRepository repository, MediaStorage storage, IMediaFileSystem fileSystem, IFreeSpaceProvider space, IMediaSourceOpener opener, IOutbox outbox, ILogger<MediaImportService> logger) : IDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private CancellationTokenSource? _current;
    private readonly object _gate = new();
    private int _running;

    /// <summary>Tiempo sin avance tras el cual se aborta la copia (por defecto 15 s; los tests lo acortan).</summary>
    public TimeSpan StallTimeout { get; init; } = MediaLimits.StallTimeout;

    /// <summary>Cada cuántos bloques de 1 MiB se vuelve a comprobar el espacio libre durante la copia (por defecto 64; los tests lo bajan).</summary>
    public int SpaceCheckEveryBlocks { get; init; } = 64;

    public bool IsRunning => Volatile.Read(ref _running) != 0;

    /// <summary>Arranca la importación y vuelve. Falla (<c>media.importBusy</c>) si ya hay una en curso o el mantenimiento de la biblioteca está corriendo.</summary>
    public void Start(string importId, MediaImportPayload payload)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            throw new IpcException(MediaErrorCodes.ImportBusy, "Ya hay una importación en curso; espera a que termine o cancélala.");
        if (!storage.Gate.Wait(0))
        {
            Interlocked.Exchange(ref _running, 0);
            throw new IpcException(MediaErrorCodes.ImportBusy, "La biblioteca está ocupada (mantenimiento en curso); inténtalo de nuevo en unos segundos.");
        }
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        lock (_gate) _current = cts;
        _ = Task.Run(async () =>
        {
            try { await RunAsync(importId, payload, cts.Token).ConfigureAwait(false); }
            catch (Exception ex) { logger.LogError(ex, "Falló el hilo de la importación {ImportId}", importId); }
            finally
            {
                lock (_gate) _current = null;
                cts.Dispose();
                storage.Gate.Release();
                Interlocked.Exchange(ref _running, 0);
            }
        });
    }

    /// <summary>Cancela la importación en curso (si la hay). El parcial se borra y se publica <c>media.cancelled</c>.</summary>
    public void Cancel()
    {
        lock (_gate) _current?.Cancel();
    }

    private async Task RunAsync(string importId, MediaImportPayload p, CancellationToken ct)
    {
        string? partial = null;
        try
        {
            var validation = ImportSourceValidator.Validate(p.Path, fileSystem);
            if (!validation.IsValid) { Finish(importId, false, validation.Code, validation.Message); return; }
            var source = validation.Source!;

            storage.EnsureDirectories();
            var vol = space.Of(storage.Root);
            var reserve = MediaStorage.ReserveFor(vol);
            if (vol.Free >= 0 && vol.Free < source.Length + reserve)
            {
                logger.LogWarning("Importación {ImportId}: no hay espacio (faltan {Missing} bytes)", importId, source.Length + reserve - vol.Free);
                Finish(importId, false, MediaErrorCodes.DiskFull, $"{MediaRules.MessageFor(MediaErrorCodes.DiskFull)} Faltan {FormatBytes(source.Length + reserve - vol.Free)}.");
                return;
            }

            partial = storage.NewPartialPath();
            var progress = new ProgressClock();
            var copy = Task.Factory.StartNew(() => Copy(importId, source, partial, vol, reserve, progress, ct), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            var outcome = await WaitForCopyAsync(copy, progress, ct).ConfigureAwait(false);
            if (outcome.Stalled)
            {
                logger.LogWarning("Importación {ImportId}: sin avance durante {Seconds} s; se abandona la copia", importId, (int)StallTimeout.TotalSeconds);
                var abandoned = partial;
                _ = copy.ContinueWith(_ => TryDelete(abandoned), TaskScheduler.Default);   // cuando el hilo colgado vuelva (si vuelve), se borra el parcial
                partial = null;
                Finish(importId, false, MediaErrorCodes.SourceStalled, MediaRules.MessageFor(MediaErrorCodes.SourceStalled));
                return;
            }
            if (ct.IsCancellationRequested)
            {
                var toDelete = partial; partial = null;   // el hilo de copia puede seguir con el archivo abierto un instante: se borra cuando termine
                _ = copy.ContinueWith(_ => TryDelete(toDelete), TaskScheduler.Default);
                Finish(importId, false, MediaErrorCodes.Cancelled, "Importación cancelada. No se cambió nada.");
                return;
            }
            var copied = await copy.ConfigureAwait(false);
            if (copied.ErrorCode is { } copyError) { Finish(importId, false, copyError, MediaRules.MessageFor(copyError)); return; }

            Publish(importId, "comprobando", source.Length, source.Length);
            MediaProbeResult probe;
            await using (var fs = new FileStream(partial, FileMode.Open, FileAccess.Read, FileShare.Read))
                probe = MediaProbe.Probe(fs, source.Ext);
            var verdict = MediaProbe.Judge(source.Kind, probe);
            if (verdict.RejectCode is { } reject) { Finish(importId, false, reject, MediaRules.MessageFor(reject)); return; }

            if (await repository.FindByShaAsync(copied.Sha256!, ct).ConfigureAwait(false) is { } existing)
            {
                Finish(importId, false, MediaErrorCodes.Duplicate, MediaRules.MessageFor(MediaErrorCodes.Duplicate), duplicate: existing);
                return;
            }

            Publish(importId, "guardando", source.Length, source.Length);
            var final = storage.FilePathFor(copied.Sha256!, source.Ext);
            Directory.CreateDirectory(Path.GetDirectoryName(final)!);
            if (File.Exists(final)) TryDelete(partial);   // un huérfano con el mismo contenido de un cierre anterior: sirve tal cual
            else File.Move(partial, final);
            partial = null;
            try
            {
                var item = await repository.InsertAsync(new NewMediaItem(
                    source.Kind, string.IsNullOrWhiteSpace(p.Name) ? MediaRules.NameFromFile(source.OriginalFileName) : MediaRules.NormalizeName(p.Name), p.FolderId, source.OriginalFileName, source.Ext, source.Length,
                    copied.Sha256!, probe.DurationMs, probe.Width, probe.Height, probe.Container, probe.VideoCodec, probe.AudioCodec, probe.HasAudio, verdict.StatusCode), ct).ConfigureAwait(false);
                logger.LogInformation("Importación {ImportId} terminada: medio {MediaId}", importId, item.Id);
                Finish(importId, true, null, null, mediaId: item.Id, warningCode: verdict.StatusCode);
                outbox.Publish(IpcEnvelope.Create(IpcKind.Event, IpcTypes.MediaChanged, new MediaChangedPayload(importId, "added", item.Id, 0)), Targets.Control);
            }
            catch
            {
                if (await SafeFindAsync(copied.Sha256!).ConfigureAwait(false) is null) TryDelete(final);   // no queda ninguna fila: no dejar un archivo huérfano
                throw;
            }
        }
        catch (OperationCanceledException) { Finish(importId, false, MediaErrorCodes.Cancelled, "Importación cancelada. No se cambió nada."); }
        catch (MediaException ex) { Finish(importId, false, ex.Code, ex.Message); }
        catch (DatabaseUnavailableException ex) { Finish(importId, false, "database.unavailable", ex.Message); }
        catch (Exception ex)
        {
            logger.LogError(ex, "La importación {ImportId} falló", importId);
            Finish(importId, false, MediaErrorCodes.ImportFailed, "La importación falló; revisa el registro de la aplicación. No se cambió nada.");
        }
        finally { if (partial is not null) TryDelete(partial); }
    }

    private async Task<MediaItem?> SafeFindAsync(string sha)
    {
        try { return await repository.FindByShaAsync(sha, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) { return null; }
    }

    private sealed class ProgressClock
    {
        private long _ticks = Stopwatch.GetTimestamp();
        public void Touch() => Interlocked.Exchange(ref _ticks, Stopwatch.GetTimestamp());
        public TimeSpan Silence => Stopwatch.GetElapsedTime(Interlocked.Read(ref _ticks));
    }

    private sealed record CopyResult(string? Sha256, string? ErrorCode);
    private readonly record struct WaitOutcome(bool Stalled);

    /// <summary>Espera la copia vigilando que avance: si pasa <see cref="StallTimeout"/> sin leer nada, la ABANDONA (el hilo puede estar colgado en el sistema operativo).</summary>
    private async Task<WaitOutcome> WaitForCopyAsync(Task<CopyResult> copy, ProgressClock progress, CancellationToken ct)
    {
        var poll = TimeSpan.FromMilliseconds(Math.Clamp(StallTimeout.TotalMilliseconds / 4, 20, 500));
        while (!copy.IsCompleted)
        {
            try { await Task.WhenAny(copy, Task.Delay(poll, ct)).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* se atiende abajo */ }
            if (copy.IsCompleted) break;
            if (progress.Silence >= StallTimeout) return new WaitOutcome(true);
            if (ct.IsCancellationRequested && progress.Silence >= TimeSpan.FromMilliseconds(poll.TotalMilliseconds * 2)) return new WaitOutcome(false);   // cancelado y el hilo no reacciona: no se espera
        }
        return new WaitOutcome(false);
    }

    private CopyResult Copy(string importId, ImportSource source, string partial, VolumeSpace volume, long reserve, ProgressClock clock, CancellationToken ct)
    {
        try
        {
            using var input = opener.OpenRead(source.Path);
            using var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, MediaLimits.CopyBlockBytes, FileOptions.None);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[MediaLimits.CopyBlockBytes];
            long done = 0; var last = Stopwatch.GetTimestamp(); var blocks = 0;
            clock.Touch();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var read = input.Read(buffer, 0, buffer.Length);
                clock.Touch();
                if (read <= 0) break;
                done += read;
                if (done > MediaLimits.MaxBytes) return new CopyResult(null, MediaErrorCodes.TooLarge);   // el archivo creció mientras se copiaba
                hash.AppendData(buffer, 0, read);
                output.Write(buffer, 0, read);
                if (++blocks % Math.Max(1, SpaceCheckEveryBlocks) == 0 && volume.Free >= 0)
                {   // «se vuelve a comprobar a mitad»: otro programa puede llenar el disco mientras se copia
                    var now = space.Of(storage.Root);
                    if (now.Free >= 0 && now.Free < Math.Max(0, source.Length - done) + reserve) return new CopyResult(null, MediaErrorCodes.DiskFull);
                }
                if (Stopwatch.GetElapsedTime(last) >= TimeSpan.FromMilliseconds(250)) { Publish(importId, "copiando", done, source.Length); last = Stopwatch.GetTimestamp(); }
            }
            output.Flush(flushToDisk: true);
            if (done == 0) return new CopyResult(null, MediaErrorCodes.SourceInvalid);   // el origen devolvió 0 bytes
            Publish(importId, "copiando", done, source.Length);
            return new CopyResult(Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), null);
        }
        catch (IOException ex) when (IsDiskFull(ex)) { return new CopyResult(null, MediaErrorCodes.DiskFull); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Importación {ImportId}: no se pudo leer o escribir ({Type})", importId, ex.GetType().Name);
            return new CopyResult(null, MediaErrorCodes.SourceInvalid);
        }
    }

    private static bool IsDiskFull(IOException ex) => (ex.HResult & 0xFFFF) is 0x27 or 0x70;   // ERROR_HANDLE_DISK_FULL, ERROR_DISK_FULL

    private void Publish(string importId, string phase, long done, long total)
    {
        try { outbox.Publish(IpcEnvelope.Create(IpcKind.Event, IpcTypes.MediaImportProgress, new MediaImportProgressPayload(importId, phase, done, total)), Targets.Control); }
        catch (Exception ex) { logger.LogWarning(ex, "No se pudo publicar el avance de la importación"); }
    }

    private void Finish(string importId, bool ok, string? code, string? message, long? mediaId = null, string? warningCode = null, MediaItem? duplicate = null)
    {
        var finished = new MediaImportFinishedPayload(importId, ok, code, message, mediaId, warningCode, warningCode is null ? null : MediaRules.MessageFor(warningCode), duplicate?.Id, duplicate?.Name);
        try { outbox.Publish(IpcEnvelope.Create(IpcKind.Event, IpcTypes.MediaImportFinished, finished), Targets.Control); }
        catch (Exception ex) { logger.LogWarning(ex, "No se pudo publicar el resultado de la importación"); }
    }

    private static void TryDelete(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* el barrido del arranque lo recoge */ }
    }

    private static string FormatBytes(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GiB" : $"{Math.Max(1, bytes / (1L << 20))} MiB";

    public void Dispose() => _shutdown.Cancel();
}
