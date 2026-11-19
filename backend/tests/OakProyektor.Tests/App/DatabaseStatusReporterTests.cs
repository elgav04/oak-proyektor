using Microsoft.Extensions.Logging.Abstractions;
using OakProyektor.App;
using OakProyektor.Core.Database;
using OakProyektor.Data;
using OakProyektor.Ipc;
using OakProyektor.Ipc.Messages;

namespace OakProyektor.Tests.App;

internal sealed class FakeReadiness : IDatabaseReadiness
{
    public DatabaseStatus Status { get; private set; } = new(DatabaseState.Initializing, null, null);
    public event Action<DatabaseStatus>? Changed;
    public Task WhenReadyAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public void Set(DatabaseStatus status) { Status = status; Changed?.Invoke(status); }
}

public class DatabaseStatusReporterTests
{
    private static async Task<List<(Targets Target, IpcEnvelope Env)>> Drain(Outbox outbox)
    {
        outbox.Complete();
        var list = new List<(Targets, IpcEnvelope)>();
        await foreach (var (target, json) in outbox.ReadAllAsync(CancellationToken.None))
        {
            Assert.True(IpcSerializer.TryParse(json, out var env, out _));
            list.Add((target, env!));
        }
        return list;
    }

    [Fact]
    public async Task A_failure_reaches_the_panel_as_an_event_with_the_message_and_the_backup_path()
    {
        var readiness = new FakeReadiness();
        var outbox = new Outbox(NullLogger<Outbox>.Instance);
        _ = new DatabaseStatusReporter(readiness, outbox);

        readiness.Set(new DatabaseStatus(DatabaseState.Failed, "Falló la migración 3", "C:/data/backups/oak.v2.db"));

        var events = await Drain(outbox);
        var (target, env) = Assert.Single(events);
        Assert.Equal((Targets.Control, "event", "database.statusChanged"), (target, env.Kind, env.Type));
        var p = env.GetPayload<DatabaseStatusPayload>();
        Assert.Equal((DatabaseState.Failed, "Falló la migración 3", "C:/data/backups/oak.v2.db"), (p.State, p.Message, p.BackupPath));
        Assert.Equal(1, p.Seq);
    }

    [Fact]
    public async Task Each_real_change_gets_the_next_sequence_and_repeats_are_not_published()
    {
        var readiness = new FakeReadiness();
        var outbox = new Outbox(NullLogger<Outbox>.Instance);
        _ = new DatabaseStatusReporter(readiness, outbox);

        readiness.Set(new DatabaseStatus(DatabaseState.Ready, null, null));
        readiness.Set(new DatabaseStatus(DatabaseState.Ready, null, null));      // igual: no se repite
        readiness.Set(new DatabaseStatus(DatabaseState.Failed, "x", null));

        var seqs = (await Drain(outbox)).Select(e => e.Env.GetPayload<DatabaseStatusPayload>().Seq);
        Assert.Equal(new long[] { 1, 2 }, seqs);
    }

    [Fact]
    public void A_panel_that_loads_after_the_failure_can_still_ask_for_the_status()
    {
        var readiness = new FakeReadiness();
        var reporter = new DatabaseStatusReporter(readiness, new Outbox(NullLogger<Outbox>.Instance));
        readiness.Set(new DatabaseStatus(DatabaseState.Failed, "disco lleno", null));

        var snapshot = reporter.Snapshot();

        Assert.Equal((DatabaseState.Failed, "disco lleno"), (snapshot.State, snapshot.Message));
        Assert.True(snapshot.Seq >= 1);
    }

    [Fact]
    public void The_initial_snapshot_is_initializing()
    {
        var reporter = new DatabaseStatusReporter(new FakeReadiness(), new Outbox(NullLogger<Outbox>.Instance));
        Assert.Equal(DatabaseState.Initializing, reporter.Snapshot().State);
    }
}