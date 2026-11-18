using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using OakProyektor.Ipc;

namespace OakProyektor.App;

/// <summary>
/// Entrada: los handlers de Photino (hilo de UI) solo encolan y vuelven al instante; otro hilo procesa en orden.
/// </summary>
internal sealed class InboundQueue(ILogger<InboundQueue> logger)
{
    private volatile bool _closed;
    private readonly Channel<(WindowKind Source, string Raw)> _channel =
        Channel.CreateBounded<(WindowKind, string)>(new BoundedChannelOptions(512) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

    /// <summary>No bloquea ni lanza. Devuelve false (y registra) si la cola está llena o cerrada.</summary>
    public bool Enqueue(WindowKind source, string raw)
    {
        var ok = _channel.Writer.TryWrite((source, raw));
        if (!ok) logger.Log(_closed ? LogLevel.Debug : LogLevel.Warning, "Cola de entrada IPC {State}; mensaje de {Source} descartado", _closed ? "cerrada (apagando)" : "llena", source);
        return ok;
    }

    public IAsyncEnumerable<(WindowKind Source, string Raw)> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
    public void Complete() { _closed = true; _channel.Writer.TryComplete(); }
}

/// <summary>Salida: único canal (FIFO) hacia todas las ventanas, como exige CLAUDE.md.</summary>
internal sealed class Outbox(ILogger<Outbox> logger)
{
    private volatile bool _closed;
    private readonly Channel<(Targets Target, string Json)> _channel =
        Channel.CreateBounded<(Targets, string)>(new BoundedChannelOptions(512) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

    public void Publish(IpcEnvelope envelope, Targets targets)
    {
        if (!_channel.Writer.TryWrite((targets, IpcSerializer.Serialize(envelope))))
            logger.Log(_closed ? LogLevel.Debug : LogLevel.Warning, "Cola de salida IPC {State}; {Type} descartado", _closed ? "cerrada (apagando)" : "llena", envelope.Type);
    }

    public IAsyncEnumerable<(Targets Target, string Json)> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
    public void Complete() { _closed = true; _channel.Writer.TryComplete(); }
}

