using System.Text.Json;
using Microsoft.Extensions.Logging;
using OakProyektor.Ipc.Messages;

namespace OakProyektor.Ipc;

/// <summary>
/// Interpreta y enruta mensajes entrantes. Contrato: <c>DispatchAsync</c> NO lanza nunca (salvo cancelación) y, si algo falla,
/// devuelve el mensaje de error correlacionado para que el solicitante no se quede esperando:
/// una query fallida devuelve un reply <c>{ ok: false, error }</c>; un comando fallido devuelve un evento <c>command.failed</c>.
/// Solo se descartan en silencio (con log) los mensajes que no se pueden correlacionar: JSON roto, versión inválida, o
/// kinds <c>event</c>/<c>reply</c> entrantes.
/// </summary>
public sealed class IpcDispatcher
{
    private readonly Dictionary<(string Kind, string Type), IIpcHandler> _handlers = [];
    private readonly ILogger<IpcDispatcher> _logger;

    public IpcDispatcher(IEnumerable<IIpcHandler> handlers, ILogger<IpcDispatcher> logger)
    {
        _logger = logger;
        foreach (var handler in handlers)
        {
            if (handler.Kind is not (IpcKind.Command or IpcKind.Query))
                throw new ArgumentException($"El handler de {handler.Type} debe ser command o query, no {handler.Kind}.");
            if (!_handlers.TryAdd((handler.Kind, handler.Type), handler))
                throw new ArgumentException($"Handler duplicado para {handler.Kind} {handler.Type}.");
        }
    }

    /// <returns>El mensaje a devolver al solicitante (reply, o <c>command.failed</c>); null si no hay nada que devolver.</returns>
    public Task<IpcEnvelope?> DispatchAsync(string? raw, CancellationToken cancellationToken)
    {
        if (!IpcSerializer.TryParse(raw, out var envelope, out var error) || envelope is null)
        {
            _logger.LogWarning("Mensaje IPC descartado: {Error}", error);
            return Task.FromResult<IpcEnvelope?>(null);
        }
        return DispatchAsync(envelope, cancellationToken);
    }

    public async Task<IpcEnvelope?> DispatchAsync(IpcEnvelope envelope, CancellationToken cancellationToken)
    {
        if (envelope.Kind is not (IpcKind.Command or IpcKind.Query))
        {
            _logger.LogWarning("Mensaje IPC entrante ignorado: kind {Kind} no se acepta desde la UI (type={Type})", envelope.Kind, envelope.Type);
            return null;
        }

        if (!_handlers.TryGetValue((envelope.Kind, envelope.Type), out var handler))
        {
            _logger.LogWarning("Mensaje IPC desconocido: kind={Kind} type={Type} id={Id}", envelope.Kind, envelope.Type, envelope.Id);
            return Failure(envelope, new IpcError("unknown_message", $"El servidor no soporta {envelope.Kind} «{envelope.Type}»."));
        }

        try
        {
            var result = await handler.HandleAsync(envelope, cancellationToken).ConfigureAwait(false);
            if (envelope.Kind != IpcKind.Query) return null;
            if (result is null)
            {
                _logger.LogWarning("La query {Type} (id {Id}) no produjo respuesta", envelope.Type, envelope.Id);
                return Failure(envelope, new IpcError("no_result", $"La consulta «{envelope.Type}» no produjo resultado."));
            }
            return IpcEnvelope.Create(IpcKind.Reply, envelope.Type, new IpcReply(true, result, null), envelope.Id);   // el reply conserva el id de la query
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (IpcException ex)
        {
            _logger.LogWarning(ex, "{Kind} {Type} (id {Id}) falló: {Code}", envelope.Kind, envelope.Type, envelope.Id, ex.Code);
            return Failure(envelope, new IpcError(ex.Code, ex.Message));
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Payload inválido en {Kind} {Type} (id {Id})", envelope.Kind, envelope.Type, envelope.Id);
            return Failure(envelope, new IpcError("invalid_payload", $"El mensaje «{envelope.Type}» tiene un payload inválido."));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falló el handler de {Kind} {Type} (id {Id})", envelope.Kind, envelope.Type, envelope.Id);
            return Failure(envelope, new IpcError("internal", "Error interno; revisa el registro de la aplicación."));   // sin detalles internos hacia la UI
        }
    }

    /// <summary>
    /// La respuesta de error correlacionada para <paramref name="request"/>: un reply <c>{ ok: false, error }</c> si era una query, o el evento
    /// <c>command.failed</c> si era un comando. Pública para que quien descarte un mensaje (p. ej. un carril lleno) avise al solicitante en vez de callar.
    /// </summary>
    public static IpcEnvelope Failure(IpcEnvelope request, IpcError error) =>
        request.Kind == IpcKind.Query
            ? IpcEnvelope.Create(IpcKind.Reply, request.Type, new IpcReply(false, null, error), request.Id)
            : IpcEnvelope.Create(IpcKind.Event, IpcTypes.CommandFailed, new CommandFailedPayload(request.Id, request.Type, error));
}
