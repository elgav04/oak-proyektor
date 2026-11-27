using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using OakProyektor.Core.Audio;

namespace OakProyektor.App.Audio;

/// <summary>
/// Respaldo REAL del motor de audio con NAudio 3.3.0 (MIT, versión fijada): Media Foundation decodifica (MP3, AAC/M4A, FLAC, WAV) y WASAPI en modo compartido reproduce en el dispositivo elegido
/// por su id. Es lo ÚNICO que toca NAudio para reproducir; todo lo demás habla con <see cref="IAudioBackend"/>. Nunca corre en el hilo de la interfaz.
/// </summary>
internal sealed class NAudioBackend : IAudioBackend
{
    /// <summary>El rol de «predeterminado» que usa el navegador (el audio del video en primer plano): el de consola/multimedia de Windows.</summary>
    internal const Role DefaultRole = Role.Console;

    public IAudioPlayback Open(string? deviceId, string filePath, bool loop, float gain) => NAudioPlayback.Create(deviceId, filePath, loop, gain);

    /// <summary>WASAPI en modo COMPARTIDO con sincronía por eventos y ≈ 100 ms de latencia (WasapiOut está obsoleto en NAudio 3.x).</summary>
    internal static WasapiPlayer BuildPlayer(MMDevice device) => new WasapiPlayerBuilder().WithDevice(device).WithSharedMode().WithEventSync().WithLatency(100).Build();

    public async Task PlayToneAsync(string? deviceId, int frequencyHz, int durationMs, float gain, CancellationToken cancellationToken)
    {
        using var enumerator = new MMDeviceEnumerator();
        var device = NAudioPlayback.ResolveDevice(enumerator, deviceId);
        try
        {
            WaveFormat mix; using (var client = device.CreateAudioClient()) mix = client.MixFormat;
            var generator = new SignalGenerator(mix.SampleRate, mix.Channels) { Frequency = frequencyHz, Gain = gain, Type = SignalGeneratorType.Sin };
            var ramp = new GainRamp(mix.SampleRate, 0f);
            ramp.SetTarget(1f, 60);   // entra suave: nunca un chasquido
            var source = new GainSampleProvider(generator.Take(TimeSpan.FromMilliseconds(durationMs)), ramp);
            using var output = NAudioBackend.BuildPlayer(device);
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            output.PlaybackStopped += (_, _) => done.TrySetResult();
            output.Init(new SampleToWaveProvider(source));
            output.Play();
            _ = Task.Delay(Math.Max(0, durationMs - 90), CancellationToken.None).ContinueWith(_ => ramp.SetTarget(0f, 80), TaskScheduler.Default);   // sale suave
            await Task.WhenAny(done.Task, Task.Delay(durationMs + 2000, cancellationToken)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (COMException ex) { throw new AudioException(AudioErrorCodes.DeviceBusy, AudioRules.MessageFor(AudioErrorCodes.DeviceBusy), ex); }
        finally { device.Dispose(); }
    }
}

/// <summary>Multiplica por una <see cref="GainRamp"/> (fundidos y silencio sin chasquido).</summary>
internal sealed class GainSampleProvider(ISampleProvider source, GainRamp ramp) : ISampleProvider
{
    public WaveFormat WaveFormat => source.WaveFormat;

    public int Read(Span<float> buffer)
    {
        var read = source.Read(buffer);
        if (read > 0) ramp.Apply(buffer[..read], source.WaveFormat.Channels);
        return read;
    }
}

/// <summary>Lee de un archivo y, con <c>loop</c>, vuelve al principio al llegar al final. Accesos al lector bajo un candado (buscar desde otro hilo).</summary>
internal sealed class LoopingWaveProvider(WaveStream source, bool loop, object gate) : IWaveProvider
{
    public WaveFormat WaveFormat => source.WaveFormat;

    public int Read(Span<byte> buffer)
    {
        lock (gate)
        {
            var n = source.Read(buffer);
            if (n > 0 || !loop) return n;
            source.Position = 0;
            return source.Read(buffer);   // un archivo vacío devuelve 0 también aquí: termina en vez de girar sin parar
        }
    }
}

internal sealed class NAudioPlayback : IAudioPlayback
{
    private const int DeviceInvalidated = unchecked((int)0x88890004);   // AUDCLNT_E_DEVICE_INVALIDATED

    private readonly MediaFoundationReader _reader;
    private readonly object _readerGate;
    private readonly GainRamp _ramp;
    private readonly WasapiPlayer _output;
    private readonly MMDevice _device;
    private readonly TimeSpan _total;
    private readonly object _gate = new();
    private float _gain;
    private bool _wantPlaying;
    private bool _disposed;
    private bool _silenced;      // Stop o Dispose pedidos por el llamador: no se avisa de nada
    private int _fadeToken;      // sube con cada orden: una pausa o una liberación pendiente se cancela si llegó otra orden

    private NAudioPlayback(MMDevice device, MediaFoundationReader reader, object readerGate, GainRamp ramp, WasapiPlayer output, float gain)
    {
        _device = device; _reader = reader; _readerGate = readerGate; _ramp = ramp; _output = output; _gain = gain;
        _total = reader.TotalTime;
        _output.PlaybackStopped += OnStopped;
    }

    public event Action<PlaybackEndReason, string?>? Ended;

    public long PositionMs { get { lock (_readerGate) { try { return _disposed ? 0 : (long)_reader.CurrentTime.TotalMilliseconds; } catch (ObjectDisposedException) { return 0; } } } }
    public long DurationMs => (long)_total.TotalMilliseconds;

    internal static MMDevice ResolveDevice(MMDeviceEnumerator enumerator, string? deviceId)
    {
        try
        {
            var device = deviceId is null ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, NAudioBackend.DefaultRole) : enumerator.GetDevice(deviceId);
            if (device.State != DeviceState.Active) { device.Dispose(); throw new AudioException(AudioErrorCodes.DeviceNotFound, AudioRules.MessageFor(AudioErrorCodes.DeviceNotFound)); }
            return device;
        }
        catch (COMException ex) { throw new AudioException(AudioErrorCodes.DeviceNotFound, AudioRules.MessageFor(AudioErrorCodes.DeviceNotFound), ex); }
        catch (ArgumentException ex) { throw new AudioException(AudioErrorCodes.DeviceNotFound, AudioRules.MessageFor(AudioErrorCodes.DeviceNotFound), ex); }
    }

    public static NAudioPlayback Create(string? deviceId, string filePath, bool loop, float gain)
    {
        MMDevice? device = null;
        MediaFoundationReader? reader = null;
        try
        {
            using (var enumerator = new MMDeviceEnumerator()) device = ResolveDevice(enumerator, deviceId);
            WaveFormat mix; using (var client = device.CreateAudioClient()) mix = client.MixFormat;
            try { reader = new MediaFoundationReader(filePath); }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
            { throw new AudioException(AudioErrorCodes.PlayFailed, AudioRules.MessageFor(AudioErrorCodes.PlayFailed), ex); }

            var gate = new object();
            ISampleProvider samples = new LoopingWaveProvider(reader, loop, gate).ToSampleProvider();
            // Al formato de mezcla del dispositivo: canales y frecuencia (WASAPI compartido solo acepta el suyo).
            if (samples.WaveFormat.Channels == 1 && mix.Channels >= 2) samples = new MonoToStereoSampleProvider(samples);
            else if (samples.WaveFormat.Channels == 2 && mix.Channels == 1) samples = new StereoToMonoSampleProvider(samples);
            else if (samples.WaveFormat.Channels != mix.Channels && !(samples.WaveFormat.Channels == 2 && mix.Channels > 2))
                throw new AudioException(AudioErrorCodes.PlayFailed, AudioRules.MessageFor(AudioErrorCodes.PlayFailed));
            if (samples.WaveFormat.SampleRate != mix.SampleRate) samples = new WdlResamplingSampleProvider(samples, mix.SampleRate);

            var ramp = new GainRamp(mix.SampleRate, 0f);   // nace en silencio: Play() sube con el fundido de entrada
            var output = NAudioBackend.BuildPlayer(device);
            try { output.Init(new SampleToWaveProvider(new GainSampleProvider(samples, ramp))); }
            catch { output.Dispose(); throw; }
            var playback = new NAudioPlayback(device, reader, gate, ramp, output, gain);
            device = null; reader = null;   // ahora los posee la reproducción
            return playback;
        }
        catch (COMException ex) { throw new AudioException(AudioErrorCodes.DeviceBusy, AudioRules.MessageFor(AudioErrorCodes.DeviceBusy), ex); }
        finally { reader?.Dispose(); device?.Dispose(); }
    }

    public void Play(int fadeInMs)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _fadeToken++;
            _wantPlaying = true;
            if (_output.PlaybackState != PlaybackState.Playing) _output.Play();
            _ramp.SetTarget(_gain, fadeInMs);
        }
    }

    public void Pause(int fadeMs)
    {
        int token;
        lock (_gate)
        {
            if (_disposed) return;
            token = ++_fadeToken;
            _wantPlaying = false;
            _ramp.SetTarget(0f, fadeMs);
            if (fadeMs <= 0) { try { _output.Pause(); } catch (Exception) { /* dispositivo ya perdido: Ended lo informará */ } return; }
        }
        _ = Task.Delay(fadeMs + 40).ContinueWith(_ =>
        {
            lock (_gate)
            {
                if (_disposed || token != _fadeToken) return;   // llegó otra orden (reanudar, detener): esta pausa ya no vale
                try { _output.Pause(); } catch (Exception) { /* idem */ }
            }
        }, TaskScheduler.Default);
    }

    public void Seek(long positionMs)
    {
        lock (_readerGate)
        {
            if (_disposed) return;
            try { _reader.CurrentTime = TimeSpan.FromMilliseconds(Math.Clamp(positionMs, 0, (long)_total.TotalMilliseconds)); }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException) { /* se queda donde estaba */ }
        }
    }

    public void SetGain(float gain, int rampMs)
    {
        lock (_gate)
        {
            _gain = Math.Clamp(gain, 0f, 1f);
            if (_wantPlaying && !_disposed) _ramp.SetTarget(_gain, rampMs);
        }
    }

    public void Stop(int fadeMs)
    {
        int token;
        lock (_gate)
        {
            if (_disposed) return;
            _silenced = true;
            token = ++_fadeToken;
            _wantPlaying = false;
            _ramp.SetTarget(0f, fadeMs);
        }
        _ = Task.Delay(fadeMs + 40).ContinueWith(_ => { if (token == Volatile.Read(ref _fadeToken)) Dispose(); }, TaskScheduler.Default);
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        PlaybackEndReason reason;
        string? detail = null;
        lock (_gate)
        {
            if (_disposed || _silenced) return;
            if (e.Exception is null) reason = PlaybackEndReason.Finished;
            else if (e.Exception is COMException { HResult: DeviceInvalidated }) reason = PlaybackEndReason.DeviceLost;
            else { reason = PlaybackEndReason.Failed; detail = e.Exception.GetType().Name + " 0x" + e.Exception.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture); }
        }
        try { Ended?.Invoke(reason, detail); } catch (Exception) { /* un suscriptor que falla no puede tumbar el hilo de audio */ }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _silenced = true;
            _fadeToken++;
        }
        try { _output.PlaybackStopped -= OnStopped; } catch (Exception) { }
        try { _output.Dispose(); } catch (Exception) { /* dispositivo perdido */ }
        lock (_readerGate) { try { _reader.Dispose(); } catch (Exception) { } }
        try { _device.Dispose(); } catch (Exception) { }
    }
}
