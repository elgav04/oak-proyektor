namespace OakProyektor.Core.Audio;

/// <summary>
/// Ganancia con rampas lineales por MUESTRA (fundidos de entrada y salida, y silencio inmediato sin chasquido). Función pura con estado mínimo y segura entre hilos: el hilo de audio
/// llama a <see cref="Apply"/> en cada búfer y cualquier otro hilo puede pedir <see cref="SetTarget"/> (un candado corto: nunca se espera a nada lento). La rampa cuenta los cuadros que
/// le faltan y al llegar a cero salta EXACTAMENTE al objetivo (la acumulación en coma flotante no deja nunca un resto audible). Probada sin dispositivos de audio.
/// </summary>
public sealed class GainRamp(int sampleRate, float initialGain = 0f)
{
    private readonly object _gate = new();
    private float _current = Math.Clamp(initialGain, 0f, 1f);
    private float _target = Math.Clamp(initialGain, 0f, 1f);
    private float _step;        // por cuadro (un cuadro = una muestra de cada canal)
    private long _framesLeft;   // cuadros que faltan para llegar al objetivo

    public float Current { get { lock (_gate) return _current; } }
    public float Target { get { lock (_gate) return _target; } }

    /// <summary>Pide llegar a <paramref name="target"/> (0–1) en <paramref name="rampMs"/> ms desde donde esté ahora. 0 ms = al instante (para el siguiente búfer).</summary>
    public void SetTarget(float target, int rampMs)
    {
        target = Math.Clamp(target, 0f, 1f);
        lock (_gate)
        {
            _target = target;
            var frames = (long)rampMs * sampleRate / 1000;
            if (frames <= 0) { _current = target; _step = 0; _framesLeft = 0; return; }
            _framesLeft = frames;
            _step = (target - _current) / frames;
        }
    }

    /// <summary>Multiplica el búfer (cuadros de <paramref name="channels"/> muestras) por la ganancia, avanzando la rampa.</summary>
    public void Apply(Span<float> buffer, int channels)
    {
        if (channels < 1) channels = 1;
        float current, target, step;
        long left;
        lock (_gate) { current = _current; target = _target; step = _step; left = _framesLeft; }

        var frames = buffer.Length / channels;
        var pos = 0;
        for (var f = 0; f < frames; f++)
        {
            if (left > 0)
            {
                current += step;
                if (--left == 0) current = target;   // llegó: exactamente el objetivo
            }
            for (var c = 0; c < channels; c++) buffer[pos++] *= current;
        }
        for (; pos < buffer.Length; pos++) buffer[pos] *= current;   // muestras sueltas de un último cuadro incompleto

        // Si otro hilo pidió otro objetivo mientras se procesaba el búfer, manda el pedido nuevo (su rampa arranca del valor que tenía al pedirlo: a lo sumo un búfer de diferencia).
        lock (_gate) { if (_target == target) { _current = current; _step = step; _framesLeft = left; } }
    }
}