using System.Globalization;
using Microsoft.Extensions.Logging;
using OakProyektor.Core;
using OakProyektor.Core.Audio;
using OakProyektor.Ipc;
using OakProyektor.Ipc.Messages;

namespace OakProyektor.App.Audio;

internal interface IAudioService
{
    /// <summary>Estado completo (ruta, dispositivos, pista, ajustes). Lee Windows en el momento: refleja el cableado de ahora.</summary>
    AudioStatePayload GetState();
    void PlayTrack(long mediaId, string path, long durationMs, bool loop, bool startPlaying, int volumePct, int? fadeInMs, long order);
    void PauseTrack(long order);
    void ResumeTrack(long order);
    void SeekTrack(long positionMs);
    void SetTrackVolume(int volumePct);
    void StopTrack(long order);
    /// <summary>«Silencio» del atajo global: calla la pista y la deja en pausa. Nunca reanuda. Solo toca memoria (no espera a ninguna apertura de dispositivo).</summary>
    void SilenceTracks(long order);
    /// <summary>Los ajustes cambiaron (volumen maestro, fundidos, dispositivo): aplica y publica el estado.</summary>
    void SettingsApplied();
    /// <summary>«Probar sonido»: un tono suave y corto por el dispositivo de la iglesia o por el predeterminado de Windows.</summary>
    Task TestToneAsync(AudioTestTarget target, CancellationToken cancellationToken);
}

/// <summary>
/// Pegamento del audio: el motor de pistas, el enumerador de dispositivos, los ajustes y la publicación al panel (<c>audio.stateChanged</c> en cada cambio y <c>media.progress</c> de la pista
/// ≈ 4 veces por segundo mientras suena). Recuerda el último cambio del dispositivo predeterminado de Windows (Windows lo cambia solo al conectar o quitar el USB) para que el panel avise
/// «cambió durante el servicio». NO cambia nunca el dispositivo predeterminado de Windows. Los registros llevan solo ids y conteos.
/// </summary>
internal sealed class AudioService : IAudioService, IDisposable
{
    private readonly IAudioBackend _backend;
    private readonly IAudioDeviceEnumerator _devices;
    private readonly ISettingsStore _settings;
    private readonly IOutbox _outbox;
    private readonly ILogger<AudioService> _logger;
    private readonly AudioEngine _engine;
    private readonly Timer _progress;
    private readonly SemaphoreSlim _toneGate = new(1, 1);
    private readonly object _gate = new();
    private long _seq;
    private string? _lastDefaultId;
    private string? _lastDefaultName;
    private AudioDefaultChangeDto? _defaultChange;

    public AudioService(IAudioBackend backend, IAudioDeviceEnumerator devices, ISettingsStore settings, IOutbox outbox, ILogger<AudioService> logger)
    {
        _backend = backend; _devices = devices; _settings = settings; _outbox = outbox; _logger = logger;
        var def = devices.GetDefaultDevice();
        _lastDefaultId = def?.Id; _lastDefaultName = def?.Name;
        _engine = new AudioEngine(backend, devices, () => settings.Current.AudioConfig, logger);
        _engine.Changed += Publish;
        _devices.Changed += OnDevicesChanged;
        _progress = new Timer(_ => TickProgress(), null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
    }

    private AudioSettings Config => _settings.Current.AudioConfig;

    private void OnDevicesChanged()
    {
        try
        {
            var def = _devices.GetDefaultDevice();
            lock (_gate)
            {
                if (!string.Equals(def?.Id, _lastDefaultId, StringComparison.Ordinal))
                {
                    // Windows cambió el dispositivo predeterminado (p. ej. al conectar o quitar el USB de la mezcladora): se registra y el panel lo avisa. No se hace nada más.
                    _defaultChange = new AudioDefaultChangeDto(_lastDefaultName, def?.Name, DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
                    _logger.LogWarning("El dispositivo de audio predeterminado de Windows cambió");
                    _lastDefaultId = def?.Id; _lastDefaultName = def?.Name;
                }
            }
            Publish();
        }
        catch (Exception ex) { _logger.LogError(ex, "Falló el tratamiento de un cambio de dispositivos de audio"); }
    }

    public AudioStatePayload GetState()
    {
        var active = _devices.GetActiveDevices();
        var def = _devices.GetDefaultDevice();
        var cfg = Config;
        var route = AudioRoutePolicy.Evaluate(cfg, active, def);
        var devices = active.Select(d => new AudioDeviceDto(d.Id, d.Name, string.Equals(d.Id, def?.Id, StringComparison.Ordinal), string.Equals(d.Id, cfg.ChurchDeviceId, StringComparison.Ordinal))).ToList();
        var track = _engine.Snapshot() is { } t ? new AudioTrackDto(t.MediaId, t.Phase, t.PositionMs, t.DurationMs, t.Loop, t.VolumePct, t.ErrorCode, t.ErrorMessage) : null;
        AudioDefaultChangeDto? change;
        long seq;
        lock (_gate) { change = _defaultChange; seq = ++_seq; }
        return new AudioStatePayload(
            seq,
            new AudioRouteDto(route.UsesWindowsDefault, route.ChurchDeviceId, route.ChurchDeviceName, route.ChurchConnected, route.DefaultDeviceId, route.DefaultDeviceName, route.Mismatch),
            devices, track, ToPayload(cfg), change);
    }

    public static AudioSettingsPayload ToPayload(AudioSettings s) =>
        new(s.ChurchDeviceId, s.ChurchDeviceName, s.MasterVolumePct, s.TrackFadeInMs, s.TrackFadeOutMs, s.VideoFadeInMs, s.VideoFadeOutMs, s.ResumeOnDeviceReturn);

    private void Publish()
    {
        try { _outbox.Publish(IpcEnvelope.Create(IpcKind.Event, IpcTypes.AudioStateChanged, GetState()), Targets.Control); }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo publicar el estado del audio"); }
    }

    private void TickProgress()
    {
        try
        {
            if (_engine.Snapshot() is { Phase: AudioTrackPhase.Playing } t)
                _outbox.Publish(IpcEnvelope.Create(IpcKind.Event, IpcTypes.MediaProgress, new MediaProgressPayload(PlaybackTarget.Track, null, t.PositionMs, t.DurationMs, true)), Targets.Control);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "No se pudo publicar el avance de la pista"); }
    }

    public void PlayTrack(long mediaId, string path, long durationMs, bool loop, bool startPlaying, int volumePct, int? fadeInMs, long order)
    {
        try { _engine.Play(mediaId, path, durationMs, loop, startPlaying, volumePct, fadeInMs ?? Config.TrackFadeInMs, order); }
        catch (AudioException ex) { Publish(); throw new IpcException(ex.Code, ex.Message, ex); }
    }

    public void PauseTrack(long order) => _engine.Pause(order, Config.TrackFadeOutMs / 2);

    public void ResumeTrack(long order)
    {
        try { _engine.Resume(order, Config.TrackFadeInMs); }
        catch (AudioException ex) { Publish(); throw new IpcException(ex.Code, ex.Message, ex); }
    }

    public void SeekTrack(long positionMs) => _engine.Seek(positionMs);

    public void SetTrackVolume(int volumePct) => _engine.SetClipVolume(volumePct);

    public void StopTrack(long order) => _engine.Stop(order, Config.TrackFadeOutMs);

    public void SilenceTracks(long order) => _engine.StopAllNow(order);

    public void SettingsApplied()
    {
        _engine.ApplyMaster();
        Publish();
    }

    public async Task TestToneAsync(AudioTestTarget target, CancellationToken cancellationToken)
    {
        if (!await _toneGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new IpcException(AudioErrorCodes.DeviceBusy, "Ya se está reproduciendo un tono de prueba.");
        try
        {
            var cfg = Config;
            string? deviceId = null;
            if (target == AudioTestTarget.Church && cfg.ChurchDeviceId is { } church)
            {
                if (!_devices.GetActiveDevices().Any(d => string.Equals(d.Id, church, StringComparison.Ordinal)))
                    throw new IpcException(AudioErrorCodes.DeviceNotFound, AudioRules.MessageFor(AudioErrorCodes.DeviceNotFound));
                deviceId = church;
            }
            // Suave a propósito: ≈ 12 % de amplitud escalada por el volumen maestro (con un mínimo para que se oiga con el maestro bajo).
            var gain = 0.12f * Math.Max(cfg.MasterVolumePct / 100f, 0.25f);
            await _backend.PlayToneAsync(deviceId, 440, 700, gain, cancellationToken).ConfigureAwait(false);
        }
        catch (AudioException ex) { throw new IpcException(ex.Code, ex.Message, ex); }
        finally { _toneGate.Release(); }
    }

    public void Dispose()
    {
        _progress.Dispose();
        _devices.Changed -= OnDevicesChanged;
        _engine.Dispose();
        _devices.Dispose();
    }
}
