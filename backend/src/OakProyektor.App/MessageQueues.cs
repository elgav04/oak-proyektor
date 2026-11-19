using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using OakProyektor.Ipc;

namespace OakProyektor.App;

/// <summary>
/// Entrada: los handlers de Photino (hilo de UI) solo encolan y vuelven al instante. Un enrutador (<see cref="InboundProcessor"/>)
/// lee de aquí, interpreta y reparte a los carriles de prioridad y normal.
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

/// <summary>Cola de mensajes salientes hacia las ventanas.</summary>
internal interface IOutbox
{
    void Publish(IpcEnvelope envelope, Targets targets);
    IAsyncEnumerable<(Targets Target, string Json)> ReadAllAsync(CancellationToken ct);
    void Complete();
}

/// <summary>
/// Salida: único canal FIFO hacia todas las ventanas, como exige CLAUDE.md.
/// Si se llena (la UI no consume), se conserva solo el ÚLTIMO snapshot de cada tipo y destino: los snapshots son completos e
/// idempotentes, así que los intermedios no hacen falta; replies, errores y demás mensajes no se compactan. Solo si aun así
/// no hay espacio se descarta el mensaje más antiguo.
/// </summary>
internal sealed class Outbox(ILogger<Outbox> logger, int capacity = Outbox.DefaultCapacity) : IOutbox
{
    public const int DefaultCapacity = 512;
    private static readonly HashSet<string> SnapshotTypes = [IpcTypes.OutputStateChanged, IpcTypes.DisplayChanged];

    private sealed record Item(Targets Target, string Json, string Type);

    private readonly object _lock = new();
    private readonly LinkedList<Item> _items = new();
    private readonly SemaphoreSlim _signal = new(0);
    private bool _closed;

    public void Publish(IpcEnvelope envelope, Targets targets)
    {
        var item = new Item(targets, IpcSerializer.Serialize(envelope), envelope.Type);   // serializar fuera del candado
        lock (_lock)
        {
            if (_closed)
            {
                logger.LogDebug("Cola de salida IPC cerrada (apagando); {Type} descartado", envelope.Type);
                return;
            }
            if (_items.Count >= capacity) MakeRoom(item);
            _items.AddLast(item);
        }
        _signal.Release();
    }

    /// <summary>
    /// Compacta: de cada (tipo de snapshot, destino) queda solo el ÚLTIMO, y si el mensaje entrante es un snapshot, él es el último
    /// (sustituye a los anteriores de su mismo tipo y destino). Llamar con el candado tomado.
    /// </summary>
    private void MakeRoom(Item incoming)
    {
        var incomingKey = SnapshotTypes.Contains(incoming.Type) ? (incoming.Type, incoming.Target) : ((string, Targets)?)null;
        var lastOfKind = new Dictionary<(string Type, Targets Target), LinkedListNode<Item>>();
        for (var node = _items.First; node is not null; node = node.Next)
            if (SnapshotTypes.Contains(node.Value.Type)) lastOfKind[(node.Value.Type, node.Value.Target)] = node;

        var dropped = 0;
        for (var node = _items.First; node is not null;)
        {
            var next = node.Next;
            if (SnapshotTypes.Contains(node.Value.Type))
            {
                var key = (node.Value.Type, node.Value.Target);
                if (key == incomingKey || !ReferenceEquals(lastOfKind[key], node))
                {
                    _items.Remove(node);
                    dropped++;
                }
            }
            node = next;
        }
        if (dropped > 0)
            logger.LogWarning("Cola de salida IPC llena: se conservó solo el último snapshot de cada tipo ({Dropped} descartados)", dropped);

        if (_items.Count >= capacity)
        {
            logger.LogWarning("Cola de salida IPC llena de mensajes no compactables; se descarta el más antiguo ({Type})", _items.First!.Value.Type);
            _items.RemoveFirst();
        }
    }

    public async IAsyncEnumerable<(Targets Target, string Json)> ReadAllAsync([EnumeratorCancellation] CancellationToken ct)
    {
        while (true)
        {
            Item? item = null;
            bool closed;
            lock (_lock)
            {
                if (_items.First is { } first) { item = first.Value; _items.RemoveFirst(); }
                closed = _closed;
            }
            if (item is not null) { yield return (item.Target, item.Json); continue; }
            if (closed) yield break;
            await _signal.WaitAsync(ct).ConfigureAwait(false);   // un despertar espurio (cola compactada) simplemente vuelve a comprobar
        }
    }

    public void Complete()
    {
        lock (_lock) _closed = true;
        _signal.Release();
    }
}
