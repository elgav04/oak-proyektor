using OakProyektor.Core.Output;
using OakProyektor.Ipc;
using OakProyektor.Ipc.Messages;

namespace OakProyektor.App.Handlers;

internal sealed class DisplayListHandler(IDisplayManager displays) : IIpcHandler
{
    private DisplayListPayload RefreshAndSnapshot()
    {
        displays.Refresh();
        return displays.Snapshot();
    }

    public string Kind => IpcKind.Query;
    public string Type => IpcTypes.DisplayList;
    public Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken) =>
        Task.FromResult<object?>(RefreshAndSnapshot());
}

internal sealed class OutputGetStateHandler(IOutputController output) : IIpcHandler
{
    public string Kind => IpcKind.Query;
    public string Type => IpcTypes.OutputGetState;
    public Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken) =>
        Task.FromResult<object?>(output.Snapshot());
}

internal sealed class OutputOpenHandler(IOutputController output) : IIpcHandler
{
    public string Kind => IpcKind.Command;
    public string Type => IpcTypes.OutputOpen;
    public Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        output.Open(request.GetPayload<OutputOpenPayload>().DisplayId, request.Order);
        return Task.FromResult<object?>(null);
    }
}

internal sealed class OutputCloseHandler(IOutputController output) : IIpcHandler
{
    public string Kind => IpcKind.Command;
    public string Type => IpcTypes.OutputClose;
    public Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        output.Close();
        return Task.FromResult<object?>(null);
    }
}

internal sealed class OutputSetMasterHandler(IOutputController output) : IIpcHandler
{
    public string Kind => IpcKind.Command;
    public string Type => IpcTypes.OutputSetMaster;
    public Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        output.SetMaster(request.GetPayload<OutputSetMasterPayload>().Master);
        return Task.FromResult<object?>(null);
    }
}

/// <summary>
/// <c>output.setBackground</c>: valida ANTES de enviar nada a la salida (ruta canónica, existe, extensión permitida, sin rutas de red). La salida recibe solo la URL ya validada.
/// La comprobación toca el disco (una unidad desconectada puede tardar): corre en el pool y con tope de tiempo, para no retener el carril de entrada indefinidamente.
/// </summary>
internal sealed class OutputSetBackgroundHandler(IOutputController output, IMediaFileSystem fileSystem, TimeSpan? validationTimeout = null) : IIpcHandler
{
    internal static readonly TimeSpan DefaultValidationTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// La comprobación del disco es síncrona e incancelable (una unidad atascada bloquea el hilo hasta que el sistema la suelta): no se puede ABANDONAR, así que se LIMITA a UNA a la vez.
    /// Si la anterior sigue colgada, las nuevas se rechazan al instante en lugar de bloquear otro hilo del pool por cada intento (H9).
    /// </summary>
    private Task? _validationInFlight;
    private readonly object _validationGate = new();

    public string Kind => IpcKind.Command;
    public string Type => IpcTypes.OutputSetBackground;
    public async Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var p = request.GetPayload<OutputSetBackgroundPayload>();
        if (!Enum.IsDefined(p.Kind)) throw new IpcException("output.invalidContent", "Tipo de fondo desconocido.");
        if (ContentValidator.ValidateTransition(p.TransitionMs, "Fondo") is { } badTransition) throw new IpcException("output.invalidContent", badTransition);
        var color = p.Color ?? BackgroundLayer.DefaultColor;
        if (ContentValidator.ValidateColor(color, "Fondo") is { } badColor) throw new IpcException("output.invalidContent", badColor);

        BackgroundLayer layer;
        switch (p.Kind)
        {
            case BackgroundKind.None:
                layer = BackgroundLayer.Empty with { TransitionMs = p.TransitionMs };
                break;
            case BackgroundKind.Color:
                layer = BackgroundLayer.OfColor(color, p.TransitionMs);
                break;
            default:
                var expected = p.Kind == BackgroundKind.Video ? MediaKind.Video : MediaKind.Image;
                MediaValidation result;
                Task<MediaValidation> work;
                lock (_validationGate)
                {
                    if (_validationInFlight is { IsCompleted: false })
                        throw new IpcException("output.mediaRejected", "Una comprobación anterior sigue sin terminar (¿una unidad desconectada o muy lenta?). Espera unos segundos e inténtalo de nuevo.");
                    work = Task.Run(() => MediaPathValidator.Validate(p.Path, fileSystem, expected), CancellationToken.None);
                    _validationInFlight = work;
                }
                try
                {
                    result = await work.WaitAsync(validationTimeout ?? DefaultValidationTimeout, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    throw new IpcException("output.mediaRejected", "No se pudo comprobar el archivo a tiempo (¿la unidad está desconectada o es muy lenta?).");
                }
                if (!result.IsValid) throw new IpcException("output.mediaRejected", result.Message ?? "Archivo rechazado.");
                var media = result.Media!;
                layer = new BackgroundLayer(p.Kind, color, media.Url, media.FileName, p.Muted, p.TransitionMs, media.Removable ? BackgroundLayer.RemovableDriveWarning : null);
                break;
        }
        output.SetBackground(layer);
        return null;
    }
}

/// <summary><c>output.retryBackground</c>: «Reintentar» el fondo actual (típico: el USB volvió a conectarse). No revalida la ruta: la salida informa si el medio sigue fallando.</summary>
internal sealed class OutputRetryBackgroundHandler(IOutputController output) : IIpcHandler
{
    public string Kind => IpcKind.Command;
    public string Type => IpcTypes.OutputRetryBackground;
    public Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        output.RetryBackground();
        return Task.FromResult<object?>(null);
    }
}

internal sealed class OutputShowContentHandler(IOutputController output) : IIpcHandler
{
    public string Kind => IpcKind.Command;
    public string Type => IpcTypes.OutputShowContent;
    public Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var p = request.GetPayload<OutputShowContentPayload>();
        if (ContentValidator.ValidateSlide(p.Blocks, p.Theme, p.TransitionMs) is { } problem) throw new IpcException("output.invalidContent", problem);
        output.ShowContent(p.Blocks, p.Theme, p.TransitionMs);
        return Task.FromResult<object?>(null);
    }
}

internal sealed class OutputClearContentHandler(IOutputController output) : IIpcHandler
{
    public string Kind => IpcKind.Command;
    public string Type => IpcTypes.OutputClearContent;
    public Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var p = request.GetPayload<OutputClearContentPayload>();
        if (ContentValidator.ValidateTransition(p.TransitionMs, "Contenido") is { } problem) throw new IpcException("output.invalidContent", problem);
        output.ClearContent(p.TransitionMs);
        return Task.FromResult<object?>(null);
    }
}

internal sealed class OutputReportBackgroundFailureHandler(IOutputController output) : IIpcHandler
{
    public string Kind => IpcKind.Command;
    public string Type => IpcTypes.OutputReportBackgroundFailure;
    public Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var p = request.GetPayload<OutputReportBackgroundFailurePayload>();
        if (!Enum.IsDefined(p.Reason)) throw new IpcException("invalid", "Motivo de fallo desconocido.");
        output.ReportBackgroundFailure(p.MediaUrl, p.Reason, p.Detail);
        return Task.FromResult<object?>(null);
    }
}

internal sealed class OutputRecoverHandler(IOutputController output) : IIpcHandler
{
    public string Kind => IpcKind.Command;
    public string Type => IpcTypes.OutputRecover;
    public Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        // Solo Manual o Emergency: cualquier otro valor (o ausente) es Manual. Un cliente no puede pedir una pausa «automática».
        var reason = request.GetPayload<OutputRecoverPayload>().Reason == OakProyektor.Core.Output.OutputPauseReason.Emergency
            ? OakProyektor.Core.Output.OutputPauseReason.Emergency
            : OakProyektor.Core.Output.OutputPauseReason.Manual;
        output.EmergencyPause(reason, request.Order);
        return Task.FromResult<object?>(null);
    }
}

internal sealed class SettingsSetAutoRestoreHandler(ISettingsStore settings, IOutputController output) : IIpcHandler
{
    public string Kind => IpcKind.Command;
    public string Type => IpcTypes.SettingsSetAutoRestore;
    public async Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var enabled = request.GetPayload<SetAutoRestorePayload>().Enabled;
        await settings.UpdateAsync(s => s with { AutoRestoreOutput = enabled }, cancellationToken).ConfigureAwait(false);
        output.SettingsChanged();
        return null;
    }
}

internal sealed class SettingsSetDisplayLostPolicyHandler(ISettingsStore settings, IOutputController output) : IIpcHandler
{
    public string Kind => IpcKind.Command;
    public string Type => IpcTypes.SettingsSetDisplayLostPolicy;
    public async Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var policy = request.GetPayload<SetDisplayLostPolicyPayload>().Policy;
        if (!Enum.IsDefined(policy)) throw new IpcException("invalid", "Valor no válido para «Si el monitor de la salida desaparece».");
        await settings.UpdateAsync(s => s with { OnDisplayLost = policy }, cancellationToken).ConfigureAwait(false);
        output.SettingsChanged();
        return null;
    }
}

internal sealed class SettingsSetPanelAlertOnOutputHandler(ISettingsStore settings, IOutputController output) : IIpcHandler
{
    public string Kind => IpcKind.Command;
    public string Type => IpcTypes.SettingsSetPanelAlertOnOutput;
    public async Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var enabled = request.GetPayload<SetPanelAlertOnOutputPayload>().Enabled;
        await settings.UpdateAsync(s => s with { ShowPanelAlertOnOutput = enabled }, cancellationToken).ConfigureAwait(false);
        output.SettingsChanged();
        return null;
    }
}

internal sealed class DatabaseGetStatusHandler(DatabaseStatusReporter reporter) : IIpcHandler
{
    public string Kind => IpcKind.Query;
    public string Type => IpcTypes.DatabaseGetStatus;
    public Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken) =>
        Task.FromResult<object?>(reporter.Snapshot());
}
