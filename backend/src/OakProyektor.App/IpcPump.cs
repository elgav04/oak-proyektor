using Microsoft.Extensions.Logging;
using OakProyektor.Ipc;

namespace OakProyektor.App;

/// <summary>Dos tareas de fondo: procesa mensajes entrantes en orden y envía los salientes a las ventanas.</summary>
internal sealed class IpcPump(
    InboundQueue inbound, Outbox outbox, IpcDispatcher dispatcher, WindowRegistry windows, IUiDispatcher ui, ILogger<IpcPump> logger)
{
    public Task[] Start(CancellationToken ct) => [Task.Run(() => RunInboundAsync(ct), CancellationToken.None), Task.Run(() => RunOutboundAsync(ct), CancellationToken.None)];

    private async Task RunInboundAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var (source, raw) in inbound.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    var reply = await dispatcher.DispatchAsync(raw, ct).ConfigureAwait(false);
                    if (reply is not null) outbox.Publish(reply, source == WindowKind.Control ? Targets.Control : Targets.Output);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { logger.LogError(ex, "Error procesando un mensaje de {Source}", source); }   // una falla no detiene la cola
            }
        }
        catch (OperationCanceledException) { /* apagado */ }
        catch (Exception ex) { logger.LogCritical(ex, "La cola de entrada IPC terminó inesperadamente"); }
    }

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
        catch (OperationCanceledException) { /* apagado */ }
        catch (Exception ex) { logger.LogCritical(ex, "La cola de salida IPC terminó inesperadamente"); }
    }
}

