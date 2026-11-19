using Microsoft.Extensions.Logging;

namespace OakProyektor.App;

/// <summary>Tareas de fondo: el procesador de entrada (dos carriles) y el envío de los mensajes salientes a las ventanas.</summary>
internal sealed class IpcPump(InboundProcessor inbound, IOutbox outbox, WindowRegistry windows, IUiDispatcher ui, ILogger<IpcPump> logger)
{
    public Task[] Start(CancellationToken ct) => [.. inbound.Start(ct), Task.Run(() => RunOutboundAsync(ct), CancellationToken.None)];

    private async Task RunOutboundAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var (target, json) in outbox.ReadAllAsync(ct).ConfigureAwait(false))
            {
                foreach (var kind in new[] { WindowKind.Control, WindowKind.Output })
                {
                    var flag = kind == WindowKind.Control ? Targets.Control : Targets.Output;
                    if (!target.HasFlag(flag) || ct.IsCancellationRequested) continue;
                    var window = windows.GetIfReady(kind);
                    if (window is null) continue;
                    try { ui.Invoke(() => window.SendWebMessage(json)); }
                    catch (Exception ex) { logger.LogWarning(ex, "No se pudo enviar un mensaje a la ventana {Kind}", kind); }   // p. ej. ventana ya cerrada
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* apagado */ }
        catch (Exception ex) { logger.LogCritical(ex, "La cola de salida IPC terminó inesperadamente"); }
    }
}
