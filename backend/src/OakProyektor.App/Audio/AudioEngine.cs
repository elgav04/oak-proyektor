using Microsoft.Extensions.Logging;
using OakProyektor.Core.Audio;
using OakProyektor.Core.Output;
using OakProyektor.Ipc.Messages;

namespace OakProyektor.App.Audio;

/// <param name="PositionMs">Dónde va (o dónde quedó, si está en pausa o perdió el dispositivo).</param>
/// <param name="VolumePct">Volumen del CLIP (0–100); el maestro se aplica aparte.</param>
internal sealed record AudioTrackSnapshot(long MediaId, AudioTrackPhase Phase, long PositionMs, long DurationMs, bool Loop, int VolumePct, string? ErrorCode, string? ErrorMessage);

/// <summary>
/// Motor de pistas de audio (UNA pista a la vez): estado, orden de las órdenes del operador, volumen maestro y política de pérdida del dispositivo. Habla con la biblioteca de audio solo por
/// <see cref="IAudioBackend"/> (así se prueba entero con un respaldo falso y un enumerador simulado) y NO usa nunca el hilo de la interfaz: abrir un dispositivo se hace FUERA del candado y el
/// candado (<c>_gate</c>) solo guarda memoria, así que <see cref="StopAllNow"/> (el atajo «Silencio») no espera a una apertura lenta.
/// <para><b>Dispositivo</b>: con «dispositivo de la iglesia» elegido, las pistas salen SIEMPRE por él (si no está conectado se rechaza con <c>media.deviceNotFound</c>; jamás se usa otro). Sin
/// elegir, por el predeterminado de Windows. Si el dispositivo con el que se abrió desaparece, la pista queda en pausa conservando su posición (<c>DeviceLost</c>) y espera al operador; solo
/// con <c>ResumeOnDeviceReturn</c> (apagado por defecto) se reanuda sola, y únicamente cuando vuelve el MISMO dispositivo (mismo id). Nunca salta a otro por su cuenta.</para>
/// <para><b>Pausas de la salida, Negro, Texto limpio, Logo y Freeze NO cortan las pistas</b> (decisión aprobada): este motor no sabe nada de ellos. Callan una pista: el operador, el fin del
/// medio y «Silencio».</para>
/// </summary>
internal sealed class AudioEngine : IDisposable
{
    private readonly IAudioBackend _backend;
    private readonly IAudioDeviceEnumerator _devices;
    private readonly Func<AudioSettings> _settings;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly TransportOrder _order = new();

    private IAudioPlayback? _pb;
    private bool _hasTrack;
    private long _generation;          // sube con cada pista nueva o detenida: una apertura o una reanudación automática antigua no puede instalarse sobre otra
    private long _mediaId;
    private string? _path;
    private bool _loop;
    private int _clipPct = 100;
    private AudioTrackPhase _phase;
    private string? _openedDeviceId;
    private long _savedPositionMs;
    private long _durationMs;
    private string? _errorCode;
    private string? _errorMessage;
    private bool _disposed;

    public AudioEngine(IAudioBackend backend, IAudioDeviceEnumerator devices, Func<AudioSettings> settings, ILogger logger)
    {
        _backend = backend; _devices = devices; _settings = settings; _logger = logger;
        _devices.Changed += OnDevicesChanged;
    }

    /// <summary>Cambió algo de la pista (estado, no posición). Se dispara FUERA del candado y puede llegar en cualquier hilo.</summary>
    public event Action? Changed;

    public AudioTrackSnapshot? Snapshot()
    {
        lock (_gate) return SnapshotLocked();
    }

    private AudioTrackSnapshot? SnapshotLocked() =>
        !_hasTrack ? null : new AudioTrackSnapshot(_mediaId, _phase, _pb?.PositionMs ?? _savedPositionMs, _pb?.DurationMs is > 0 and var d ? d : _durationMs, _loop, _clipPct, _errorCode, _errorMessage);

    private void Raise()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { _logger.LogError(ex, "Falló un suscriptor del estado de audio"); }
    }

    private float GainLocked() => Math.Clamp(_clipPct, 0, 100) / 100f * Math.Clamp(_settings().MasterVolumePct, 0, 100) / 100f;

    /// <summary>El dispositivo con el que se abre una pista nueva: el de la iglesia si está elegido (y conectado: si no, falla) o el predeterminado de Windows.</summary>
    private (string? DeviceId, string? OpenedId) ResolveDevice()
    {
        var s = _settings();
        if (s.ChurchDeviceId is { } church)
        {
            if (!_devices.GetActiveDevices().Any(d => string.Equals(d.Id, church, StringComparison.Ordinal)))
                throw new AudioException(AudioErrorCodes.DeviceNotFound, AudioRules.MessageFor(AudioErrorCodes.DeviceNotFound));
            return (church, church);
        }
        return (null, _devices.GetDefaultDevice()?.Id);
    }

    // ───────────── órdenes del operador ─────────────

    /// <summary>
    /// Abre una pista. <paramref name="startPlaying"/> = false la deja LISTA en pausa (un sonido nunca arranca sin pedirlo). Reemplaza a la pista anterior. Lanza <see cref="AudioException"/> si no
    /// se pudo abrir (el estado queda sin pista). Con una orden de «Silencio» posterior a <paramref name="order"/> no hace nada.
    /// </summary>
    public void Play(long mediaId, string path, long durationHintMs, bool loop, bool startPlaying, int clipVolumePct, int fadeInMs, long order)
    {
        long gen;
        lock (_gate)
        {
            if (_disposed) return;
            gen = ++_generation;
            ReleaseLocked(AudioRules.SilenceFadeMs * 2);
            _hasTrack = false;
        }

        IAudioPlayback pb;
        string? opened;
        try
        {
            var (deviceId, openedId) = ResolveDevice();
            opened = openedId;
            float gain;
            lock (_gate) { _clipPct = Math.Clamp(clipVolumePct, 0, 100); gain = GainLocked(); }
            pb = _backend.Open(deviceId, path, loop, gain);   // FUERA del candado: abrir un dispositivo puede tardar
        }
        catch (AudioException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo abrir la pista {MediaId}", mediaId);
            throw new AudioException(AudioErrorCodes.PlayFailed, AudioRules.MessageFor(AudioErrorCodes.PlayFailed), ex);
        }

        var install = false;
        lock (_gate)
        {
            if (_disposed || gen != _generation || (startPlaying && !_order.AllowPlay(order))) { /* una orden posterior (otra pista, detener, «Silencio») ya llegó */ }
            else
            {
                _pb = pb; _hasTrack = true; _mediaId = mediaId; _path = path; _loop = loop; _openedDeviceId = opened;
                _durationMs = Math.Max(durationHintMs, pb.DurationMs); _savedPositionMs = 0; _errorCode = null; _errorMessage = null;
                pb.Ended += (reason, message) => OnEnded(pb, reason, message);
                if (startPlaying) { pb.Play(fadeInMs); _phase = AudioTrackPhase.Playing; } else _phase = AudioTrackPhase.Paused;
                install = true;
            }
        }
        if (!install) { DisposeQuietly(pb); return; }
        _logger.LogInformation("Pista {MediaId} {State}", mediaId, startPlaying ? "reproduciéndose" : "lista en pausa");
        Raise();
    }

    public void Pause(long order, int fadeMs)
    {
        lock (_gate)
        {
            if (!_order.AllowHalt(order)) return;
            if (_pb is null || _phase != AudioTrackPhase.Playing) return;
            _pb.Pause(fadeMs);
            _phase = AudioTrackPhase.Paused;
        }
        Raise();
    }

    /// <summary>Reanuda una pista en pausa, terminada o que perdió el dispositivo. Con «dispositivo de la iglesia» desconectado lanza <c>media.deviceNotFound</c>: no pasa a otro.</summary>
    public void Resume(long order, int fadeInMs)
    {
        long gen = 0; string path = ""; bool loop = false; long at = 0; var reopen = false;
        lock (_gate)
        {
            if (!_order.AllowPlay(order)) return;
            if (!_hasTrack) throw new AudioException(AudioErrorCodes.NothingPlaying, AudioRules.MessageFor(AudioErrorCodes.NothingPlaying));
            if (_phase == AudioTrackPhase.Playing) return;
            if (_pb is not null) { _pb.Play(fadeInMs); _phase = AudioTrackPhase.Playing; }   // estaba en pausa (o lista): sigue donde iba
            else
            {
                // Sin reproductor abierto (terminó, perdió el dispositivo o falló): se vuelve a abrir en el dispositivo que corresponda AHORA.
                reopen = true; gen = ++_generation; path = _path!; loop = _loop; at = _phase == AudioTrackPhase.Ended ? 0 : _savedPositionMs;
            }
        }
        if (reopen) ReopenAndPlay(gen, path, loop, at, fadeInMs); else Raise();
    }
    private void ReopenAndPlay(long gen, string path, bool loop, long at, int fadeInMs)
    {
        IAudioPlayback pb;
        string? opened;
        try
        {
            var (deviceId, openedId) = ResolveDevice();
            opened = openedId;
            float gain;
            lock (_gate) gain = GainLocked();
            pb = _backend.Open(deviceId, path, loop, gain);
            if (at > 0) pb.Seek(at);
        }
        catch (AudioException) { throw; }
        catch (Exception ex) { throw new AudioException(AudioErrorCodes.PlayFailed, AudioRules.MessageFor(AudioErrorCodes.PlayFailed), ex); }

        var install = false;
        lock (_gate)
        {
            if (!_disposed && gen == _generation && _hasTrack)
            {
                _pb = pb; _openedDeviceId = opened; _errorCode = null; _errorMessage = null;
                pb.Ended += (reason, message) => OnEnded(pb, reason, message);
                pb.Play(fadeInMs);
                _phase = AudioTrackPhase.Playing;
                install = true;
            }
        }
        if (!install) { DisposeQuietly(pb); return; }
        Raise();
    }

    public void Seek(long positionMs)
    {
        lock (_gate)
        {
            if (!_hasTrack) return;
            var target = Math.Clamp(positionMs, 0, _durationMs > 0 ? _durationMs : long.MaxValue);
            if (_pb is not null) _pb.Seek(target); else _savedPositionMs = target;
        }
    }

    public void SetClipVolume(int volumePct)
    {
        lock (_gate)
        {
            _clipPct = Math.Clamp(volumePct, 0, 100);
            _pb?.SetGain(GainLocked(), 100);
        }
        Raise();
    }

    /// <summary>El volumen maestro cambió en los ajustes: se aplica ya a la pista que suena.</summary>
    public void ApplyMaster()
    {
        lock (_gate) _pb?.SetGain(GainLocked(), 100);
    }

    public void Stop(long order, int fadeOutMs)
    {
        lock (_gate)
        {
            if (!_order.AllowHalt(order)) return;
            _generation++;
            ReleaseLocked(fadeOutMs);
            _hasTrack = false;
        }
        Raise();
    }

    /// <summary>
    /// «Silencio» (atajo global): calla la pista en ≈ 100 ms y la deja en PAUSA (cargada, con su posición); NUNCA reanuda. Solo toma el candado de memoria, que nunca se retiene durante una
    /// apertura de dispositivo, así que no espera a nada lento. Recuerda el número de llegada: un «reproducir» anterior que aún estaba encolado no la hace sonar de nuevo.
    /// </summary>
    public void StopAllNow(long order)
    {
        var changed = false;
        lock (_gate)
        {
            _order.AllowHalt(order);
            if (_pb is not null && _phase == AudioTrackPhase.Playing)
            {
                _pb.Pause(AudioRules.SilenceFadeMs);
                _phase = AudioTrackPhase.Paused;
                changed = true;
            }
        }
        if (changed) Raise();
    }

    // ───────────── dispositivos y fin de pista ─────────────

    private void OnDevicesChanged()
    {
        try
        {
            bool resume = false; long gen = 0; string path = ""; bool loop = false; long at = 0; int fade = 0;
            lock (_gate)
            {
                if (_disposed || !_hasTrack) { }
                else if (_pb is not null && _openedDeviceId is { } id && !IsActive(id)) MarkLostLocked();
                else if (_phase == AudioTrackPhase.DeviceLost && _pb is null && _settings().ResumeOnDeviceReturn && _openedDeviceId is { } back && IsActive(back))
                {
                    // Vuelve el MISMO dispositivo (mismo id) y el ajuste lo permite: reanuda sola, donde se quedó.
                    resume = true; gen = ++_generation; path = _path!; loop = _loop; at = _savedPositionMs; fade = _settings().TrackFadeInMs;
                }
            }
            Raise();   // la ruta (predeterminado de Windows, dispositivos) pudo cambiar aunque la pista no
            if (resume) _ = Task.Run(() => { try { ReopenAndPlay(gen, path, loop, at, fade); } catch (Exception ex) { _logger.LogWarning(ex, "No se pudo reanudar la pista al volver el dispositivo"); } });
        }
        catch (Exception ex) { _logger.LogError(ex, "Falló el tratamiento de un cambio de dispositivos de audio"); }
    }

    private bool IsActive(string id) => _devices.GetActiveDevices().Any(d => string.Equals(d.Id, id, StringComparison.Ordinal));

    private void MarkLostLocked()
    {
        var old = _pb;
        _savedPositionMs = old?.PositionMs ?? _savedPositionMs;
        _pb = null;
        _phase = AudioTrackPhase.DeviceLost;
        _errorCode = AudioErrorCodes.DeviceLost;
        _errorMessage = AudioRules.MessageFor(AudioErrorCodes.DeviceLost);
        _logger.LogWarning("La pista {MediaId} quedó en pausa: el dispositivo de audio desapareció", _mediaId);
        if (old is not null) _ = Task.Run(() => DisposeQuietly(old));   // liberar un dispositivo que ya no está puede tardar: nunca bajo el candado
    }

    private void OnEnded(IAudioPlayback pb, PlaybackEndReason reason, string? message)
    {
        try
        {
            lock (_gate)
            {
                if (!ReferenceEquals(pb, _pb)) return;   // una pista ya reemplazada o detenida
                switch (reason)
                {
                    case PlaybackEndReason.Finished:
                        _pb = null; _phase = AudioTrackPhase.Ended; _savedPositionMs = _durationMs;
                        _ = Task.Run(() => DisposeQuietly(pb));
                        break;
                    case PlaybackEndReason.DeviceLost:
                        MarkLostLocked();
                        break;
                    default:
                        _pb = null; _phase = AudioTrackPhase.Error; _errorCode = AudioErrorCodes.PlayFailed; _errorMessage = AudioRules.MessageFor(AudioErrorCodes.PlayFailed);
                        _logger.LogWarning("La pista {MediaId} falló: {Detail}", _mediaId, message is { Length: > 200 } m ? m[..200] : message);
                        _ = Task.Run(() => DisposeQuietly(pb));
                        break;
                }
            }
            Raise();
        }
        catch (Exception ex) { _logger.LogError(ex, "Falló el tratamiento del fin de una pista"); }
    }

    private void ReleaseLocked(int fadeOutMs)
    {
        var old = _pb;
        _pb = null;
        if (old is null) return;
        try { old.Stop(fadeOutMs); } catch (Exception ex) { _logger.LogWarning(ex, "No se pudo detener la pista"); }
    }

    private void DisposeQuietly(IAudioPlayback pb)
    {
        try { pb.Dispose(); } catch (Exception ex) { _logger.LogWarning(ex, "No se pudo liberar una pista de audio"); }
    }

    public void Dispose()
    {
        IAudioPlayback? old;
        lock (_gate) { _disposed = true; old = _pb; _pb = null; }
        _devices.Changed -= OnDevicesChanged;
        if (old is not null) DisposeQuietly(old);
    }
}
