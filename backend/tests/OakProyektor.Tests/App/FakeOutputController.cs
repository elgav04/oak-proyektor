using OakProyektor.App;
using OakProyektor.Core.Display;
using OakProyektor.Core.Output;
using OakProyektor.Ipc.Messages;

namespace OakProyektor.Tests.App;

internal sealed class FakeOutputController : IOutputController
{
    public List<string> Calls { get; } = [];
    /// <summary>El estado que devuelve <c>Snapshot</c> (los tests lo cambian, p. ej. para simular un fondo en la salida).</summary>
    public OutputState State { get; set; } = OutputState.Initial;
    public OutputStateChangedPayload Snapshot() => new(1, State, new OutputWindowStatus(false, null, false, false, null, true, null, null, DisplayLostPolicy.WarnAndWait, false, false, []));
    /// <summary>El número de llegada recibido en cada llamada a <c>Open</c> / <c>EmergencyPause</c>, en orden.</summary>
    public List<long> Orders { get; } = [];
    public void Open(string? displayId, long order = 0) { Calls.Add($"open:{displayId ?? "auto"}"); Orders.Add(order); }
    public void Close() => Calls.Add("close");
    public void SetMaster(MasterMode master) => Calls.Add($"master:{master}");
    public List<BackgroundLayer> Backgrounds { get; } = [];
    public void SetBackground(BackgroundLayer background) { Calls.Add("background:" + background.Kind); Backgrounds.Add(background); }
    public void RetryBackground() => Calls.Add("retryBackground");
    public SlideOrigin? LastOrigin { get; private set; }
    public void ShowContent(IReadOnlyList<string> blocks, TextTheme theme, int transitionMs, SlideOrigin? origin = null) { LastOrigin = origin; Calls.Add($"showContent:{blocks.Count}:{transitionMs}"); }
    public void ClearContent(int transitionMs) => Calls.Add($"clearContent:{transitionMs}");
    /// <summary>Las alertas recibidas (texto ya normalizado, modo, duración y número de llegada), en orden.</summary>
    public List<(string Text, AlertMode Mode, int DurationSec, long Order)> Alerts { get; } = [];
    /// <summary>Si es false, <c>ShowAlert</c> responde «ignorada» (como cuando el operador la quitó después de pedirla).</summary>
    public bool AlertApplied { get; set; } = true;
    public bool ShowAlert(string text, AlertMode mode, int durationSec, long order = 0) { Calls.Add($"showAlert:{mode}:{durationSec}"); Alerts.Add((text, mode, durationSec, order)); Orders.Add(order); return AlertApplied; }
    public void HideAlert(long order = 0) { Calls.Add("hideAlert"); Orders.Add(order); }
    public void ReportBackgroundFailure(string mediaUrl, BackgroundFailureReason reason, string? detail) => Calls.Add($"backgroundFailure:{reason}:{mediaUrl}");
    public void CloseForShutdown() => Calls.Add("shutdown");
    public void EmergencyPause(OutputPauseReason reason, long order = 0) { Calls.Add($"pause:{reason}"); Orders.Add(order); }
    public void SettingsChanged() => Calls.Add("settingsChanged");
    public void SetEmergencyHotkey(string? description) => Calls.Add($"hotkey:{description ?? "none"}");
    public void SetBackupHotkeys(IReadOnlyList<HotkeyStatus> hotkeys) => Calls.Add($"backupHotkeys:{hotkeys.Count}");
    public void ToggleMaster(MasterMode requested) => Calls.Add($"toggle:{requested}");
    public void SetPanelUnresponsive(bool unresponsive) => Calls.Add($"panelUnresponsive:{unresponsive}");
}

internal sealed class FakeDisplayManager(params DisplayInfo[] displays) : IDisplayManager
{
    public IReadOnlyList<DisplayInfo> Current => displays;
    public event Action<IReadOnlyList<DisplayInfo>>? Polled { add { } remove { } }
    public IReadOnlyList<DisplayInfo> Refresh() => displays;
    public DisplayListPayload Snapshot() => new(1, displays);
    public DisplayResolution Resolve(string? requestedId, string? preferredId = null) => DisplayResolver.Resolve(displays, requestedId, preferredId);
    public bool DuplicateMode => false;
}




internal sealed class InMemorySettings : ISettingsStore
{
    public OakProyektor.Core.AppSettings Current { get; private set; } = new();
    public IReadOnlyList<string> Warnings { get; set; } = [];
    public Task Loaded => Task.CompletedTask;
    public Task LoadAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task UpdateAsync(Func<OakProyektor.Core.AppSettings, OakProyektor.Core.AppSettings> change, CancellationToken cancellationToken)
    {
        Current = change(Current);
        return Task.CompletedTask;
    }
}
