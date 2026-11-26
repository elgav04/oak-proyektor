using Microsoft.Extensions.Logging;
using OakProyektor.Core.Alerts;
using OakProyektor.Core.Output;
using OakProyektor.Ipc;

namespace OakProyektor.App;

// Alertas rápidas (capa 3, protocolo v15). Toda la lógica de transición y de reloj vive en AlertEngine (Core, pura y probada con un reloj falso); aquí solo hay el pegamento:
// tomar el candado del estado, armar UN temporizador de un disparo y publicar. Nada de esto toca la máquina de estados de la pausa, la política de seguridad ni el sondeo de monitores.
internal sealed partial class OutputController
{
    private readonly AlertEngine _alerts = new(TimeProvider.System);   // solo bajo _gate
    private readonly object _alertTimerGate = new();
    private Timer? _alertTimer;                                        // solo bajo _alertTimerGate; nunca se toma _gate con este candado tomado

    public bool ShowAlert(string text, AlertMode mode, int durationSec, long order = 0)
    {
        if (order <= 0) order = _clock.Next();   // sin número (llamada interna): cuenta como «ahora»
        var theme = _settings.Current.Alerts.EffectiveTheme;
        AlertShowOutcome outcome;
        lock (_gate)
        {
            // Una alerta no sobrevive a una pausa: si la ventana está en pausa nadie la vería y al reanudar sería un aviso viejo. Solo se LEE el estado de la pausa.
            if (_window is not null && _pause.IsPaused)
                throw new IpcException(AlertErrorCodes.OutputPaused, "La salida está en pausa: la alerta no se mostró. Reanuda la salida y vuelve a mostrarla.");
            outcome = _alerts.Show(_state, text, mode, durationSec, theme, order);
            if (outcome.Applied) _state = outcome.State;
        }
        if (!outcome.Applied)
        {
            _logger.LogInformation("Se ignoró una alerta: el operador la quitó después de pedirla");
            return false;
        }
        ArmAlertTimer(outcome.Generation, outcome.Duration);
        _logger.LogInformation("Alerta {AlertId} mostrada: modo {Mode}, {Seconds} s", outcome.State.Overlays.Alert!.Id, mode, (int)outcome.Duration.TotalSeconds);
        Publish();
        return true;
    }

    public void HideAlert(long order = 0)
    {
        if (order <= 0) order = _clock.Next();
        bool had;
        lock (_gate)
        {
            had = _state.Overlays.Alert is not null;
            _state = _alerts.Hide(_state, order);
        }
        CancelAlertTimer();
        if (had) _logger.LogInformation("Alerta quitada por el operador");
        Publish();
    }

    /// <summary>
    /// «Una alerta no sobrevive a una pausa»: reacciona a la pausa que el snapshot YA informa (<c>window.paused</c>, la misma lectura de <c>BuildSnapshot</c>) y quita la alerta.
    /// No modifica ni consulta nada más que ese indicador: ni la máquina de estados de la pausa, ni <c>OutputSafety</c>, ni el camino de los monitores. Nunca lanza.
    /// </summary>
    private void DropAlertIfPaused()
    {
        try
        {
            bool dropped;
            lock (_gate)
            {
                dropped = _window is not null && _pause.IsPaused && _state.Overlays.Alert is not null;
                if (dropped) _state = _alerts.Drop(_state);
            }
            if (!dropped) return;
            CancelAlertTimer();
            _logger.LogWarning("La alerta se quitó porque la salida se pausó");
            _publisher.Publish(BuildSnapshot);
        }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo quitar la alerta al pausar la salida"); }
    }

    private void ArmAlertTimer(long generation, TimeSpan delay)
    {
        lock (_alertTimerGate)
        {
            _alertTimer?.Dispose();
            _alertTimer = new Timer(_ => OnAlertTimer(generation), null, delay, Timeout.InfiniteTimeSpan);
        }
    }

    private void CancelAlertTimer()
    {
        lock (_alertTimerGate)
        {
            _alertTimer?.Dispose();
            _alertTimer = null;
        }
    }

    /// <summary>Disparo del temporizador: solo quita la alerta si ese plazo sigue siendo el vigente (un disparo viejo no puede quitar una alerta nueva). Nunca lanza.</summary>
    private void OnAlertTimer(long generation)
    {
        try
        {
            AlertExpiry result;
            lock (_gate)
            {
                result = _alerts.Expire(_state, generation);
                if (result.State is { } next) _state = next;
            }
            if (result.RetryIn is { } retry) { ArmAlertTimer(generation, retry); return; }   // disparó un poco antes de tiempo
            if (result.State is null) return;
            _logger.LogInformation("La alerta caducó");
            Publish();
        }
        catch (Exception ex) { _logger.LogError(ex, "Falló el temporizador de la alerta"); }
    }
}
