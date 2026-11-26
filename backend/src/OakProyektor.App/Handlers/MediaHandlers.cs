using Microsoft.Extensions.Logging;
using OakProyektor.App.Media;
using OakProyektor.Core.Media;
using OakProyektor.Core.Output;
using OakProyektor.Data;
using OakProyektor.Data.Media;
using OakProyektor.Ipc;
using OakProyektor.Ipc.Messages;

namespace OakProyektor.App.Handlers;

/// <summary>
/// Biblioteca de medios (protocolo v17). Los mensajes de error y los registros NUNCA llevan nombres ni rutas del operador: solo códigos, ids y conteos. Todo lo que toca disco o la BD va por el carril
/// LENTO; las lecturas, por el carril en vivo. Reproducir (o servir un fondo) usa SIEMPRE la copia local de la biblioteca, nunca una ruta que mande el cliente.
/// </summary>
internal static class MediaIpc
{
    public static async Task<T> RunAsync<T>(Func<Task<T>> action)
    {
        try { return await action().ConfigureAwait(false); }
        catch (MediaException ex) { throw new IpcException(ex.Code, ex.Message, ex); }
        catch (DatabaseUnavailableException ex) { throw new IpcException("database.unavailable", ex.Message, ex); }
    }

    public static MediaItemDto Map(MediaItem i, MediaStorage storage)
    {
        var fileUrl = i.Status == MediaStatus.Missing ? null : MediaPathValidator.ToFileUrl(storage.FilePathFor(i));
        var thumb = i.HasThumbnail ? MediaPathValidator.ToFileUrl(storage.ThumbPathFor(i.Id)) + "?r=" + i.Revision : null;
        return new MediaItemDto(i.Id, i.Kind, i.Name, i.FolderId, i.OriginalFileName, i.Ext, i.Bytes, i.DurationMs, i.Width, i.Height, i.Container, i.VideoCodec, i.AudioCodec, i.HasAudio,
            i.Status, i.StatusCode, i.StatusCode is null ? null : MediaRules.MessageFor(i.StatusCode), fileUrl, thumb, i.Revision, MediaRepository.Stamp(i.CreatedAt), MediaRepository.Stamp(i.UpdatedAt));
    }

    public static void PublishChanged(IOutbox outbox, string commandId, string kind, long? mediaId = null, int count = 0) =>
        outbox.Publish(IpcEnvelope.Create(IpcKind.Event, IpcTypes.MediaChanged, new MediaChangedPayload(commandId, kind, mediaId, count)), Targets.Control);
}

internal sealed class MediaListHandler(IMediaRepository media, MediaStorage storage, IFreeSpaceProvider space) : IIpcHandler
{
    public string Kind => IpcKind.Query;
    public string Type => IpcTypes.MediaList;
    public async Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var r = request.GetPayload<MediaListRequest>();
        var result = await MediaIpc.RunAsync(() => media.ListAsync(new MediaQuery(r.FolderId, r.OnlyUnfiled, r.Kind, r.Query, r.Limit ?? MediaLimits.MaxListed), cancellationToken)).ConfigureAwait(false);
        return new MediaListPayload([.. result.Folders.Select(f => new MediaFolderDto(f.Id, f.Name, f.ItemCount))], [.. result.Items.Select(i => MediaIpc.Map(i, storage))],
            result.Truncated, result.TotalItems, result.TotalBytes, space.Of(storage.Root).Free);
    }
}

internal sealed class MediaGetHandler(IMediaRepository media, MediaStorage storage) : IIpcHandler
{
    public string Kind => IpcKind.Query;
    public string Type => IpcTypes.MediaGet;
    public async Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var id = request.GetPayload<MediaGetRequest>().Id;
        var item = await MediaIpc.RunAsync(() => media.GetAsync(id, cancellationToken)).ConfigureAwait(false) ?? throw new IpcException(MediaErrorCodes.NotFound, MediaRules.MessageFor(MediaErrorCodes.NotFound));
        return MediaIpc.Map(item, storage);
    }
}

/// <summary>Vuelve AL INSTANTE: la copia corre en un hilo de fondo (una a la vez) y el resultado llega como <c>media.importFinished</c> con el id de este comando.</summary>
internal sealed class MediaImportHandler(MediaImportService importer) : IIpcHandler
{
    public string Kind => IpcKind.Command;
    public string Type => IpcTypes.MediaImport;
    public Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        importer.Start(request.Id, request.GetPayload<MediaImportPayload>());
        return Task.FromResult<object?>(null);
    }
}

internal sealed class MediaCancelImportHandler(MediaImportService importer) : IIpcHandler
{
    public string Kind => IpcKind.Command;
    public string Type => IpcTypes.MediaCancelImport;
    public Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        importer.Cancel();
        return Task.FromResult<object?>(null);
    }
}

internal sealed class MediaRenameHandler(IMediaRepository media, IOutbox outbox) : IIpcHandler
{
    public string Kind => IpcKind.Command;
    public string Type => IpcTypes.MediaRename;
    public async Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var r = request.GetPayload<MediaRenamePayload>();
        var item = await MediaIpc.RunAsync(() => media.RenameAsync(r.Id, r.Name, cancellationToken)).ConfigureAwait(false);
        MediaIpc.PublishChanged(outbox, request.Id, "updated", item.Id);
        return null;
    }
}

internal sealed class MediaMoveHandler(IMediaRepository media, IOutbox outbox) : IIpcHandler
{
    public string Kind => IpcKind.Command;
    public string Type => IpcTypes.MediaMove;
    public async Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var r = request.GetPayload<MediaMovePayload>();
        var item = await MediaIpc.RunAsync(() => media.MoveAsync(r.Id, r.FolderId, cancellationToken)).ConfigureAwait(false);
        MediaIpc.PublishChanged(outbox, request.Id, "updated", item.Id);
        return null;
    }
}

/// <summary>Borra la fila y la copia del disco. Se RECHAZA si es el fondo que está en la salida (borrarlo dejaría el fondo apuntando a un archivo que desaparece).</summary>
internal sealed class MediaDeleteHandler(IMediaRepository media, MediaStorage storage, IOutputController output, IOutbox outbox, ILogger<MediaDeleteHandler> logger) : IIpcHandler
{
    public string Kind => IpcKind.Command;
    public string Type => IpcTypes.MediaDelete;
    public async Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var id = request.GetPayload<MediaDeletePayload>().Id;
        var item = await MediaIpc.RunAsync(() => media.GetAsync(id, cancellationToken)).ConfigureAwait(false);
        if (item is not null)
        {
            var current = output.Snapshot().State.Background.MediaUrl;
            if (current is not null && string.Equals(current, MediaPathValidator.ToFileUrl(storage.FilePathFor(item)), StringComparison.OrdinalIgnoreCase))
                throw new IpcException("media.inUse", "Es el fondo que está en la salida: cambia el fondo antes de borrarlo.");
        }
        var result = await MediaIpc.RunAsync(() => media.DeleteAsync(id, cancellationToken)).ConfigureAwait(false);
        if (result.Existed)
        {
            TryDelete(storage.FilePathFor(result.Sha256!, result.Ext!), id);
            if (result.HadThumbnail) TryDelete(storage.ThumbPathFor(id), id);
        }
        MediaIpc.PublishChanged(outbox, request.Id, "deleted", id);
        return null;
    }

    private void TryDelete(string path, long id)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger.LogWarning("No se pudo borrar el archivo del medio {MediaId} ({Type}); lo recogerá el barrido del arranque", id, ex.GetType().Name); }
    }
}

internal sealed class MediaCreateFolderHandler(IMediaRepository media, IOutbox outbox) : IIpcHandler
{
    public string Kind => IpcKind.Command;
    public string Type => IpcTypes.MediaCreateFolder;
    public async Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        await MediaIpc.RunAsync(() => media.CreateFolderAsync(request.GetPayload<MediaCreateFolderPayload>().Name, cancellationToken)).ConfigureAwait(false);
        MediaIpc.PublishChanged(outbox, request.Id, "folders");
        return null;
    }
}

internal sealed class MediaRenameFolderHandler(IMediaRepository media, IOutbox outbox) : IIpcHandler
{
    public string Kind => IpcKind.Command;
    public string Type => IpcTypes.MediaRenameFolder;
    public async Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var r = request.GetPayload<MediaRenameFolderPayload>();
        await MediaIpc.RunAsync(() => media.RenameFolderAsync(r.Id, r.Name, cancellationToken)).ConfigureAwait(false);
        MediaIpc.PublishChanged(outbox, request.Id, "folders");
        return null;
    }
}

internal sealed class MediaDeleteFolderHandler(IMediaRepository media, IOutbox outbox) : IIpcHandler
{
    public string Kind => IpcKind.Command;
    public string Type => IpcTypes.MediaDeleteFolder;
    public async Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var id = request.GetPayload<MediaDeleteFolderPayload>().Id;
        await MediaIpc.RunAsync<int>(async () => { await media.DeleteFolderAsync(id, cancellationToken).ConfigureAwait(false); return 0; }).ConfigureAwait(false);
        MediaIpc.PublishChanged(outbox, request.Id, "folders");
        return null;
    }
}

/// <summary>Lo que el navegador del panel averiguó al cargar la copia (se reproduce o no, duración y dimensiones reales). Un HEVC que no decodifica se dice como tal.</summary>
internal sealed class MediaReportProbeHandler(IMediaRepository media, IOutbox outbox) : IIpcHandler
{
    public string Kind => IpcKind.Command;
    public string Type => IpcTypes.MediaReportProbe;
    public async Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var r = request.GetPayload<MediaReportProbePayload>();
        var item = await MediaIpc.RunAsync(() => media.ApplyProbeAsync(r.MediaId, r.Ok, SanitizeLong(r.DurationMs), SanitizeInt(r.Width), SanitizeInt(r.Height), FailureCode(r.ErrorCode), cancellationToken)).ConfigureAwait(false);
        MediaIpc.PublishChanged(outbox, request.Id, "updated", item.Id);
        return null;
    }

    private static long? SanitizeLong(long? v) => v is > 0 and < 7L * 24 * 3600 * 1000 ? v : null;
    private static int? SanitizeInt(int? v) => v is > 0 and <= 16384 ? v : null;

    /// <summary>Del código del navegador al del módulo (el texto lo pone el backend). Solo se aceptan los conocidos.</summary>
    private static string FailureCode(string? error) => error switch
    {
        "unsupported" => MediaErrorCodes.VideoCodecUnsupported,
        "audioUnsupported" => MediaErrorCodes.AudioCodecUnsupported,
        _ => MediaErrorCodes.DecodeFailed,
    };
}

/// <summary>Guarda la miniatura que generó el panel (JPEG ≤ 200 KB y ≤ 640×360; se comprueban los bytes, no solo lo que dice el cliente).</summary>
internal sealed class MediaSetThumbnailHandler(IMediaRepository media, MediaStorage storage, IOutbox outbox) : IIpcHandler
{
    public string Kind => IpcKind.Command;
    public string Type => IpcTypes.MediaSetThumbnail;
    public async Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var r = request.GetPayload<MediaSetThumbnailPayload>();
        var b64 = r.JpegBase64 ?? "";
        if (b64.Length == 0 || b64.Length > MediaLimits.MaxThumbnailBytes / 3 * 4 + 8) throw new IpcException(MediaErrorCodes.InvalidThumbnail, "La miniatura no es válida (demasiado grande o vacía).");
        var bytes = new byte[b64.Length / 4 * 3 + 3];
        if (!Convert.TryFromBase64String(b64, bytes, out var written)) throw new IpcException(MediaErrorCodes.InvalidThumbnail, "La miniatura no es válida.");
        bytes = bytes[..written];
        if (bytes.Length > MediaLimits.MaxThumbnailBytes || bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8 || bytes[2] != 0xFF) throw new IpcException(MediaErrorCodes.InvalidThumbnail, "La miniatura no es un JPEG válido.");
        using (var ms = new MemoryStream(bytes))
        {
            var probe = MediaProbe.Probe(ms, ".jpg");
            if (probe.Width is not > 0 or > 640 || probe.Height is not > 0 or > 360) throw new IpcException(MediaErrorCodes.InvalidThumbnail, "La miniatura no es un JPEG válido o es demasiado grande (máximo 640×360).");
        }
        var item = await MediaIpc.RunAsync(() => media.GetAsync(r.MediaId, cancellationToken)).ConfigureAwait(false) ?? throw new IpcException(MediaErrorCodes.NotFound, MediaRules.MessageFor(MediaErrorCodes.NotFound));
        storage.EnsureDirectories();
        var path = storage.ThumbPathFor(item.Id);
        var temp = path + ".tmp";
        await File.WriteAllBytesAsync(temp, bytes, cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
        await MediaIpc.RunAsync<int>(async () => { await media.SetThumbnailAsync(item.Id, true, cancellationToken).ConfigureAwait(false); return 0; }).ConfigureAwait(false);
        MediaIpc.PublishChanged(outbox, request.Id, "updated", item.Id);
        return null;
    }
}

internal sealed class MediaVerifyLibraryHandler(MediaLibraryMaintenance maintenance, IOutbox outbox) : IIpcHandler
{
    public string Kind => IpcKind.Command;
    public string Type => IpcTypes.MediaVerifyLibrary;
    public async Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var result = await MediaIpc.RunAsync(() => maintenance.VerifyAsync(cancellationToken)).ConfigureAwait(false);
        MediaIpc.PublishChanged(outbox, request.Id, "verified", null, result.Missing);
        return null;
    }
}

/// <summary>
/// Usa un video o una imagen de la biblioteca como fondo. Resuelve la COPIA local por id y la pasa por el validador de siempre (existe, es local, extensión permitida); la salida recibe solo la URL
/// validada. La comprobación toca el disco: corre en el pool con un tope de 5 s (como <c>output.setBackground</c>) y por el carril lento.
/// </summary>
internal sealed class MediaSetBackgroundHandler(IMediaRepository media, MediaStorage storage, IMediaFileSystem fileSystem, IOutputController output) : IIpcHandler
{
    public string Kind => IpcKind.Command;
    public string Type => IpcTypes.MediaSetBackground;
    public async Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var r = request.GetPayload<MediaSetBackgroundPayload>();
        if (ContentValidator.ValidateTransition(r.TransitionMs, "Fondo") is { } badTransition) throw new IpcException("output.invalidContent", badTransition);
        var item = await MediaIpc.RunAsync(() => media.GetAsync(r.MediaId, cancellationToken)).ConfigureAwait(false) ?? throw new IpcException(MediaErrorCodes.NotFound, MediaRules.MessageFor(MediaErrorCodes.NotFound));
        if (item.Kind == MediaItemKind.Audio) throw new IpcException(MediaErrorCodes.NotBackgroundable, "Una pista de audio no puede ser el fondo.");
        if (item.Status == MediaStatus.Missing) throw new IpcException(MediaErrorCodes.MissingFile, MediaRules.MessageFor(MediaErrorCodes.MissingFile));
        if (item.Status == MediaStatus.Unsupported) throw new IpcException(item.StatusCode ?? MediaErrorCodes.DecodeFailed, MediaRules.MessageFor(item.StatusCode ?? MediaErrorCodes.DecodeFailed));

        var expected = item.Kind == MediaItemKind.Video ? Core.Output.MediaKind.Video : Core.Output.MediaKind.Image;
        var path = storage.FilePathFor(item);
        MediaValidation result;
        try { result = await Task.Run(() => MediaPathValidator.Validate(path, fileSystem, expected), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) { throw new IpcException("output.mediaRejected", "No se pudo comprobar el archivo a tiempo."); }
        if (!result.IsValid) throw new IpcException(MediaErrorCodes.MissingFile, result.Message ?? MediaRules.MessageFor(MediaErrorCodes.MissingFile));
        var media2 = result.Media!;
        var kind = item.Kind == MediaItemKind.Video ? BackgroundKind.Video : BackgroundKind.Image;
        output.SetBackground(new BackgroundLayer(kind, BackgroundLayer.DefaultColor, media2.Url, item.Name, r.Muted, r.TransitionMs, null));
        return null;
    }
}
