using Microsoft.Extensions.Logging;
using OakProyektor.App.Audio;
using OakProyektor.App.Media;
using OakProyektor.Core;
using OakProyektor.Core.Audio;
using OakProyektor.Core.Media;
using OakProyektor.Core.Output;
using OakProyektor.Data;
using OakProyektor.Data.Media;
using OakProyektor.Ipc;
using OakProyektor.Ipc.Messages;

namespace OakProyektor.App.Handlers;

// Reproducción y audio (protocolo v18). Los mensajes de error y los registros NUNCA llevan nombres ni rutas del operador: solo códigos e ids. Pausar, detener y «Silencio» van por el carril de
// PRIORIDAD (solo memoria); reproducir y «Probar sonido», por el LENTO; reanudar, buscar, volumen, el informe de avance y los ajustes, por el EN VIVO. Las órdenes de reproducción se ordenan por su
// NÚMERO DE LLEGADA (request.Order): la última intención del operador gana aunque viajen por carriles distintos.
internal static class PlaybackIpc
{
    public static IpcException Invalid(string message) => new(ForegroundErrorCodes.InvalidRequest, message);

    public static void ValidateVolume(int? pct)
    {
        if (pct is < 0 or > 100) throw Invalid("El volumen debe estar entre 0 y 100.");
    }

    public static void ValidateFade(int? ms, string what)
    {
        if (ms is < 0 or > ForegroundMedia.MaxFadeMs) throw Invalid($"El fundido {what} debe estar entre 0 y 5000 ms.");
    }
}

internal sealed class MediaPlayHandler(
    IMediaRepository media, MediaStorage storage, IMediaFileSystem fileSystem, IOutputController output, IAudioService audio, ISettingsStore settings) : IIpcHandler
{
    public string Kind => IpcKind.Command;
    public string Type => IpcTypes.MediaPlay;

    public async Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var r = request.GetPayload<MediaPlayPayload>();
        PlaybackIpc.ValidateVolume(r.VolumePct);
        PlaybackIpc.ValidateFade(r.FadeInMs, "de entrada");
        PlaybackIpc.ValidateFade(r.FadeOutMs, "de salida");
        if (!Enum.IsDefined(r.Backdrop) || !Enum.IsDefined(r.OnEnd)) throw PlaybackIpc.Invalid("Opciones de reproducción desconocidas.");

        var item = await MediaIpc.RunAsync(() => media.GetAsync(r.MediaId, cancellationToken)).ConfigureAwait(false) ?? throw new IpcException(MediaErrorCodes.NotFound, MediaRules.MessageFor(MediaErrorCodes.NotFound));
        if (item.Kind == MediaItemKind.Image) throw new IpcException(ForegroundErrorCodes.NotPlayable, "Una imagen no se reproduce: úsala como fondo.");
        if (item.Status == MediaStatus.Missing) throw new IpcException(MediaErrorCodes.MissingFile, MediaRules.MessageFor(MediaErrorCodes.MissingFile));
        if (item.Status == MediaStatus.Unsupported) throw new IpcException(item.StatusCode ?? MediaErrorCodes.DecodeFailed, MediaRules.MessageFor(item.StatusCode ?? MediaErrorCodes.DecodeFailed));

        var cfg = settings.Current.AudioConfig;
        var volume = r.VolumePct ?? 100;
        var path = storage.FilePathFor(item);

        if (item.Kind == MediaItemKind.Audio)
        {
            // La copia local: una comprobación corta y con tope (si el disco de datos se atasca no retiene el carril).
            bool exists;
            try { exists = await Task.Run(() => fileSystem.Probe(path).Exists, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false); }
            catch (TimeoutException) { throw new IpcException("output.mediaRejected", "No se pudo comprobar el archivo a tiempo."); }
            if (!exists) throw new IpcException(MediaErrorCodes.MissingFile, MediaRules.MessageFor(MediaErrorCodes.MissingFile));
            // Un audio suelto queda LISTO en pausa por defecto: un sonido no pedido en plena oración es el peor error posible.
            audio.PlayTrack(item.Id, path, item.DurationMs ?? 0, r.Loop, r.StartPlaying ?? false, volume, r.FadeInMs, request.Order);
            return null;
        }

        MediaValidation result;
        try { result = await Task.Run(() => MediaPathValidator.Validate(path, fileSystem, Core.Output.MediaKind.Video), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) { throw new IpcException("output.mediaRejected", "No se pudo comprobar el archivo a tiempo."); }
        if (!result.IsValid) throw new IpcException(MediaErrorCodes.MissingFile, result.Message ?? MediaRules.MessageFor(MediaErrorCodes.MissingFile));

        output.PlayForeground(
            new ForegroundRequest(item.Id, result.Media!.Url, item.Name, r.StartPlaying ?? true, volume, cfg.MasterVolumePct, r.Muted, r.Loop,
                r.FadeInMs ?? cfg.VideoFadeInMs, r.FadeOutMs ?? cfg.VideoFadeOutMs, r.Backdrop, r.OnEnd),
            request.Order);
        return null;
    }
}

internal sealed class MediaPauseHandler(IOutputController output, IAudioService audio) : IIpcHandler
{
    public string Kind => IpcKind.Command;
    public string Type => IpcTypes.MediaPause;
    public Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var r = request.GetPayload<MediaTransportPayload>();
        if (r.Target == PlaybackTarget.Foreground) output.PauseForeground(request.Order); else audio.PauseTrack(request.Order);
        return Task.FromResult<object?>(null);
    }
}

internal sealed class MediaResumeHandler(IOutputController output, IAudioService audio) : IIpcHandler
{
    public string Kind => IpcKind.Command;
    public string Type => IpcTypes.MediaResume;
    public Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var r = request.GetPayload<MediaTransportPayload>();
        if (r.Target == PlaybackTarget.Foreground) output.ResumeForeground(request.Order); else audio.ResumeTrack(request.Order);
        return Task.FromResult<object?>(null);
    }
}

internal sealed class MediaSeekHandler(IOutputController output, IAudioService audio) : IIpcHandler
{
    public string Kind => IpcKind.Command;
    public string Type => IpcTypes.MediaSeek;
    public Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var r = request.GetPayload<MediaSeekPayload>();
        if (r.PositionMs is < 0 or > 86_400_000) throw PlaybackIpc.Invalid("La posición no es válida.");
        if (r.Target == PlaybackTarget.Foreground) output.SeekForeground(r.PositionMs); else audio.SeekTrack(r.PositionMs);
        return Task.FromResult<object?>(null);
    }
}

internal sealed class MediaSetVolumeHandler(IOutputController output, IAudioService audio) : IIpcHandler
{
    public string Kind => IpcKind.Command;
    public string Type => IpcTypes.MediaSetVolume;
    public Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var r = request.GetPayload<MediaSetVolumePayload>();
        PlaybackIpc.ValidateVolume(r.VolumePct);
        if (r.Target == PlaybackTarget.Foreground) output.SetForegroundVolume(r.VolumePct, r.Muted);
        else if (r.VolumePct is { } pct) audio.SetTrackVolume(pct);
        return Task.FromResult<object?>(null);
    }
}

internal sealed class MediaStopHandler(IOutputController output, IAudioService audio) : IIpcHandler
{
    public string Kind => IpcKind.Command;
    public string Type => IpcTypes.MediaStop;
    public Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var r = request.GetPayload<MediaTransportPayload>();
        if (r.Target == PlaybackTarget.Foreground) output.StopForeground(request.Order); else audio.StopTrack(request.Order);
        return Task.FromResult<object?>(null);
    }
}

/// <summary>Respaldo de «Silencio» desde la página de salida (si el atajo global no pudo registrarse) y botón del panel: calla las pistas y pausa el video. Solo calla.</summary>
internal sealed class MediaStopAllHandler(IOutputController output, IAudioService audio) : IIpcHandler
{
    public string Kind => IpcKind.Command;
    public string Type => IpcTypes.MediaStopAll;
    public Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        Silence.Run(output, audio, request.Order, null);
        return Task.FromResult<object?>(null);
    }
}

internal static class Silence
{
    /// <summary>Calla TODO: el video en primer plano y las pistas. Cada parte captura su fallo: que una falle no impide callar la otra.</summary>
    public static void Run(IOutputController output, IAudioService audio, long order, Microsoft.Extensions.Logging.ILogger? logger)
    {
        try { audio.SilenceTracks(order); } catch (Exception ex) { logger?.LogError(ex, "Falló el silencio de las pistas"); }
        try { output.SilenceForeground(order); } catch (Exception ex) { logger?.LogError(ex, "Falló el silencio del video"); }
    }
}

/// <summary>Lo manda la SALIDA (≈ 4 por segundo mientras haya video): solo memoria, así que va por el carril en vivo.</summary>
internal sealed class MediaReportProgressHandler(IOutputController output) : IIpcHandler
{
    public string Kind => IpcKind.Command;
    public string Type => IpcTypes.MediaReportProgress;
    public Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        output.ReportForegroundProgress(request.GetPayload<MediaReportProgressPayload>());
        return Task.FromResult<object?>(null);
    }
}

internal sealed class AudioGetStateHandler(IAudioService audio) : IIpcHandler
{
    public string Kind => IpcKind.Query;
    public string Type => IpcTypes.AudioGetState;
    public Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken) => Task.FromResult<object?>(audio.GetState());
}

internal sealed class AudioSetDeviceHandler(ISettingsStore settings, IAudioService audio, IAudioDeviceEnumerator devices) : IIpcHandler
{
    public string Kind => IpcKind.Command;
    public string Type => IpcTypes.AudioSetDevice;
    public async Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var r = request.GetPayload<AudioSetDevicePayload>();
        string? name = null;
        if (r.DeviceId is { } id)
        {
            // Solo se puede ELEGIR un dispositivo conectado ahora (la lista que ve el operador); uno guardado que luego desaparece se conserva por su id.
            var device = devices.GetActiveDevices().FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.Ordinal))
                ?? throw new IpcException(AudioErrorCodes.DeviceNotFound, AudioRules.MessageFor(AudioErrorCodes.DeviceNotFound));
            name = device.Name;
        }
        var updated = settings.Current.AudioConfig with { ChurchDeviceId = r.DeviceId, ChurchDeviceName = name };
        if (AudioRules.Validate(updated) is { } problem) throw new IpcException(AudioErrorCodes.InvalidSettings, problem);
        await settings.UpdateAsync(s => s with { Audio = s.AudioConfig with { ChurchDeviceId = r.DeviceId, ChurchDeviceName = name } }, cancellationToken).ConfigureAwait(false);
        audio.SettingsApplied();
        return null;
    }
}

internal sealed class AudioSetMasterVolumeHandler(ISettingsStore settings, IAudioService audio, IOutputController output) : IIpcHandler
{
    public string Kind => IpcKind.Command;
    public string Type => IpcTypes.AudioSetMasterVolume;
    public async Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var r = request.GetPayload<AudioSetMasterVolumePayload>();
        if (r.VolumePct is < 0 or > 100) throw new IpcException(AudioErrorCodes.InvalidSettings, "El volumen maestro debe estar entre 0 y 100.");
        await settings.UpdateAsync(s => s with { Audio = s.AudioConfig with { MasterVolumePct = r.VolumePct } }, cancellationToken).ConfigureAwait(false);
        audio.SettingsApplied();
        output.ApplyMasterVolume();   // el video en primer plano usa el mismo volumen maestro
        return null;
    }
}

/// <summary>Valida ANTES de guardar nada (un valor inválido no cambia nada). Sustituye todos los ajustes de audio.</summary>
internal sealed class AudioSetSettingsHandler(ISettingsStore settings, IAudioService audio, IOutputController output) : IIpcHandler
{
    public string Kind => IpcKind.Command;
    public string Type => IpcTypes.AudioSetSettings;
    public async Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var r = request.GetPayload<AudioSettingsPayload>();
        var next = new AudioSettings(r.ChurchDeviceId, r.ChurchDeviceName, r.MasterVolumePct, r.TrackFadeInMs, r.TrackFadeOutMs, r.VideoFadeInMs, r.VideoFadeOutMs, r.ResumeOnDeviceReturn);
        if (AudioRules.Validate(next) is { } problem) throw new IpcException(AudioErrorCodes.InvalidSettings, problem);
        await settings.UpdateAsync(s => s with { Audio = next }, cancellationToken).ConfigureAwait(false);
        audio.SettingsApplied();
        output.ApplyMasterVolume();
        return null;
    }
}

/// <summary>«Probar sonido»: un tono suave y corto por el dispositivo de la iglesia o por el predeterminado de Windows. Carril lento (abrir un dispositivo puede tardar).</summary>
internal sealed class AudioTestHandler(IAudioService audio) : IIpcHandler
{
    public string Kind => IpcKind.Command;
    public string Type => IpcTypes.AudioTest;
    public async Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var r = request.GetPayload<AudioTestPayload>();
        if (!Enum.IsDefined(r.Target)) throw new IpcException(AudioErrorCodes.InvalidSettings, "Destino de prueba desconocido.");
        await audio.TestToneAsync(r.Target, cancellationToken).ConfigureAwait(false);
        return null;
    }
}
