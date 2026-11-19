using OakProyektor.App;
using OakProyektor.Core.Display;
using OakProyektor.Core.Output;
using OakProyektor.Ipc.Messages;

namespace OakProyektor.Tests.App;

internal sealed class FakeOutputController : IOutputController
{
    public List<string> Calls { get; } = [];
    public OutputStateChangedPayload Snapshot() => new(OutputState.Initial, new OutputWindowStatus(false, null, false, false, null, true, null));
    public void Open(int? displayIndex) => Calls.Add($"open:{displayIndex?.ToString() ?? "auto"}");
    public void Close() => Calls.Add("close");
    public void SetMaster(MasterMode master) => Calls.Add($"master:{master}");
    public void CloseForShutdown() => Calls.Add("shutdown");
    public void EmergencyPause(OutputPauseReason reason) => Calls.Add($"pause:{reason}");
    public void SettingsChanged() => Calls.Add("settingsChanged");
    public void SetEmergencyHotkey(string? description) => Calls.Add($"hotkey:{description ?? "none"}");
}

internal sealed class FakeDisplayManager(params DisplayInfo[] displays) : IDisplayManager
{
    public IReadOnlyList<DisplayInfo> Current => displays;
    public event Action<IReadOnlyList<DisplayInfo>>? Polled { add { } remove { } }
    public IReadOnlyList<DisplayInfo> Refresh() => displays;
    public DisplayInfo? Resolve(int? requestedIndex) => displays.FirstOrDefault(d => d.Index == requestedIndex) ?? displays.FirstOrDefault(d => !d.HostsPanel);
}




internal sealed class InMemorySettings : ISettingsStore
{
    public OakProyektor.Core.AppSettings Current { get; private set; } = new();
    public Task LoadAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task UpdateAsync(Func<OakProyektor.Core.AppSettings, OakProyektor.Core.AppSettings> change, CancellationToken cancellationToken)
    {
        Current = change(Current);
        return Task.CompletedTask;
    }
}
