using OakProyektor.Core;
using OakProyektor.Core.Scripture;
using OakProyektor.Data;
using OakProyektor.Data.Scripture;
using OakProyektor.Ipc;
using OakProyektor.Ipc.Messages;

namespace OakProyektor.App.Handlers;

/// <summary>Convierte los fallos esperados del módulo de Biblias en errores de protocolo (el resto sigue su camino: <c>internal</c> sin detalles).</summary>
internal static class BibleIpc
{
    public static async Task<T> RunAsync<T>(Func<Task<T>> action)
    {
        try { return await action().ConfigureAwait(false); }
        catch (BibleException ex) { throw new IpcException(ex.Code, ex.Message, ex); }
        catch (DatabaseUnavailableException ex) { throw new IpcException("database.unavailable", ex.Message, ex); }
    }

    public static BiblePassagePayload Map(BiblePassage p) =>
        new(p.Bible, p.Attribution, p.Reference, [.. p.Verses.Select(v => new BibleVerseDto(v.BookNumber, v.BookName, v.Chapter, v.VerseNumber, v.Text))]);

    public static BibleImportReportDto Map(ImportReport r) =>
        new(r.BibleId, r.CreatedBible, r.NumberingProfile, r.SourceVerses, r.Inserted, r.Updated, r.Unchanged, r.Deleted, r.CleanedVerses, r.JoinedVerses,
            r.Validation.Books, r.Validation.Chapters, r.Validation.Verses, r.Validation.EmptyVerses, r.Validation.DifferenceFromStandardVerseTotal, r.FtsIntegrityOk,
            [.. r.AffectedItems.Select(a => new AffectedItemDto(a.Kind, a.Id, a.PlaylistId, a.Reference))],
            [.. r.Warnings.Concat(r.Validation.Warnings)], r.ElapsedMs,
            r.PreviousVerses, r.BackupPath, r.NumberingProfileKept, r.DiscardedVerses, r.DuplicateChaptersDifferent);
}

internal sealed class BibleListHandler(IBibleQueryService bibles) : IIpcHandler
{
    public string Kind => IpcKind.Query;
    public string Type => IpcTypes.BibleList;
    public async Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var list = await BibleIpc.RunAsync(() => bibles.ListBiblesAsync(cancellationToken)).ConfigureAwait(false);
        return new BibleListPayload([.. list.Select(b => new BibleDto(b.Id, b.Abbreviation, b.Name, b.Language, b.Attribution, b.NumberingProfile, b.Books, b.Verses))]);
    }
}

internal sealed class BibleLookupHandler(IBibleQueryService bibles) : IIpcHandler
{
    public string Kind => IpcKind.Query;
    public string Type => IpcTypes.BibleLookup;
    public async Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var r = request.GetPayload<BibleLookupRequest>();
        return BibleIpc.Map(await BibleIpc.RunAsync(() => bibles.LookupAsync(r.Reference, r.Bible, r.Record, cancellationToken)).ConfigureAwait(false));
    }
}

internal sealed class BibleChapterHandler(IBibleQueryService bibles) : IIpcHandler
{
    public string Kind => IpcKind.Query;
    public string Type => IpcTypes.BibleChapter;
    public async Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var r = request.GetPayload<BibleChapterRequest>();
        return BibleIpc.Map(await BibleIpc.RunAsync(() => bibles.GetChapterAsync(r.Bible, r.BookNumber, r.Chapter, cancellationToken)).ConfigureAwait(false));
    }
}

internal sealed class BibleSearchHandler(IBibleQueryService bibles) : IIpcHandler
{
    public string Kind => IpcKind.Query;
    public string Type => IpcTypes.BibleSearch;
    public async Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var r = request.GetPayload<BibleSearchRequest>();
        var result = await BibleIpc.RunAsync(() => bibles.SearchAsync(r.Query, r.Bible, r.Limit, cancellationToken)).ConfigureAwait(false);
        return new BibleSearchPayload(result.Query, result.Truncated,
            [.. result.Hits.Select(h => new BibleSearchHitDto(h.Bible, h.BookNumber, h.BookName, h.Chapter, h.VerseNumber, h.Fragment))]);
    }
}

internal sealed class BibleHistoryHandler(IBibleQueryService bibles) : IIpcHandler
{
    public string Kind => IpcKind.Query;
    public string Type => IpcTypes.BibleHistory;
    public async Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var r = request.GetPayload<BibleHistoryRequest>();
        var entries = await BibleIpc.RunAsync(() => bibles.GetHistoryAsync(r.Limit, cancellationToken)).ConfigureAwait(false);
        return new BibleHistoryPayload([.. entries.Select(e => new CitationDto(e.Id, e.Bible, e.BookNumber, e.BookName, e.Chapter, e.VerseStart, e.VerseEnd, e.Label, e.CitedAt))]);
    }
}

/// <summary>Autocompletado del libro: no toca la BD (los alias viven en código), así que responde al instante.</summary>
internal sealed class BibleSuggestHandler : IIpcHandler
{
    public string Kind => IpcKind.Query;
    public string Type => IpcTypes.BibleSuggest;
    public Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var r = request.GetPayload<BibleSuggestRequest>();
        var limit = r.Limit <= 0 ? 8 : Math.Min(r.Limit, 20);
        return Task.FromResult<object?>(new BibleSuggestPayload(
            [.. BookNames.Suggest(r.Prefix, limit).Select(s => new BibleSuggestionDto(s.BookNumber, s.Name, s.ShortName, s.MatchedAlias))]));
    }
}

internal sealed class BibleSetAttributionHandler(IBibleQueryService bibles) : IIpcHandler
{
    public string Kind => IpcKind.Command;
    public string Type => IpcTypes.BibleSetAttribution;
    public async Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var r = request.GetPayload<BibleSetAttributionPayload>();
        await BibleIpc.RunAsync(async () => { await bibles.SetAttributionAsync(r.Bible, r.Attribution, cancellationToken).ConfigureAwait(false); return true; }).ConfigureAwait(false);
        return null;
    }
}

internal static class BibleSettingsMapper
{
    public static BibleSettingsPayload ToPayload(AppSettings s)
    {
        var b = s.Bible;
        return new BibleSettingsPayload(s.DefaultBible, b.ProjectOnNavigate, b.SuperscriptNumbers, b.ShowAttribution, b.TransitionMs, b.EffectiveTheme,
            [.. (b.ShortNames ?? []).Select(n => new BibleShortNameDto(n.Bible, n.ShortName))]);
    }
}

/// <summary>Configuración de la proyección de Biblia (v8). Lee <c>settings.json</c>: no toca la BD.</summary>
internal sealed class BibleGetSettingsHandler(ISettingsStore settings) : IIpcHandler
{
    public string Kind => IpcKind.Query;
    public string Type => IpcTypes.BibleGetSettings;
    public Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken) =>
        Task.FromResult<object?>(BibleSettingsMapper.ToPayload(settings.Current));
}

/// <summary>Valida el conjunto completo ANTES de guardar nada (un valor inválido no cambia nada) y comprueba que la Biblia por defecto esté instalada.</summary>
internal sealed class BibleSetSettingsHandler(ISettingsStore settings, IBibleQueryService bibles) : IIpcHandler
{
    public string Kind => IpcKind.Command;
    public string Type => IpcTypes.BibleSetSettings;
    public async Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var r = request.GetPayload<BibleSetSettingsPayload>();
        var display = new BibleDisplaySettings(r.ProjectOnNavigate, r.SuperscriptNumbers, r.ShowAttribution, r.TransitionMs, r.Theme,
            [.. (r.ShortNames ?? []).Select(n => new BibleShortName(n.Bible, n.ShortName))]);
        if (r.Theme is null) throw new IpcException(BibleSettingsErrors.Invalid, "Biblia: falta el tema.");
        if (BibleDisplaySettings.Validate(display) is { } problem) throw new IpcException(BibleSettingsErrors.Invalid, problem);

        var defaultBible = string.IsNullOrWhiteSpace(r.DefaultBible) ? null : r.DefaultBible;
        if (defaultBible is not null)
        {
            var installed = await BibleIpc.RunAsync(() => bibles.ListBiblesAsync(cancellationToken)).ConfigureAwait(false);
            if (installed.All(b => b.Abbreviation != defaultBible))
                throw new IpcException(BibleErrorCodes.NotFound, $"La Biblia «{defaultBible}» no está instalada.");
        }
        // El tema lleva el ajuste automático como red de seguridad; el resto se guarda tal cual.
        await settings.UpdateAsync(s => s with { DefaultBible = defaultBible, BibleDisplay = display }, cancellationToken).ConfigureAwait(false);
        return null;
    }
}

internal static class BibleSettingsErrors
{
    public const string Invalid = "bible.invalidSettings";
}

/// <summary>El comando solo ARRANCA la importación (en otro hilo) y vuelve: el handler no debe retener el carril de entrada durante segundos.</summary>
internal sealed class BibleImportHandler(BibleImportService imports) : IIpcHandler
{
    public string Kind => IpcKind.Command;
    public string Type => IpcTypes.BibleImport;
    public Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        imports.Start(request.GetPayload<BibleImportPayload>());
        return Task.FromResult<object?>(null);
    }
}
