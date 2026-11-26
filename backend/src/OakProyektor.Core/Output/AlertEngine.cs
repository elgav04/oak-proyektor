using System.Globalization;

namespace OakProyektor.Core.Output;

/// <summary>Resultado de <see cref="AlertEngine.Show"/>. <see cref="Applied"/> = false: se ignoró (el operador la quitó DESPUÉS de pedir esta); el estado no cambió.</summary>
/// <param name="Generation">Identifica el plazo armado: el temporizador lo devuelve a <see cref="AlertEngine.Expire"/>.</param>
/// <param name="Duration">Cuánto falta para que caduque (el tiempo que debe esperar el temporizador).</param>
public sealed record AlertShowOutcome(OutputState State, bool Applied, long Generation, TimeSpan Duration);

/// <summary>Resultado de <see cref="AlertEngine.Expire"/>: <see cref="State"/> si la alerta caducó; <see cref="RetryIn"/> si el temporizador disparó antes de tiempo (hay que rearmarlo).</summary>
public sealed record AlertExpiry(OutputState? State, TimeSpan? RetryIn);

/// <summary>
/// Reloj y reglas de transición de la alerta: lógica PURA, sin hilos ni temporizadores (el controlador arma uno con el plazo que se le devuelve). NO es seguro entre hilos: el
/// controlador la llama bajo el candado de su estado. El plazo se mide con la marca de tiempo MONÓTONA del <see cref="TimeProvider"/>: un cambio de hora de Windows no alarga ni
/// acorta un aviso. Cada <see cref="Show"/> y cada <see cref="Hide"/> suben la GENERACIÓN: el disparo de un temporizador viejo no puede quitar una alerta nueva.
/// <para>Orden de llegada: <see cref="Hide"/> recuerda su número y <see cref="Show"/> con un número MENOR se ignora (misma técnica que la pausa de emergencia): un «mostrar» ya encolado en
/// otro carril no puede revivir una alerta que el operador acaba de quitar con el atajo global. La alerta NO se persiste: reiniciar la aplicación nunca revive un aviso.</para>
/// </summary>
public sealed class AlertEngine(TimeProvider clock)
{
    private long _generation;
    private long _idCounter;
    private long _hideOrder;
    private bool _armed;
    private long _startTimestamp;
    private TimeSpan _duration;

    public bool IsArmed => _armed;
    public long Generation => _generation;

    public AlertShowOutcome Show(OutputState state, string text, AlertMode mode, int durationSec, AlertTheme theme, long order)
    {
        if (order < _hideOrder) return new AlertShowOutcome(state, false, _generation, TimeSpan.Zero);

        var sticky = durationSec == 0;
        var duration = TimeSpan.FromSeconds(sticky ? AlertRules.MaxStickySeconds : durationSec);
        var current = state.Overlays.Alert;
        // Mismo texto, modo y tema mientras ya está visible: se conserva el id (la salida no parpadea ni reinicia el desplazamiento); solo cambia la caducidad.
        var id = current is not null && current.Text == text && current.Mode == mode && current.Theme == theme ? current.Id : $"a{++_idCounter}";
        var expires = clock.GetUtcNow().Add(duration).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

        _generation++;
        _armed = true;
        _startTimestamp = clock.GetTimestamp();
        _duration = duration;
        return new AlertShowOutcome(state.WithAlert(new AlertOverlay(id, text, mode, theme, sticky, expires)), true, _generation, duration);
    }

    /// <summary>Quita la alerta (idempotente) y recuerda el número de llegada de esta orden.</summary>
    public OutputState Hide(OutputState state, long order)
    {
        _hideOrder = Math.Max(_hideOrder, order);
        return Drop(state);
    }

    /// <summary>Quita la alerta SIN tocar el orden: la pausa de la salida («una alerta no sobrevive a una pausa»). Idempotente.</summary>
    public OutputState Drop(OutputState state)
    {
        _generation++;
        _armed = false;
        return state.WithAlert(null);
    }

    /// <summary>El temporizador de <paramref name="generation"/> disparó. Solo quita la alerta si ese plazo sigue siendo el vigente y ya venció.</summary>
    public AlertExpiry Expire(OutputState state, long generation)
    {
        if (!_armed || generation != _generation) return new AlertExpiry(null, null);
        var remaining = _duration - clock.GetElapsedTime(_startTimestamp);
        if (remaining > TimeSpan.Zero) return new AlertExpiry(null, remaining);   // un disparo anticipado: se rearma, nunca se quita antes de tiempo
        _armed = false;
        return new AlertExpiry(state.WithAlert(null), null);
    }
}
