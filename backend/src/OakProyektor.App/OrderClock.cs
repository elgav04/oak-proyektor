namespace OakProyektor.App;

/// <summary>
/// Reloj de llegada de las órdenes del operador: cada mensaje entrante (lo numera el enrutador, <see cref="InboundProcessor"/>) y cada pulsación
/// del atajo de emergencia reciben un número creciente. Sirve para decidir, entre los dos carriles de entrada, cuál fue ANTES
/// (ver <c>OutputPauseState</c>). Es un contador atómico, no una hora: sin estado estático.
/// </summary>
internal sealed class OrderClock
{
    private long _last;

    /// <summary>El siguiente número (empieza en 1; 0 queda reservado para «sin numerar»).</summary>
    public long Next() => Interlocked.Increment(ref _last);
}
