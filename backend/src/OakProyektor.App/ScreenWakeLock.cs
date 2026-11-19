using Microsoft.Extensions.Logging;

namespace OakProyektor.App;

/// <summary>
/// Evita que Windows apague la pantalla o suspenda el equipo mientras la salida esté abierta (I14).
/// SetThreadExecutionState es por hilo: se llama siempre en el hilo de UI, que vive toda la sesión.
/// </summary>
internal sealed class ScreenWakeLock(IUiDispatcher ui, ILogger<ScreenWakeLock> logger)
{
    public void Acquire() => Set("activado", NativeMethods.ES_CONTINUOUS | NativeMethods.ES_SYSTEM_REQUIRED | NativeMethods.ES_DISPLAY_REQUIRED);

    public void Release() => Set("liberado", NativeMethods.ES_CONTINUOUS);

    private void Set(string what, uint flags)
    {
        try
        {
            uint previous = 0;
            ui.Invoke(() => previous = NativeMethods.SetThreadExecutionState(flags));
            if (previous == 0) logger.LogWarning("SetThreadExecutionState falló; la pantalla podría apagarse con la salida abierta");
            else logger.LogInformation("Bloqueo de apagado de pantalla {What}", what);
        }
        catch (Exception ex) { logger.LogWarning(ex, "No se pudo cambiar el bloqueo de apagado de pantalla"); }
    }
}
