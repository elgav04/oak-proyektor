using OakProyektor.Core;
using OakProyektor.Core.Alerts;
using OakProyektor.Core.Output;
using OakProyektor.Data;
using OakProyektor.Data.Alerts;
using OakProyektor.Ipc;
using OakProyektor.Ipc.Messages;

namespace OakProyektor.App.Handlers;

/// <summary>
/// Alertas rápidas (protocolo v15). Los mensajes de error y los registros NUNCA llevan el texto de una alerta: solo códigos e ids. <c>alert.show</c> y <c>alert.hide</c> van por el carril de
/// PRIORIDAD y solo tocan memoria (el uso se anota en segundo plano); guardar y borrar, por el LENTO; las lecturas y los ajustes, por el EN VIVO.
/// </summary>
internal static class AlertIpc
{
    public static async Task<T> RunAsync<T>(Func<Task<T>> action)
    {
        try { return await action().ConfigureAwait(false); }
        catch (AlertException ex) { throw new IpcException(ex.Code, ex.Message, ex); }
        catch (DatabaseUnavailableException ex) { throw new IpcException("database.unavailable", ex.Message, ex); }
    }

    public static AlertSettingsPayload ToPayload(AppSettings s) => new(s.Alerts.Mode, s.Alerts.DurationSec, s.Alerts.EffectiveTheme);

    public static SavedAlertDto Map(SavedAlert a) =>
        new(a.Id, a.Text, a.Pinned, a.UseCount, a.LastUsedAt is { } t ? AlertStore.Stamp(t) : null);

    public static void PublishChanged(IOutbox outbox, string commandId, long alertId, string kind) =>
        outbox.Publish(IpcEnvelope.Create(IpcKind.Event, IpcTypes.AlertChanged, new AlertChangedPayload(commandId, alertId, kind)), Targets.Control);
}

internal sealed class AlertShowHandler(IOutputController output, IAlertRecorder recorder) : IIpcHandler
{
    public string Kind => IpcKind.Command;
    public string Type => IpcTypes.AlertShow;
    public Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var p = request.GetPayload<AlertShowPayload>();
        var problem = AlertRules.TryNormalizeText(p.Text, out var text) ?? AlertRules.ValidateMode(p.Mode) ?? AlertRules.ValidateDuration(p.DurationSec);
        if (problem is not null) throw new IpcException(AlertErrorCodes.InvalidContent, problem);
        // El número de llegada permite ignorar este «mostrar» si el operador quitó la alerta (atajo global) DESPUÉS de pedirlo.
        if (output.ShowAlert(text, p.Mode, p.DurationSec, request.Order)) recorder.Record(text);   // anotar solo ENCOLA: no espera a la base de datos
        return Task.FromResult<object?>(null);
    }
}

internal sealed class AlertHideHandler(IOutputController output) : IIpcHandler
{
    public string Kind => IpcKind.Command;
    public string Type => IpcTypes.AlertHide;
    public Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        output.HideAlert(request.Order);
        return Task.FromResult<object?>(null);
    }
}

/// <summary>Lee <c>settings.json</c>: no toca la BD.</summary>
internal sealed class AlertGetSettingsHandler(ISettingsStore settings) : IIpcHandler
{
    public string Kind => IpcKind.Query;
    public string Type => IpcTypes.AlertGetSettings;
    public Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken) =>
        Task.FromResult<object?>(AlertIpc.ToPayload(settings.Current));
}

/// <summary>Valida ANTES de guardar nada (un valor inválido no cambia nada). No toca la alerta que ya está en pantalla: los ajustes valen para la siguiente.</summary>
internal sealed class AlertSetSettingsHandler(ISettingsStore settings) : IIpcHandler
{
    public string Kind => IpcKind.Command;
    public string Type => IpcTypes.AlertSetSettings;
    public async Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var r = request.GetPayload<AlertSettingsPayload>();
        var display = new AlertDisplaySettings(r.Mode, r.DurationSec, r.Theme);
        if (r.Theme is null) throw new IpcException(AlertErrorCodes.InvalidSettings, "Alertas: falta el tema.");
        if (AlertRules.ValidateSettings(display) is { } problem) throw new IpcException(AlertErrorCodes.InvalidSettings, problem);
        await settings.UpdateAsync(s => s with { AlertDisplay = display }, cancellationToken).ConfigureAwait(false);
        return null;
    }
}

internal sealed class AlertListHandler(IAlertRepository alerts) : IIpcHandler
{
    public string Kind => IpcKind.Query;
    public string Type => IpcTypes.AlertList;
    public async Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var list = await AlertIpc.RunAsync(() => alerts.ListAsync(cancellationToken)).ConfigureAwait(false);
        return new AlertListPayload([.. list.Pinned.Select(AlertIpc.Map)], [.. list.Recent.Select(AlertIpc.Map)]);
    }
}

internal sealed class AlertSaveHandler(IAlertRepository alerts, IOutbox outbox) : IIpcHandler
{
    public string Kind => IpcKind.Command;
    public string Type => IpcTypes.AlertSave;
    public async Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var r = request.GetPayload<AlertSavePayload>();
        var saved = await AlertIpc.RunAsync(() => alerts.SaveAsync(r.Id, r.Text, r.Pinned, cancellationToken)).ConfigureAwait(false);
        AlertIpc.PublishChanged(outbox, request.Id, saved.Id, "saved");
        return null;
    }
}

internal sealed class AlertDeleteHandler(IAlertRepository alerts, IOutbox outbox) : IIpcHandler
{
    public string Kind => IpcKind.Command;
    public string Type => IpcTypes.AlertDelete;
    public async Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var r = request.GetPayload<AlertDeletePayload>();
        await AlertIpc.RunAsync(() => alerts.DeleteAsync(r.Id, cancellationToken)).ConfigureAwait(false);
        AlertIpc.PublishChanged(outbox, request.Id, r.Id, "deleted");
        return null;
    }
}
