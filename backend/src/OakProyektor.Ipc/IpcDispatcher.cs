using Microsoft.Extensions.Logging;

namespace OakProyektor.Ipc;

/// <summary>
/// Interpreta y enruta mensajes entrantes. Contrato: <see cref="DispatchAsync"/> NO lanza nunca
/// (salvo cancelación): mensajes inválidos o desconocidos y fallos de handlers se registran y se ignoran.
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

    /// <returns>El reply para una query atendida; null en cualquier otro caso.</returns>
    public async Task<IpcEnvelope?> DispatchAsync(string? raw, CancellationToken cancellationToken)
    {
        if (!IpcSerializer.TryParse(raw, out var envelope, out var error) || envelope is null)
        {
            _logger.LogWarning("Mensaje IPC descartado: {Error}", error);
            return null;
        }

        if (!_handlers.TryGetValue((envelope.Kind, envelope.Type), out var handler))
        {
            _logger.LogWarning("Mensaje IPC desconocido ignorado: kind={Kind} type={Type} id={Id}", envelope.Kind, envelope.Type, envelope.Id);
            return null;
        }

        try
        {
            var result = await handler.HandleAsync(envelope, cancellationToken).ConfigureAwait(false);
            if (envelope.Kind != IpcKind.Query) return null;
            if (result is null)
            {
                _logger.LogWarning("La query {Type} (id {Id}) no produjo respuesta", envelope.Type, envelope.Id);
                return null;
            }
            return IpcEnvelope.Create(IpcKind.Reply, envelope.Type, result, envelope.Id);   // el reply conserva el id de la query
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falló el handler de {Kind} {Type} (id {Id})", envelope.Kind, envelope.Type, envelope.Id);
            return null;
        }
    }
}
