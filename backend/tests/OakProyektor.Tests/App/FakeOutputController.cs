using OakProyektor.App;
using OakProyektor.Core.Display;
using OakProyektor.Core.Output;
using OakProyektor.Ipc.Messages;

namespace OakProyektor.Tests.App;

internal sealed class FakeOutputController : IOutputController
{
    public List<string> Calls { get; } = [];
    public OutputStateChangedPayload Snapshot() => new(OutputState.Initial, new OutputWindowStatus(false, null, false));
    public void Open(int? displayIndex) => Calls.Add($"open:{displayIndex?.ToString() ?? "auto"}");
    public void Close() => Calls.Add("close");
    public void SetMaster(MasterMode master) => Calls.Add($"master:{master}");
    public void CloseForShutdown() => Calls.Add("shutdown");
}

internal sealed class FakeDisplayManager(params DisplayInfo[] displays) : IDisplayManager
{
    public IReadOnlyList<DisplayInfo> Current => displays;
    public IReadOnlyList<DisplayInfo> Refresh() => displays;
    public DisplayInfo? Resolve(int? requestedIndex) => displays.FirstOrDefault(d => d.Index == requestedIndex) ?? displays.FirstOrDefault(d => !d.HostsPanel);
}
