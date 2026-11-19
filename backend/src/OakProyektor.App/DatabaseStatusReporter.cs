using OakProyektor.Core.Database;
using OakProyektor.Data;
using OakProyektor.Ipc;
using OakProyektor.Ipc.Messages;

namespace OakProyektor.App;

/// <summary>
/// Avisa al panel de los cambios del estado de la base de datos (hallazgo I9): publica <c>database.statusChanged</c> con secuencia
/// (como el resto de snapshots) y responde a <c>database.getStatus</c>, para que un panel que cargue DESPUÉS de un fallo también lo vea.
/// </summary>
internal sealed class DatabaseStatusReporter
{
    private readonly IDatabaseReadiness _readiness;
    private readonly SequencedSnapshotPublisher<DatabaseStatus> _publisher;

    public DatabaseStatusReporter(IDatabaseReadiness readiness, IOutbox outbox)
    {
        _readiness = readiness;
        _publisher = new SequencedSnapshotPublisher<DatabaseStatus>(
            outbox, IpcTypes.DatabaseStatusChanged, Targets.Control, (seq, s) => new DatabaseStatusPayload(seq, s.State, s.Message, s.BackupPath));
        readiness.Changed += _ => _publisher.Publish(() => _readiness.Status);
    }

    public DatabaseStatusPayload Snapshot() =>
        _publisher.Read(() => _readiness.Status, (seq, s) => new DatabaseStatusPayload(seq, s.State, s.Message, s.BackupPath));
}