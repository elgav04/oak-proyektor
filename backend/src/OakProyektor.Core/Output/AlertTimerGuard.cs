namespace OakProyektor.Core.Output;

/// <summary>
/// Regla de armado del temporizador de la alerta, PURA y con su propio candado: guarda la última generación armada (o cancelada) e IGNORA cualquier armado con una generación menor.
/// Sin ella, un disparo anticipado (<see cref="AlertExpiry.RetryIn"/>) que se rearma desde su hilo podía llegar DESPUÉS de que <c>ShowAlert</c> armara el plazo de una alerta
/// nueva y destruirlo, dejando un temporizador que se ignora (generación vieja): la alerta nueva no caducaba nunca, ni siquiera a los 30 min del tope.
/// La comprobación y la acción de armar ocurren bajo el MISMO candado: no hay hueco entre «es la última» y «reemplazar el temporizador».
/// </summary>
public sealed class AlertTimerGuard
{
    private readonly object _gate = new();
    private long _last;

    /// <summary>Última generación armada o cancelada (para los tests).</summary>
    public long Last { get { lock (_gate) return _last; } }

    /// <summary>Ejecuta <paramref name="arm"/> solo si <paramref name="generation"/> no es menor que la última armada o cancelada. Devuelve si se armó.</summary>
    public bool TryArm(long generation, Action arm)
    {
        lock (_gate)
        {
            if (generation < _last) return false;
            _last = generation;
            arm();
            return true;
        }
    }

    /// <summary>Cancela el temporizador (<paramref name="cancel"/>) y recuerda la generación de esa cancelación: ningún rearmado de una generación anterior puede resucitarlo.</summary>
    public void Cancel(long generation, Action cancel)
    {
        lock (_gate)
        {
            if (generation > _last) _last = generation;
            cancel();
        }
    }
}
