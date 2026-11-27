namespace OakProyektor.App.Audio;

internal enum PlaybackEndReason
{
    /// <summary>La pista llegó a su final.</summary>
    Finished,
    /// <summary>El dispositivo desapareció (USB desenchufado, Bluetooth caído).</summary>
    DeviceLost,
    /// <summary>Otro fallo de reproducción.</summary>
    Failed,
}

/// <summary>Fallo esperado del motor de audio (código estable <c>media.*</c>; el mensaje nunca cita rutas ni nombres).</summary>
internal sealed class AudioException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}

/// <summary>
/// Una pista abierta en un dispositivo. Nace en PAUSA (<see cref="Open"/> no suena): <see cref="Play"/> la arranca con su fundido de entrada. Todos los métodos son rápidos y no bloquean al llamador
/// (los fundidos corren en el hilo de audio); <see cref="Dispose"/> libera el dispositivo y el archivo.
/// </summary>
internal interface IAudioPlayback : IDisposable
{
    long PositionMs { get; }
    long DurationMs { get; }
    /// <summary>Arranca (o reanuda) con un fundido de entrada de <paramref name="fadeInMs"/> hasta la ganancia actual.</summary>
    void Play(int fadeInMs);
    /// <summary>Baja a silencio en <paramref name="fadeMs"/> ms y pausa conservando la posición.</summary>
    void Pause(int fadeMs);
    void Seek(long positionMs);
    /// <summary>Ganancia de 0 a 1 (clip × maestro), alcanzada en <paramref name="rampMs"/> ms.</summary>
    void SetGain(float gain, int rampMs);
    /// <summary>Fundido de salida y liberación (la pista no se puede volver a usar).</summary>
    void Stop(int fadeMs);
    /// <summary>Terminó sola o falló; NO se avisa de un <see cref="Stop"/>/<see cref="IDisposable.Dispose"/> pedidos por el llamador. Puede llegar en cualquier hilo.</summary>
    event Action<PlaybackEndReason, string?>? Ended;
}

/// <summary>Costura con la biblioteca de audio (NAudio): permite probar todo el motor con un respaldo falso.</summary>
internal interface IAudioBackend
{
    /// <summary>Abre <paramref name="filePath"/> en el dispositivo <paramref name="deviceId"/> (nulo = el predeterminado de Windows). <c>AudioException</c> si no se puede.</summary>
    IAudioPlayback Open(string? deviceId, string filePath, bool loop, float gain);

    /// <summary>Un tono suave y corto por el dispositivo (nulo = el predeterminado). Termina al acabar el tono; <c>AudioException</c> si no se puede abrir.</summary>
    Task PlayToneAsync(string? deviceId, int frequencyHz, int durationMs, float gain, CancellationToken cancellationToken);
}
