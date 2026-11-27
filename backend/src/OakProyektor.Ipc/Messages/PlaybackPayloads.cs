using OakProyektor.Core.Output;

namespace OakProyektor.Ipc.Messages;

// Reproducción y audio (protocolo v18). Ni los eventos ni los errores llevan nombres de archivo ni rutas: solo ids y números (el nombre del medio viaja en las consultas de la biblioteca).

/// <summary>A qué se refiere una orden de transporte: al video en primer plano (<c>foreground</c>, lo reproduce la salida) o a la pista de audio (<c>track</c>, la reproduce el motor de C#).</summary>
public enum PlaybackTarget
{
    Foreground,
    Track,
}

/// <summary>
/// <c>media.play</c>: reproduce un elemento de la biblioteca. Un VIDEO pasa a primer plano; un AUDIO lo toma el motor de pistas; una imagen se rechaza. <c>StartPlaying</c> nulo = el valor por defecto
/// del tipo (video: arrancar; audio: dejarlo listo en pausa, para que un sonido nunca arranque sin pedirlo). <c>VolumePct</c> nulo = 100. <c>FadeInMs</c>/<c>FadeOutMs</c> nulos = los ajustes.
/// <c>Backdrop</c> y <c>OnEnd</c> solo valen para el video. La COPIA local la resuelve el backend: nunca una ruta del cliente.
/// </summary>
public sealed record MediaPlayPayload(
    long MediaId, bool? StartPlaying, int? VolumePct, bool Muted, bool Loop, int? FadeInMs, int? FadeOutMs, ForegroundBackdrop Backdrop, ForegroundEndAction OnEnd);

/// <summary><c>media.pause</c>, <c>media.resume</c> y <c>media.stop</c>.</summary>
public sealed record MediaTransportPayload(PlaybackTarget Target);

public sealed record MediaSeekPayload(PlaybackTarget Target, long PositionMs);

/// <summary><c>media.setVolume</c>: el volumen del CLIP (0–100; el maestro se aplica aparte) y, para el video, silenciarlo. Un campo nulo no cambia.</summary>
public sealed record MediaSetVolumePayload(PlaybackTarget Target, int? VolumePct, bool? Muted);

/// <summary>Estado que la salida informa del video en primer plano.</summary>
public enum ReportedPlayback
{
    Playing,
    Paused,
    Ended,
    Error,
}

/// <summary>
/// <c>media.reportProgress</c> (lo manda la SALIDA, ≈ 4 por segundo mientras haya video y al instante en cada cambio): posición y duración reales del video <c>Id</c>. Un informe de un video que
/// ya no es el actual se ignora. <c>Ended</c> dispara el «al terminar» (quitar o quedarse); <c>Error</c> trae el código (<c>decodeError</c>, <c>readError</c>, <c>unsupportedFormat</c>, <c>stalled</c>).
/// </summary>
public sealed record MediaReportProgressPayload(string Id, long PositionMs, long DurationMs, ReportedPlayback State, string? ErrorCode);

/// <summary>
/// Evento <c>media.progress</c> (al panel; NO es parte del snapshot ni cambia <c>seq</c>): avance cosmético. <c>Id</c> identifica el video (<c>Foreground</c>) o es nulo (<c>Track</c>; la pista se
/// identifica por <c>audio.stateChanged</c>). La verdad del video es la salida; el avance del panel es solo un indicador.
/// </summary>
public sealed record MediaProgressPayload(PlaybackTarget Target, string? Id, long PositionMs, long DurationMs, bool Playing);

/// <summary>Evento <c>media.playbackFailed</c> (al panel): un video en primer plano no pudo reproducirse (la salida ya lo dejó en pausa). El texto lo pone el backend.</summary>
public sealed record MediaPlaybackFailedPayload(string Id, string Code, string Message);

// ───────────── audio ─────────────

public enum AudioTestTarget
{
    /// <summary>Tono corto por el dispositivo de la iglesia (por donde salen las pistas).</summary>
    Church,
    /// <summary>Tono corto por el predeterminado de Windows (por donde sale el audio del video en primer plano).</summary>
    Default,
}

public enum AudioTrackPhase
{
    Playing,
    Paused,
    Ended,
    /// <summary>El dispositivo desapareció: la pista quedó en pausa conservando su posición y espera al operador (o al mismo dispositivo, si el ajuste lo permite).</summary>
    DeviceLost,
    Error,
}

/// <summary>Un dispositivo de salida activo. <c>IsDefault</c> = el predeterminado de Windows; <c>IsChurch</c> = el elegido como «dispositivo de la iglesia».</summary>
public sealed record AudioDeviceDto(string Id, string Name, bool IsDefault, bool IsChurch);

/// <summary>Cómo está el cableado AHORA: ver <c>AudioRoutePolicy</c>. <c>Mismatch</c> = las pistas y el video NO salen por el mismo dispositivo; el panel lo avisa de forma visible.</summary>
public sealed record AudioRouteDto(
    bool UsesWindowsDefault, string? ChurchDeviceId, string? ChurchDeviceName, bool ChurchConnected, string? DefaultDeviceId, string? DefaultDeviceName, bool Mismatch);

/// <summary>La pista de audio actual (o nula si no hay ninguna). La posición viene en <c>media.progress</c>; aquí solo se informa en cambios de estado.</summary>
public sealed record AudioTrackDto(
    long MediaId, AudioTrackPhase Phase, long PositionMs, long DurationMs, bool Loop, int VolumePct, string? ErrorCode, string? ErrorMessage);

/// <summary>Último cambio del dispositivo predeterminado de Windows desde que arrancó la aplicación (nulo si no cambió): el panel avisa «cambió durante el servicio». <c>At</c> = ISO 8601 UTC.</summary>
public sealed record AudioDefaultChangeDto(string? FromName, string? ToName, string At);

/// <summary>
/// Ajustes de audio (<c>audio.setSettings</c> los sustituye todos; viaja en las dos direcciones: regla más estricta). <c>ChurchDeviceId</c> nulo = «usar el predeterminado de Windows».
/// </summary>
public sealed record AudioSettingsPayload(
    string? ChurchDeviceId, string? ChurchDeviceName, int MasterVolumePct, int TrackFadeInMs, int TrackFadeOutMs, int VideoFadeInMs, int VideoFadeOutMs, bool ResumeOnDeviceReturn);

/// <summary>Reply de <c>audio.getState</c> y evento <c>audio.stateChanged</c> (solo cuando cambia algo que no sea la posición). <c>Seq</c> creciente: las réplicas descartan menor o igual.</summary>
public sealed record AudioStatePayload(
    long Seq, AudioRouteDto Route, IReadOnlyList<AudioDeviceDto> Devices, AudioTrackDto? Track, AudioSettingsPayload Settings, AudioDefaultChangeDto? DefaultChange);

/// <summary><c>audio.setDevice</c>: el dispositivo de la iglesia por su id estable; nulo = predeterminado de Windows. Un id que no está conectado ahora se rechaza (<c>media.deviceNotFound</c>).</summary>
public sealed record AudioSetDevicePayload(string? DeviceId);

public sealed record AudioSetMasterVolumePayload(int VolumePct);

public sealed record AudioTestPayload(AudioTestTarget Target);
