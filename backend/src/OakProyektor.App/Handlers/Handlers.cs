using OakProyektor.Ipc;
using OakProyektor.Ipc.Messages;

namespace OakProyektor.App.Handlers;

internal sealed class DisplayListHandler(IDisplayManager displays) : IIpcHandler
{
    public string Kind => IpcKind.Query;
    public string Type => IpcTypes.DisplayList;
    public Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken) =>
        Task.FromResult<object?>(new DisplayListPayload(displays.Refresh()));
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
        output.Open(request.GetPayload<OutputOpenPayload>().DisplayIndex);
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
