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
        output.Open(request.GetPayload<OutputOpenPayload>().DisplayId);
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

internal sealed class OutputRecoverHandler(IOutputController output) : IIpcHandler
{
    public string Kind => IpcKind.Command;
    public string Type => IpcTypes.OutputRecover;
    public Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        output.EmergencyPause(OakProyektor.Core.Output.OutputPauseReason.Manual);
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
