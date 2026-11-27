using OakProyektor.App.Audio;
using OakProyektor.Core.Audio;
using OakProyektor.Ipc.Messages;

namespace OakProyektor.Tests.Playback;

/// <summary>Enumerador de dispositivos SIMULADO: los tests conectan, desconectan y cambian el predeterminado a voluntad, y los suscriptores se enteran como con Windows.</summary>
internal sealed class FakeDeviceEnumerator : IAudioDeviceEnumerator
{
    private readonly object _gate = new();
    private readonly List<AudioDeviceInfo> _active = [];
    private AudioDeviceInfo? _default;

    public FakeDeviceEnumerator(params AudioDeviceInfo[] active)
    {
        _active.AddRange(active);
        _default = active.FirstOrDefault();
    }

    public event Action? Changed;

    public IReadOnlyList<AudioDeviceInfo> GetActiveDevices() { lock (_gate) return [.. _active]; }
    public AudioDeviceInfo? GetDefaultDevice() { lock (_gate) return _default; }

    public void Connect(AudioDeviceInfo device, bool makeDefault = false)
    {
        lock (_gate) { _active.RemoveAll(d => d.Id == device.Id); _active.Add(device); if (makeDefault) _default = device; }
        Changed?.Invoke();
    }

    public void Disconnect(string id)
    {
        lock (_gate)
        {
            _active.RemoveAll(d => d.Id == id);
            if (_default?.Id == id) _default = _active.FirstOrDefault();   // Windows pasa solo al siguiente (p. ej. los altavoces del portátil)
        }
        Changed?.Invoke();
    }

    public void SetDefault(string id)
    {
        lock (_gate) _default = _active.First(d => d.Id == id);
        Changed?.Invoke();
    }

    public void Dispose() { }
}

/// <summary>Reproducción simulada: registra lo que el motor le pide y deja a los tests terminarla, perderla o hacerla fallar.</summary>
internal sealed class FakePlayback : IAudioPlayback
{
    public List<string> Calls { get; } = [];
    public long PositionMs { get; set; }
    public long DurationMs { get; init; } = 60_000;
    public float Gain { get; private set; }
    public bool Disposed { get; private set; }
    public event Action<PlaybackEndReason, string?>? Ended;

    public void Play(int fadeInMs) => Calls.Add($"play:{fadeInMs}");
    public void Pause(int fadeMs) => Calls.Add($"pause:{fadeMs}");
    public void Seek(long positionMs) { PositionMs = positionMs; Calls.Add($"seek:{positionMs}"); }
    public void SetGain(float gain, int rampMs) { Gain = gain; Calls.Add($"gain:{gain:0.00}:{rampMs}"); }
    public void Stop(int fadeMs) { Calls.Add($"stop:{fadeMs}"); }
    public void Dispose() => Disposed = true;
    public void Raise(PlaybackEndReason reason, string? detail = null) => Ended?.Invoke(reason, detail);
}

internal sealed class FakeAudioBackend : IAudioBackend
{
    public List<(string? DeviceId, bool Loop, float Gain)> Opened { get; } = [];
    public List<FakePlayback> Playbacks { get; } = [];
    /// <summary>Si no es nulo, <c>Open</c> espera a esta señal (una apertura lenta de dispositivo).</summary>
    public ManualResetEventSlim? OpenGate { get; set; }
    public ManualResetEventSlim OpenEntered { get; } = new(false);
    public Func<string?, Exception?>? FailOpen { get; set; }
    public List<(string? DeviceId, int Hz)> Tones { get; } = [];

    public IAudioPlayback Open(string? deviceId, string filePath, bool loop, float gain)
    {
        OpenEntered.Set();
        OpenGate?.Wait(TimeSpan.FromSeconds(10));
        if (FailOpen?.Invoke(deviceId) is { } ex) throw ex;
        var pb = new FakePlayback();
        lock (Playbacks) { Opened.Add((deviceId, loop, gain)); Playbacks.Add(pb); }
        return pb;
    }

    public Task PlayToneAsync(string? deviceId, int frequencyHz, int durationMs, float gain, CancellationToken cancellationToken)
    {
        Tones.Add((deviceId, frequencyHz));
        return Task.CompletedTask;
    }
}

/// <summary>Servicio de audio de mentira para los tests de handlers: solo anota las llamadas.</summary>
internal sealed class FakeAudioService : IAudioService
{
    public List<string> Calls { get; } = [];
    public AudioStatePayload GetState() => new(1, new AudioRouteDto(true, null, null, true, null, null, false), [], null,
        new AudioSettingsPayload(null, null, 50, 500, 800, 400, 400, false), null);
    public void PlayTrack(long mediaId, string path, long durationMs, bool loop, bool startPlaying, int volumePct, int? fadeInMs, long order) => Calls.Add($"play:{mediaId}:{startPlaying}:{order}");
    public void PauseTrack(long order) => Calls.Add($"pause:{order}");
    public void ResumeTrack(long order) => Calls.Add($"resume:{order}");
    public void SeekTrack(long positionMs) => Calls.Add($"seek:{positionMs}");
    public void SetTrackVolume(int volumePct) => Calls.Add($"volume:{volumePct}");
    public void StopTrack(long order) => Calls.Add($"stop:{order}");
    public void SilenceTracks(long order) => Calls.Add($"silence:{order}");
    public void SettingsApplied() => Calls.Add("settingsApplied");
    public Task TestToneAsync(AudioTestTarget target, CancellationToken cancellationToken) { Calls.Add($"test:{target}"); return Task.CompletedTask; }
}
