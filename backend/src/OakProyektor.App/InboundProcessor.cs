using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using OakProyektor.Ipc;
using OakProyektor.Ipc.Messages;

namespace OakProyektor.App;

/// <summary>
/// Procesa los mensajes entrantes en DOS carriles (hallazgo I5): los de prioridad (<see cref="IpcPriority"/>: controles maestros y
/// recuperación de emergencia) tienen su propio consumidor y nunca esperan detrás de comandos lentos como <c>output.open</c>.
/// Dentro de cada carril el orden es FIFO; entre carriles no hay orden garantizado. El enrutador solo interpreta y reparte
/// (microsegundos): nunca ejecuta handlers, así que no puede retrasar a nadie.
/// </summary>
internal sealed class InboundProcessor(
    InboundQueue intake, IpcDispatcher dispatcher, IOutbox outbox, OrderClock clock, ILogger<InboundProcessor> logger, int laneCapacity = InboundProcessor.DefaultLaneCapacity)
{
    public const int DefaultLaneCapacity = 512;

    private readonly record struct Work(WindowKind Source, IpcEnvelope Envelope);

    public Task[] Start(CancellationToken ct)
    {
        var priority = Channel.CreateBounded<Work>(new BoundedChannelOptions(laneCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        var normal = Channel.CreateBounded<Work>(new BoundedChannelOptions(laneCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        return
        [
            Task.Run(() => RouteAsync(priority.Writer, normal.Writer, ct), CancellationToken.None),
            Task.Run(() => WorkAsync("prioridad", priority.Reader, ct), CancellationToken.None),
            Task.Run(() => WorkAsync("normal", normal.Reader, ct), CancellationToken.None),
        ];
    }

    private async Task RouteAsync(ChannelWriter<Work> priority, ChannelWriter<Work> normal, CancellationToken ct)
    {
        try
        {
            await foreach (var (source, raw) in intake.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    if (!IpcSerializer.TryParse(raw, out var envelope, out var error) || envelope is null)
                    {
                        logger.LogWarning("Mensaje IPC de {Source} descartado: {Error}", source, error);
                        continue;
                    }
                    // Se numera aquí, en el único hilo que lee la cola FIFO de entrada: el número refleja el orden de llegada real, aunque
                    // luego los dos carriles se procesen en otro orden (así «Mover / reabrir» seguido de «Recuperar control» se puede resolver).
                    envelope = envelope with { Order = clock.Next() };
                    var lane = IpcPriority.IsHigh(envelope) ? priority : normal;
                    if (!lane.TryWrite(new Work(source, envelope)))
                        RejectOverloaded(source, envelope);
                }
                catch (Exception ex) { logger.LogError(ex, "Error enrutando un mensaje de {Source}", source); }   // un mensaje malo no detiene el enrutador
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* apagado */ }
        catch (Exception ex) { logger.LogCritical(ex, "El enrutador de entrada IPC terminó inesperadamente"); }
        finally
        {
            priority.TryComplete();
            normal.TryComplete();
        }
    }

    /// <summary>
    /// El carril está lleno (un handler lento o atascado no deja avanzar): el mensaje se DESCARTA, pero ya no en silencio. Quien lo mandó recibe el
    /// error correlacionado (<c>command.failed</c> para un comando, reply con error para una query) y el panel se lo muestra: el operador sabe que
    /// su orden NO se ejecutó y puede repetirla, en vez de creer que se hizo.
    /// </summary>
    private void RejectOverloaded(WindowKind source, IpcEnvelope envelope)
    {
        logger.LogError("Carril IPC lleno; se descarta {Kind} {Type} (id {Id}) de {Source} y se avisa al solicitante", envelope.Kind, envelope.Type, envelope.Id, source);
        try
        {
            var error = new IpcError("overloaded", $"El programa está saturado y no pudo atender «{envelope.Type}»: la orden NO se ejecutó. Inténtalo de nuevo.");
            outbox.Publish(IpcDispatcher.Failure(envelope, error), source == WindowKind.Control ? Targets.Control : Targets.Output);
        }
        catch (Exception ex) { logger.LogError(ex, "No se pudo avisar de que se descartó {Type}", envelope.Type); }   // avisar nunca debe detener el enrutador
    }

    private async Task WorkAsync(string lane, ChannelReader<Work> reader, CancellationToken ct)
    {
        try
        {
            await foreach (var (source, envelope) in reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    var response = await dispatcher.DispatchAsync(envelope, ct).ConfigureAwait(false);
                    if (response is not null) outbox.Publish(response, source == WindowKind.Control ? Targets.Control : Targets.Output);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }   // M12: solo el apagado real detiene el carril
                catch (Exception ex) { logger.LogError(ex, "Error procesando {Type} de {Source} (carril {Lane})", envelope.Type, source, lane); }   // una falla no detiene el carril
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* apagado */ }
        catch (Exception ex) { logger.LogCritical(ex, "El carril {Lane} de entrada IPC terminó inesperadamente", lane); }
    }
}
