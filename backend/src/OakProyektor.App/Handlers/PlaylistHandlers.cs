using System.Globalization;
using OakProyektor.Core;
using OakProyektor.Core.Library;
using OakProyektor.Core.Playlists;
using OakProyektor.Data;
using OakProyektor.Data.Repositories;
using OakProyektor.Ipc;
using OakProyektor.Ipc.Messages;

namespace OakProyektor.App.Handlers;

/// <summary>
/// Convierte los fallos esperados del módulo de listas en errores de protocolo (el resto sigue su camino: <c>internal</c> sin detalles). Los mensajes de error y los registros de este
/// módulo NUNCA llevan títulos, notas ni letra: solo códigos, identificadores y conteos. Las escrituras corren por el carril LENTO; las lecturas, por el carril en vivo.
/// </summary>
internal static class PlaylistIpc
{
    public static async Task<T> RunAsync<T>(Func<Task<T>> action)
    {
        try { return await action().ConfigureAwait(false); }
        catch (PlaylistException ex) { throw new IpcException(ex.Code, ex.Message, ex); }
        catch (DatabaseUnavailableException ex) { throw new IpcException("database.unavailable", ex.Message, ex); }
    }

    public static string Iso(DateTimeOffset date) => date.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    public static PlaylistSettingsPayload ToPayload(AppSettings s) => new(s.Playlists.OnItemEnd);

    /// <summary>Tipo del protocolo (<c>song</c>, <c>passage</c>, <c>note</c>) → tipo guardado. Una presentación no se ofrece todavía.</summary>
    public static string ToDbType(string? wire) => wire switch
    {
        "song" => PlaylistItemTypes.Song,
        "passage" => PlaylistItemTypes.Passage,
        "note" => PlaylistItemTypes.Note,
        _ => throw new IpcException(PlaylistErrorCodes.InvalidRequest, "El tipo de elemento debe ser «song», «passage» o «note»."),
    };

    public static PlaylistItemDto Map(PlaylistItemDetail i) =>
        new(i.Id, i.Position, i.ItemType, i.Title, i.IsMissing, i.SongId, i.BookNumber, i.Chapter, i.VerseStart, i.VerseEnd, i.BibleAbbreviation,
            i.Note, i.ClearOnActivate, i.Resolution, i.OfferedBible, i.Projectable);

    public static void PublishChanged(IOutbox outbox, string commandId, long playlistId, int revision, string kind, long? itemId = null, long? sourceId = null, int copied = 0, int skipped = 0) =>
        outbox.Publish(IpcEnvelope.Create(IpcKind.Event, IpcTypes.PlaylistChanged, new PlaylistChangedPayload(commandId, playlistId, revision, kind, itemId, sourceId, copied, skipped)), Targets.Control);
}

internal sealed class PlaylistListHandler(IPlaylistRepository playlists) : IIpcHandler
{
    public string Kind => IpcKind.Query;
    public string Type => IpcTypes.PlaylistList;
    public async Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var result = await PlaylistIpc.RunAsync(() => playlists.ListAsync(cancellationToken)).ConfigureAwait(false);
        return new PlaylistListPayload(result.Truncated, [.. result.Playlists.Select(p => new PlaylistSummaryDto(p.Id, p.Name, p.ServiceDate, p.ItemCount, p.MissingCount, p.Revision))]);
    }
}

internal sealed class PlaylistGetHandler(IPlaylistRepository playlists, ISettingsStore settings) : IIpcHandler
{
    public string Kind => IpcKind.Query;
    public string Type => IpcTypes.PlaylistGet;
    public async Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var r = request.GetPayload<PlaylistGetRequest>();
        var detail = await PlaylistIpc.RunAsync(() => playlists.GetDetailAsync(r.Id, settings.Current.DefaultBible, cancellationToken)).ConfigureAwait(false)
            ?? throw new IpcException(PlaylistErrorCodes.NotFound, "La lista no existe (puede que se haya borrado).");
        return new PlaylistPayload(detail.Id, detail.Name, detail.ServiceDate, detail.Notes, detail.Revision, PlaylistIpc.Iso(detail.CreatedAt), PlaylistIpc.Iso(detail.UpdatedAt),
            detail.MissingCount, [.. detail.Items.Select(PlaylistIpc.Map)]);
    }
}

/// <summary>Cómo se opera una lista en vivo. Lee <c>settings.json</c>: no toca la BD.</summary>
internal sealed class PlaylistGetSettingsHandler(ISettingsStore settings) : IIpcHandler
{
    public string Kind => IpcKind.Query;
    public string Type => IpcTypes.PlaylistGetSettings;
    public Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken) =>
        Task.FromResult<object?>(PlaylistIpc.ToPayload(settings.Current));
}

/// <summary>Valida ANTES de guardar nada (un valor inválido no cambia nada).</summary>
internal sealed class PlaylistSetSettingsHandler(ISettingsStore settings) : IIpcHandler
{
    public string Kind => IpcKind.Command;
    public string Type => IpcTypes.PlaylistSetSettings;
    public async Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var r = request.GetPayload<PlaylistSettingsPayload>();
        var display = new PlaylistDisplaySettings(r.OnItemEnd);
        if (PlaylistDisplaySettings.Validate(display) is { } problem) throw new IpcException(PlaylistErrorCodes.InvalidSettings, problem);
        await settings.UpdateAsync(s => s with { PlaylistDisplay = display }, cancellationToken).ConfigureAwait(false);
        return null;
    }
}

internal sealed class PlaylistCreateHandler(IPlaylistRepository playlists, IOutbox outbox) : IIpcHandler
{
    public string Kind => IpcKind.Command;
    public string Type => IpcTypes.PlaylistCreate;
    public async Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var r = request.GetPayload<PlaylistCreatePayload>();
        var result = await PlaylistIpc.RunAsync(() => playlists.CreateAsync(r.Name, r.ServiceDate, r.Notes, cancellationToken)).ConfigureAwait(false);
        PlaylistIpc.PublishChanged(outbox, request.Id, result.PlaylistId, result.Revision, "created");
        return null;
    }
}

internal sealed class PlaylistUpdateHandler(IPlaylistRepository playlists, IOutbox outbox) : IIpcHandler
{
    public string Kind => IpcKind.Command;
    public string Type => IpcTypes.PlaylistUpdate;
    public async Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var r = request.GetPayload<PlaylistUpdatePayload>();
        var result = await PlaylistIpc.RunAsync(() => playlists.UpdateAsync(r.Id, r.Name, r.ServiceDate, r.Notes, r.ExpectedRevision, cancellationToken)).ConfigureAwait(false);
        PlaylistIpc.PublishChanged(outbox, request.Id, result.PlaylistId, result.Revision, "updated");
        return null;
    }
}

internal sealed class PlaylistAddItemHandler(IPlaylistRepository playlists, IOutbox outbox) : IIpcHandler
{
    public string Kind => IpcKind.Command;
    public string Type => IpcTypes.PlaylistAddItem;
    public async Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var r = request.GetPayload<PlaylistAddItemPayload>();
        if (r.Item is not { } i) throw new IpcException(PlaylistErrorCodes.InvalidRequest, "Falta el elemento que se quiere añadir.");
        var input = new PlaylistItemInput(PlaylistIpc.ToDbType(i.ItemType), i.SongId, i.BookNumber, i.Chapter, i.VerseStart, i.VerseEnd, i.BibleAbbreviation, i.Title, i.Note, i.ClearOnActivate);
        var result = await PlaylistIpc.RunAsync(() => playlists.AddItemAsync(r.PlaylistId, input, r.Position, cancellationToken)).ConfigureAwait(false);
        PlaylistIpc.PublishChanged(outbox, request.Id, result.PlaylistId, result.Revision, "itemAdded", result.ItemId);
        return null;
    }
}

internal sealed class PlaylistMoveItemHandler(IPlaylistRepository playlists, IOutbox outbox) : IIpcHandler
{
    public string Kind => IpcKind.Command;
    public string Type => IpcTypes.PlaylistMoveItem;
    public async Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var r = request.GetPayload<PlaylistMoveItemPayload>();
        var result = await PlaylistIpc.RunAsync(() => playlists.MoveItemAsync(r.PlaylistId, r.ItemId, r.ToIndex, r.ExpectedRevision, cancellationToken)).ConfigureAwait(false);
        PlaylistIpc.PublishChanged(outbox, request.Id, result.PlaylistId, result.Revision, "itemMoved", result.ItemId);
        return null;
    }
}

internal sealed class PlaylistRemoveItemHandler(IPlaylistRepository playlists, IOutbox outbox) : IIpcHandler
{
    public string Kind => IpcKind.Command;
    public string Type => IpcTypes.PlaylistRemoveItem;
    public async Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var r = request.GetPayload<PlaylistRemoveItemPayload>();
        var result = await PlaylistIpc.RunAsync(() => playlists.RemoveItemAsync(r.PlaylistId, r.ItemId, r.ExpectedRevision, cancellationToken)).ConfigureAwait(false);
        PlaylistIpc.PublishChanged(outbox, request.Id, result.PlaylistId, result.Revision, "itemRemoved", result.ItemId);
        return null;
    }
}

internal sealed class PlaylistUpdateItemHandler(IPlaylistRepository playlists, IOutbox outbox) : IIpcHandler
{
    public string Kind => IpcKind.Command;
    public string Type => IpcTypes.PlaylistUpdateItem;
    public async Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var r = request.GetPayload<PlaylistUpdateItemPayload>();
        var update = new PlaylistItemUpdate(r.Title, r.Note, r.ClearOnActivate, r.BibleAbbreviation);
        var result = await PlaylistIpc.RunAsync(() => playlists.UpdateItemAsync(r.PlaylistId, r.ItemId, update, r.ExpectedRevision, cancellationToken)).ConfigureAwait(false);
        // Sin cambios no hay revisión nueva ni evento (el panel no manda ediciones sin cambios: ver editor.ts).
        if (result.Changed) PlaylistIpc.PublishChanged(outbox, request.Id, result.PlaylistId, result.Revision, "itemUpdated", result.ItemId);
        return null;
    }
}

internal sealed class PlaylistDuplicateHandler(IPlaylistRepository playlists, IOutbox outbox) : IIpcHandler
{
    public string Kind => IpcKind.Command;
    public string Type => IpcTypes.PlaylistDuplicate;
    public async Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var r = request.GetPayload<PlaylistDuplicatePayload>();
        var result = await PlaylistIpc.RunAsync(() => playlists.DuplicateAsync(r.Id, r.Name, r.ServiceDate, cancellationToken)).ConfigureAwait(false);
        PlaylistIpc.PublishChanged(outbox, request.Id, result.NewPlaylistId, result.Revision, "duplicated", sourceId: r.Id, copied: result.Copied, skipped: result.Skipped);
        return null;
    }
}

internal sealed class PlaylistDeleteHandler(IPlaylistRepository playlists, IOutbox outbox) : IIpcHandler
{
    public string Kind => IpcKind.Command;
    public string Type => IpcTypes.PlaylistDelete;
    public async Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var r = request.GetPayload<PlaylistDeletePayload>();
        var result = await PlaylistIpc.RunAsync(() => playlists.DeleteAsync(r.Id, r.ExpectedRevision, cancellationToken)).ConfigureAwait(false);
        PlaylistIpc.PublishChanged(outbox, request.Id, result.PlaylistId, 0, "deleted");
        return null;
    }
}
