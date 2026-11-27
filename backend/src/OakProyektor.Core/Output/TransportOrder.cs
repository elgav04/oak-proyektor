namespace OakProyektor.Core.Output;

/// <summary>
/// Regla de «la última intención del operador gana», por NÚMERO DE LLEGADA (<c>IpcEnvelope.Order</c>, que se asigna al recibir el mensaje o al pulsar el atajo global), para las órdenes de
/// reproducción que viajan por carriles distintos (<c>media.play</c> por el lento, <c>media.pause</c> y <c>media.stopAll</c> por el de prioridad): un «reproducir» ya numerado ANTES de un
/// «pausar/detener» que se procesó primero se IGNORA (si no, el video arrancaría después de haberlo callado), y un «pausar» numerado antes de un «reproducir» ya aplicado también.
/// Mismo principio que <see cref="AlertEngine"/> y la pausa de emergencia. NO es seguro entre hilos: lo llama quien ya tiene su candado.
/// </summary>
public sealed class TransportOrder
{
    private long _play;
    private long _halt;

    /// <summary>¿Puede aplicarse un «reproducir/reanudar» con este número? (no si un «pausar/detener» posterior ya llegó). Si puede, lo registra.</summary>
    public bool AllowPlay(long order)
    {
        if (order < _halt) return false;
        _play = Math.Max(_play, order);
        return true;
    }

    /// <summary>¿Puede aplicarse un «pausar/detener» con este número? (no si un «reproducir/reanudar» posterior ya se aplicó). Si puede, lo registra.</summary>
    public bool AllowHalt(long order)
    {
        if (order < _play) return false;
        _halt = Math.Max(_halt, order);
        return true;
    }
}
