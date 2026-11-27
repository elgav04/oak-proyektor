namespace OakProyektor.Core.Audio;

/// <summary>Un dispositivo de SALIDA de audio activo. <see cref="Id"/> es el identificador estable de Windows (el que se guarda: nunca el nombre ni la posición).</summary>
public sealed record AudioDeviceInfo(string Id, string Name);

/// <summary>
/// Lista los dispositivos de salida de audio y avisa de los cambios (agregado, quitado, estado, predeterminado). Interfaz para probar todo el módulo con un enumerador simulado;
/// la real (<c>NAudioDeviceEnumerator</c>) usa las notificaciones de Windows (<c>IMMNotificationClient</c>). Los eventos pueden llegar en cualquier hilo y NO deben hacer trabajo pesado.
/// </summary>
public interface IAudioDeviceEnumerator : IDisposable
{
    /// <summary>Dispositivos de salida ACTIVOS ahora. Nunca lanza: ante un fallo devuelve la lista vacía.</summary>
    IReadOnlyList<AudioDeviceInfo> GetActiveDevices();

    /// <summary>El dispositivo predeterminado de Windows (el que usa el audio del video en primer plano, que sale por el navegador), o nulo si no hay ninguno.</summary>
    AudioDeviceInfo? GetDefaultDevice();

    /// <summary>Algo cambió en los dispositivos o en el predeterminado: el suscriptor vuelve a leer.</summary>
    event Action? Changed;
}

/// <summary>Códigos de error estables del audio (<c>media.*</c>, como el resto del módulo de medios; el texto en español lo pone <see cref="AudioRules.MessageFor"/>).</summary>
public static class AudioErrorCodes
{
    public const string DeviceNotFound = "media.deviceNotFound";
    public const string DeviceLost = "media.deviceLost";
    public const string InvalidSettings = "audio.invalidSettings";
    public const string PlayFailed = "media.audioPlayFailed";
    public const string NothingPlaying = "media.nothingPlaying";
    public const string DeviceBusy = "media.deviceBusy";
}

/// <summary>
/// Ajustes de audio (<c>settings.json</c> → <c>audio</c>; carga por bloque con aviso como el resto). <see cref="ChurchDeviceId"/> = «dispositivo de audio de la iglesia» por id estable de Windows,
/// o nulo = «usar el predeterminado de Windows». <see cref="ChurchDeviceName"/> es SOLO el último nombre conocido, para mostrarlo si el dispositivo no está conectado (la decisión usa el id).
/// <see cref="MasterVolumePct"/>: el volumen maestro de pistas y video (50 por defecto: un primer arranque nunca suena fuerte). <see cref="ResumeOnDeviceReturn"/> (APAGADO por defecto):
/// si el MISMO dispositivo (mismo id) vuelve tras desaparecer, la pista que estaba sonando se reanuda sola; apagado, espera al operador.
/// </summary>
public sealed record AudioSettings(
    string? ChurchDeviceId = null,
    string? ChurchDeviceName = null,
    int MasterVolumePct = AudioRules.DefaultMasterVolumePct,
    int TrackFadeInMs = AudioRules.DefaultTrackFadeInMs,
    int TrackFadeOutMs = AudioRules.DefaultTrackFadeOutMs,
    int VideoFadeInMs = AudioRules.DefaultVideoFadeMs,
    int VideoFadeOutMs = AudioRules.DefaultVideoFadeMs,
    bool ResumeOnDeviceReturn = false);

public static class AudioRules
{
    public const int DefaultMasterVolumePct = 50;
    public const int DefaultTrackFadeInMs = 500;
    public const int DefaultTrackFadeOutMs = 800;
    public const int DefaultVideoFadeMs = 400;
    public const int MaxFadeMs = 5000;
    /// <summary>Fundido fijo del «Silencio» de emergencia: lo bastante corto para ser inmediato, lo bastante largo para no dar un chasquido.</summary>
    public const int SilenceFadeMs = 100;
    public const int MaxDeviceIdChars = 400;
    public const int MaxDeviceNameChars = 200;

    /// <summary>Devuelve el problema (texto para el operador) o nulo si los ajustes son válidos.</summary>
    public static string? Validate(AudioSettings s)
    {
        if (s.MasterVolumePct is < 0 or > 100) return "El volumen maestro debe estar entre 0 y 100.";
        if (s.TrackFadeInMs is < 0 or > MaxFadeMs || s.TrackFadeOutMs is < 0 or > MaxFadeMs) return "El fundido de las pistas debe estar entre 0 y 5000 ms.";
        if (s.VideoFadeInMs is < 0 or > MaxFadeMs || s.VideoFadeOutMs is < 0 or > MaxFadeMs) return "El fundido del video debe estar entre 0 y 5000 ms.";
        if (s.ChurchDeviceId is { } id && (id.Length is 0 or > MaxDeviceIdChars || id.Any(char.IsControl))) return "El dispositivo de audio no es válido.";
        if (s.ChurchDeviceName is { } name && (name.Length > MaxDeviceNameChars || name.Any(char.IsControl))) return "El nombre del dispositivo de audio no es válido.";
        return null;
    }

    public static string MessageFor(string code) => code switch
    {
        AudioErrorCodes.DeviceNotFound => "El dispositivo de audio de la iglesia no está conectado: no se reproduce nada. Conéctalo o elige «Usar el predeterminado de Windows» en los ajustes de audio.",
        AudioErrorCodes.DeviceLost => "Dispositivo de audio desconectado: la pista está en pausa. No se pasa a otro dispositivo por sí solo.",
        AudioErrorCodes.PlayFailed => "No se pudo reproducir la pista: el archivo está dañado o el dispositivo de audio no lo admite.",
        AudioErrorCodes.NothingPlaying => "No hay ninguna pista cargada.",
        AudioErrorCodes.DeviceBusy => "El dispositivo de audio no está disponible ahora (¿otra aplicación lo usa en modo exclusivo?).",
        _ => "Error de audio.",
    };
}

/// <summary>
/// Cómo está el cableado de audio AHORA (función pura). Hay dos rutas físicas (la salida analógica de la PC o la interfaz USB hacia la mezcladora) que Windows ve como dos dispositivos, y Windows
/// puede cambiar el predeterminado por su cuenta al conectar o desconectar el USB. Las PISTAS usan el dispositivo de la iglesia (o el predeterminado si no hay uno elegido); el audio del
/// VIDEO en primer plano sale siempre por el predeterminado de Windows (limitación de WebView2). <see cref="Mismatch"/> = las dos cosas NO salen por el mismo sitio y el panel debe avisarlo.
/// </summary>
/// <param name="UsesWindowsDefault">No hay dispositivo de la iglesia elegido: las pistas usan el predeterminado de Windows.</param>
/// <param name="ChurchConnected">El dispositivo de la iglesia elegido está activo ahora (siempre true si <see cref="UsesWindowsDefault"/>).</param>
/// <param name="TracksDeviceId">Dispositivo por el que saldría una pista nueva (nulo si no hay ninguno disponible).</param>
/// <param name="Mismatch">Pistas y video NO coinciden: el dispositivo de la iglesia está conectado pero no es el predeterminado de Windows.</param>
public sealed record AudioRouteStatus(
    bool UsesWindowsDefault, string? ChurchDeviceId, string? ChurchDeviceName, bool ChurchConnected,
    string? DefaultDeviceId, string? DefaultDeviceName, string? TracksDeviceId, bool Mismatch);

public static class AudioRoutePolicy
{
    public static AudioRouteStatus Evaluate(AudioSettings settings, IReadOnlyList<AudioDeviceInfo> active, AudioDeviceInfo? windowsDefault)
    {
        if (settings.ChurchDeviceId is not { } churchId)
            return new AudioRouteStatus(true, null, null, true, windowsDefault?.Id, windowsDefault?.Name, windowsDefault?.Id, false);

        var church = active.FirstOrDefault(d => string.Equals(d.Id, churchId, StringComparison.Ordinal));
        var connected = church is not null;
        var mismatch = connected && windowsDefault is not null && !string.Equals(windowsDefault.Id, churchId, StringComparison.Ordinal);
        // Si no hay predeterminado de Windows pero el dispositivo de la iglesia sí está, también es una discrepancia: el video no tiene por dónde sonar.
        if (connected && windowsDefault is null) mismatch = true;
        return new AudioRouteStatus(false, churchId, church?.Name ?? settings.ChurchDeviceName, connected, windowsDefault?.Id, windowsDefault?.Name, connected ? churchId : null, mismatch);
    }
}
