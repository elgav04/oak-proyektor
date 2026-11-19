using Microsoft.Extensions.Logging;
using OakProyektor.Core.Output;

namespace OakProyektor.App;

/// <summary>
/// Vigila que el JavaScript del panel siga vivo (<see cref="PanelLivenessTracker"/>). Cualquier mensaje que llegue del panel (su latido es
/// <c>database.getStatus</c> cada 4 s) cuenta como actividad; sin ninguna durante ~10 s se registra, se marca «panel sin respuesta» en el estado
/// (<see cref="IOutputController.SetPanelUnresponsive"/>) y, SOLO si el operador lo configuró, la salida lo muestra. La salida sigue proyectando
/// sin depender del panel (verificado, ver <c>tools/i8-frozen-panel-test.ps1</c>). Se evalúa en el sondeo de monitores (500 ms): no hay otro hilo.
/// </summary>
internal sealed class PanelWatchdog(IDisplayManager displays, IOutputController output, TimeProvider time, ILogger<PanelWatchdog> logger)
{
    private readonly object _gate = new();
    private readonly PanelLivenessTracker _tracker = new();
    private int _started;

    /// <summary>Llegó un mensaje del panel (se llama desde el handler de Photino: debe ser barato y no lanzar).</summary>
    public void Touch()
    {
        lock (_gate) _tracker.Touch(time.GetUtcNow());
    }

    /// <summary>La página del panel empieza a cargarse (arranque o recarga): el silencio se cuenta desde ahora.</summary>
    public void Reset()
    {
        lock (_gate) _tracker.Reset(time.GetUtcNow());
    }

    /// <summary>¿Está marcado ahora como sin respuesta? (última evaluación)</summary>
    public bool IsUnresponsive
    {
        get { lock (_gate) return _tracker.IsUnresponsiveAt(time.GetUtcNow()); }
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        Reset();
        displays.Polled += _ => Evaluate();
    }

    internal void Evaluate()
    {
        try
        {
            PanelHealth health;
            lock (_gate) health = _tracker.Evaluate(time.GetUtcNow());
            if (!health.Changed) return;

            if (health.Unresponsive)
                logger.LogWarning("El panel dejó de responder: {Seconds:F0} s sin ningún mensaje (su JavaScript está atascado o la página no cargó). La salida sigue proyectando; atajos globales de respaldo activos", health.Silence.TotalSeconds);
            else
                logger.LogInformation("El panel volvió a responder");
            output.SetPanelUnresponsive(health.Unresponsive);
        }
        catch (Exception ex) { logger.LogError(ex, "Falló la vigilancia del panel"); }
    }
}
