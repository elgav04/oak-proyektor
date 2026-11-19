using OakProyektor.Ipc;

namespace OakProyektor.App;

/// <summary>
/// Publica snapshots completos con una secuencia global creciente (hallazgo I3). Construir el snapshot, asignarle su
/// secuencia y encolarlo ocurre BAJO UN MISMO CANDADO: así el orden en la cola es siempre el orden de las secuencias y
/// ningún snapshot viejo puede colarse detrás de uno nuevo. La secuencia cambia con CUALQUIER cambio del snapshot;
/// si no cambió nada, no se publica nada. Las réplicas descartan snapshots con secuencia menor o igual a la última aplicada.
/// </summary>
/// <remarks>
/// El delegado <c>build</c> se ejecuta con el candado tomado: puede tomar candados de ESTADO, pero nunca debe esperar al hilo de UI.
/// </remarks>
internal sealed class SequencedSnapshotPublisher<TSnapshot>(
    IOutbox outbox, string eventType, Targets targets, Func<long, TSnapshot, object> toPayload, IEqualityComparer<TSnapshot>? comparer = null)
{
    private readonly object _lock = new();
    private readonly IEqualityComparer<TSnapshot> _comparer = comparer ?? EqualityComparer<TSnapshot>.Default;
    private long _seq;
    private TSnapshot? _last;
    private bool _hasLast;

    /// <summary>Publica el snapshot actual si cambió. Devuelve true si publicó.</summary>
    public bool Publish(Func<TSnapshot> build)
    {
        lock (_lock) return PublishLocked(build()).Published;
    }

    /// <summary>
    /// Para replies de queries: publica primero si hay un cambio pendiente (el evento queda en la cola ANTES que el reply) y devuelve
    /// el snapshot actual con SU secuencia, de modo que reply y evento nunca se contradicen.
    /// </summary>
    public TResult Read<TResult>(Func<TSnapshot> build, Func<long, TSnapshot, TResult> make)
    {
        lock (_lock)
        {
            var snapshot = build();
            PublishLocked(snapshot);
            return make(_seq, snapshot);
        }
    }

    public long Seq { get { lock (_lock) return _seq; } }

    private (bool Published, long Seq) PublishLocked(TSnapshot snapshot)
    {
        if (_hasLast && _comparer.Equals(snapshot, _last!)) return (false, _seq);
        _seq++;
        _last = snapshot;
        _hasLast = true;
        outbox.Publish(IpcEnvelope.Create(IpcKind.Event, eventType, toPayload(_seq, snapshot)), targets);
        return (true, _seq);
    }
}
