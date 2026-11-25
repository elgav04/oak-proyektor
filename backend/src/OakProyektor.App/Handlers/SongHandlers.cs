using System.Globalization;
using OakProyektor.Core;
using OakProyektor.Core.Songs;
using OakProyektor.Data;
using OakProyektor.Data.Repositories;
using OakProyektor.Ipc;
using OakProyektor.Ipc.Messages;

namespace OakProyektor.App.Handlers;

/// <summary>
/// Convierte los fallos esperados del módulo de canciones en errores de protocolo (el resto sigue su camino: <c>internal</c> sin detalles). Los mensajes de error y los registros
/// de este módulo NUNCA llevan letra de canciones: solo códigos, identificadores y conteos.
/// </summary>
internal static class SongIpc
{
    public static async Task<T> RunAsync<T>(Func<Task<T>> action)
    {
        try { return await action().ConfigureAwait(false); }
        catch (SongException ex) { throw new IpcException(ex.Code, ex.Message, ex); }
        catch (DatabaseUnavailableException ex) { throw new IpcException("database.unavailable", ex.Message, ex); }
    }

    public static string Iso(DateTimeOffset date) => date.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    public static SongSummaryDto Map(SongSummary s) => new(s.Id, s.Title, s.Author, Iso(s.UpdatedAt), s.Fragment);

    public static SongSectionDto Map(ParsedSection s) => new(null, s.Kind, s.Label, s.Text);

    public static SongPayload Map(SongDetail d) =>
        new(d.Id, d.Title, d.Author, d.Copyright, d.CcliNumber, d.Notes, d.Revision, Iso(d.CreatedAt), Iso(d.UpdatedAt),
            d.Background is { } b ? new SongBackgroundDto(b.Kind, b.Path, b.Color, b.Muted) : null,
            [.. d.Sections.Select(s => new SongSectionDto(s.Id, s.Kind, s.Label, s.Text))], d.Arrangement, d.Text, d.UsedInServices);

    public static string Detected(SongTextFormat format) => format switch
    {
        SongTextFormat.Brackets => "brackets",
        SongTextFormat.AliasLines => "aliasLines",
        SongTextFormat.Blocks => "blocks",
        _ => "empty",
    };

    public static SongSettingsPayload ToPayload(AppSettings s) => new(s.Songs.EffectiveTheme, s.Songs.TransitionMs, s.Songs.AtEnd);

    public static void PublishChanged(IOutbox outbox, string commandId, long songId, int revision, string kind, int usedInServices) =>
        outbox.Publish(IpcEnvelope.Create(IpcKind.Event, IpcTypes.SongChanged, new SongChangedPayload(commandId, songId, revision, kind, usedInServices)), Targets.Control);
}

internal sealed class SongSearchHandler(ISongRepository songs) : IIpcHandler
{
    public string Kind => IpcKind.Query;
    public string Type => IpcTypes.SongSearch;
    public async Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var r = request.GetPayload<SongSearchRequest>();
        var result = await SongIpc.RunAsync(() => songs.SearchAsync(r.Query ?? "", r.Limit, cancellationToken)).ConfigureAwait(false);
        return new SongSearchPayload(result.Query, result.Truncated, [.. result.Songs.Select(SongIpc.Map)]);
    }
}

internal sealed class SongListHandler(ISongRepository songs) : IIpcHandler
{
    public string Kind => IpcKind.Query;
    public string Type => IpcTypes.SongList;
    public async Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var result = await SongIpc.RunAsync(() => songs.ListAsync(cancellationToken)).ConfigureAwait(false);
        return new SongListPayload(result.Truncated, [.. result.Songs.Select(SongIpc.Map)]);
    }
}

internal sealed class SongGetHandler(ISongRepository songs) : IIpcHandler
{
    public string Kind => IpcKind.Query;
    public string Type => IpcTypes.SongGet;
    public async Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var r = request.GetPayload<SongGetRequest>();
        var song = await SongIpc.RunAsync(() => songs.GetAsync(r.Id, cancellationToken)).ConfigureAwait(false)
            ?? throw new IpcException(SongErrorCodes.NotFound, "La canción no existe (puede que se haya borrado).");
        return SongIpc.Map(song);
    }
}

/// <summary>Interpreta un texto (función pura, sin BD ni disco): cabe en el carril en vivo. El texto no se registra; el parser nunca lanza.</summary>
internal sealed class SongParseTextHandler : IIpcHandler
{
    public string Kind => IpcKind.Query;
    public string Type => IpcTypes.SongParseText;
    public Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var r = request.GetPayload<SongParseTextRequest>();
        var parsed = SongTextParser.Parse(r.Text, new SongParseOptions(r.MergeIdenticalBlocks));
        return Task.FromResult<object?>(new SongParseTextPayload([.. parsed.Sections.Select(SongIpc.Map)], parsed.Arrangement, parsed.Warnings, parsed.Errors, SongIpc.Detected(parsed.Format)));
    }
}

internal sealed class SongHistoryHandler(ISongRepository songs) : IIpcHandler
{
    public string Kind => IpcKind.Query;
    public string Type => IpcTypes.SongHistory;
    public async Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var r = request.GetPayload<SongHistoryRequest>();
        var revisions = await SongIpc.RunAsync(() => songs.GetHistoryAsync(r.SongId, cancellationToken)).ConfigureAwait(false);
        return new SongHistoryPayload([.. revisions.Select(v => new SongRevisionDto(v.Id, SongIpc.Iso(v.CreatedAt), v.Reason, v.SectionCount))]);
    }
}

/// <summary>Configuración de la proyección de canciones. Lee <c>settings.json</c>: no toca la BD.</summary>
internal sealed class SongGetSettingsHandler(ISettingsStore settings) : IIpcHandler
{
    public string Kind => IpcKind.Query;
    public string Type => IpcTypes.SongGetSettings;
    public Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken) =>
        Task.FromResult<object?>(SongIpc.ToPayload(settings.Current));
}

/// <summary>Valida el conjunto completo ANTES de guardar nada (un valor inválido no cambia nada).</summary>
internal sealed class SongSetSettingsHandler(ISettingsStore settings) : IIpcHandler
{
    public string Kind => IpcKind.Command;
    public string Type => IpcTypes.SongSetSettings;
    public async Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var r = request.GetPayload<SongSettingsPayload>();
        if (r.Theme is null) throw new IpcException(SongErrorCodes.InvalidSettings, "Canciones: falta el tema.");
        var display = new SongDisplaySettings(r.Theme, r.TransitionMs, r.AtEnd);
        if (SongDisplaySettings.Validate(display) is { } problem) throw new IpcException(SongErrorCodes.InvalidSettings, problem);
        await settings.UpdateAsync(s => s with { SongDisplay = display }, cancellationToken).ConfigureAwait(false);
        return null;
    }
}

/// <summary>Crea o actualiza una canción. El resultado (revisión nueva) llega como <c>song.changed</c>; un fallo, como <c>command.failed</c>. Va por el carril LENTO: puede esperar el bloqueo de escritura.</summary>
internal sealed class SongSaveHandler(ISongRepository songs, IOutbox outbox) : IIpcHandler
{
    public string Kind => IpcKind.Command;
    public string Type => IpcTypes.SongSave;
    public async Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var r = request.GetPayload<SongSavePayload>();
        var reason = r.Reason switch
        {
            null or "" or "save" => SongSaveReason.Save,
            "import" => SongSaveReason.Import,
            _ => throw new IpcException(SongErrorCodes.InvalidRequest, "El motivo del guardado debe ser «save» o «import»."),
        };
        var save = new SongSaveRequest(r.Id, r.ExpectedRevision, r.Title ?? "", r.Author, r.Copyright, r.CcliNumber, r.Notes,
            r.Background is { } b ? new SongBackground(b.Kind, b.Path, b.Color, b.Muted) : null,
            [.. (r.Sections ?? []).Select(s => new SongSectionInput(s.Id, s.Label ?? "", s.Kind, s.Text ?? ""))], r.Arrangement ?? [], reason);
        var result = await SongIpc.RunAsync(() => songs.SaveAsync(save, cancellationToken)).ConfigureAwait(false);
        SongIpc.PublishChanged(outbox, request.Id, result.SongId, result.Revision, result.Created ? "created" : "updated", 0);
        return null;
    }
}

internal sealed class SongRestoreRevisionHandler(ISongRepository songs, IOutbox outbox) : IIpcHandler
{
    public string Kind => IpcKind.Command;
    public string Type => IpcTypes.SongRestoreRevision;
    public async Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var r = request.GetPayload<SongRestoreRevisionPayload>();
        var result = await SongIpc.RunAsync(() => songs.RestoreRevisionAsync(r.SongId, r.RevisionId, r.ExpectedRevision, cancellationToken)).ConfigureAwait(false);
        SongIpc.PublishChanged(outbox, request.Id, result.SongId, result.Revision, "restored", 0);
        return null;
    }
}

internal sealed class SongDeleteHandler(ISongRepository songs, IOutbox outbox) : IIpcHandler
{
    public string Kind => IpcKind.Command;
    public string Type => IpcTypes.SongDelete;
    public async Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var r = request.GetPayload<SongDeletePayload>();
        var result = await SongIpc.RunAsync(() => songs.DeleteAsync(r.Id, cancellationToken)).ConfigureAwait(false);
        SongIpc.PublishChanged(outbox, request.Id, result.SongId, 0, "deleted", result.UsedInServices);
        return null;
    }
}
