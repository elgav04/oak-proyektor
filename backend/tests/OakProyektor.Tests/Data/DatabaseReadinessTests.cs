using Microsoft.Extensions.Logging.Abstractions;
using OakProyektor.Core.Database;
using OakProyektor.Data;
using OakProyektor.Data.Migrations;

namespace OakProyektor.Tests.Data;

/// <summary>I9: la BD se inicializa en segundo plano; los repositorios esperan la señal y un fallo se comunica.</summary>
public sealed class DatabaseReadinessTests : IDisposable
{
    private readonly TestDatabase _db = new();
    public void Dispose() => _db.Dispose();

    private sealed class FakeInitializer(Func<CancellationToken, Task<MigrationRunResult>> body) : IDatabaseInitializer
    {
        public Task<MigrationRunResult> InitializeAsync(CancellationToken ct) => body(ct);
    }

    private static DatabaseReadiness Create() => new(NullLogger<DatabaseReadiness>.Instance);
    private static MigrationRunResult Done(string? backup = null) => new(0, 3, backup, [1, 2, 3]);

    [Fact]
    public async Task Starts_as_initializing_waits_until_the_initialization_completes_and_then_reports_ready()
    {
        var readiness = Create();
        var gate = new TaskCompletionSource<MigrationRunResult>();
        var changes = new List<DatabaseStatus>();
        readiness.Changed += changes.Add;

        Assert.Equal(DatabaseState.Initializing, readiness.Status.State);
        var start = readiness.StartAsync(new FakeInitializer(_ => gate.Task), CancellationToken.None);
        var waiting = readiness.WhenReadyAsync(CancellationToken.None);

        await Task.Delay(50);
        Assert.False(waiting.IsCompleted);                    // el repositorio espera: la BD aún no está lista

        gate.SetResult(Done("C:/copia.db"));
        await start;
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new DatabaseStatus(DatabaseState.Ready, null, "C:/copia.db"), readiness.Status);
        Assert.Equal([DatabaseState.Ready], changes.Select(c => c.State));
    }

    [Fact]
    public async Task A_failure_is_reported_in_the_status_and_the_event_not_thrown_and_waiters_get_a_clear_exception()
    {
        var readiness = Create();
        var changes = new List<DatabaseStatus>();
        readiness.Changed += changes.Add;
        var waiting = readiness.WhenReadyAsync(CancellationToken.None);

        await readiness.StartAsync(new FakeInitializer(_ => throw new DatabaseInitializationException("Falló la migración 3", "C:/copia.db")), CancellationToken.None);   // NO lanza

        var status = readiness.Status;
        Assert.Equal(DatabaseState.Failed, status.State);
        Assert.Equal("Falló la migración 3", status.Message);
        Assert.Equal("C:/copia.db", status.BackupPath);                    // para decirle al operador dónde están sus datos originales
        Assert.Single(changes);
        Assert.Equal(DatabaseState.Failed, changes[0].State);

        var ex = await Assert.ThrowsAsync<DatabaseUnavailableException>(() => waiting);
        Assert.Contains("Falló la migración 3", ex.Message);
        await Assert.ThrowsAsync<DatabaseUnavailableException>(() => readiness.WhenReadyAsync(CancellationToken.None));   // y quien llegue después, igual
        await Assert.ThrowsAsync<DatabaseUnavailableException>(() => readiness.WhenReadyAsync(CancellationToken.None));   // las veces que sea: el fallo no se "consume"
    }

    [Fact]
    public async Task A_shutdown_cancellation_is_not_reported_as_a_database_failure()
    {
        var readiness = Create();
        var changes = new List<DatabaseStatus>();
        readiness.Changed += changes.Add;
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await readiness.StartAsync(new FakeInitializer(ct => Task.FromCanceled<MigrationRunResult>(ct)), cts.Token);

        Assert.Equal(DatabaseState.Initializing, readiness.Status.State);   // no se marcó Failed: el apagado no es un fallo
        Assert.Empty(changes);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readiness.WhenReadyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_waiter_can_cancel_without_affecting_the_initialization()
    {
        var readiness = Create();
        using var cts = new CancellationTokenSource(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readiness.WhenReadyAsync(cts.Token));

        Assert.Equal(DatabaseState.Initializing, readiness.Status.State);
    }

    [Fact]
    public async Task A_throwing_subscriber_does_not_break_the_readiness()
    {
        var readiness = Create();
        readiness.Changed += _ => throw new InvalidOperationException("suscriptor roto");

        await readiness.StartAsync(new FakeInitializer(_ => Task.FromResult(Done())), CancellationToken.None);

        await readiness.WhenReadyAsync(CancellationToken.None);
        Assert.Equal(DatabaseState.Ready, readiness.Status.State);
    }

    [Fact]
    public async Task OpenWhenReady_blocks_until_the_database_is_initialized_and_then_opens_a_usable_connection()
    {
        var readiness = Create();
        var gate = new TaskCompletionSource();
        var start = readiness.StartAsync(new FakeInitializer(async ct => { await gate.Task; return await _db.Initializer.InitializeAsync(ct); }), CancellationToken.None);

        var opening = _db.Connections.OpenWhenReadyAsync(readiness, CancellationToken.None);   // lo que hace todo repositorio
        await Task.Delay(80);
        Assert.False(opening.IsCompleted);                                                      // espera: todavía no hay esquema
        Assert.False(File.Exists(_db.Options.DatabasePath));                                    // (ni siquiera se creó el archivo)

        gate.SetResult();
        await start;
        await using var c = await opening.WaitAsync(TimeSpan.FromSeconds(5));
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Songs";                                         // el esquema ya existe
        Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task OpenWhenReady_fails_with_DatabaseUnavailable_when_the_initialization_failed()
    {
        var readiness = Create();
        await readiness.StartAsync(new FakeInitializer(_ => throw new InvalidOperationException("disco lleno")), CancellationToken.None);

        var ex = await Assert.ThrowsAsync<DatabaseUnavailableException>(() => _db.Connections.OpenWhenReadyAsync(readiness, CancellationToken.None));

        Assert.Contains("disco lleno", ex.Message);
    }

    [Fact]
    public async Task With_the_real_initializer_a_database_newer_than_the_app_ends_as_failed_with_a_clear_message()
    {
        await _db.InitializeAsync();
        await using (var c = await _db.OpenAsync())
            await Dapper.SqlMapper.ExecuteAsync(c, "PRAGMA user_version = 999");
        var readiness = Create();

        await readiness.StartAsync(_db.Initializer, CancellationToken.None);

        Assert.Equal(DatabaseState.Failed, readiness.Status.State);
        Assert.Contains("v999", readiness.Status.Message);
        Assert.Null(readiness.Status.BackupPath);   // no se migró nada: no hay copia
    }

    [Fact]
    public async Task With_the_real_initializer_a_failed_migration_reports_where_the_pre_migration_backup_is()
    {
        await _db.InitializeAsync();
        await using (var c = await _db.OpenAsync())
        {
            await Dapper.SqlMapper.ExecuteAsync(c, "PRAGMA user_version = 2");
            // Deja el esquema como si estuviera en v2 y con un servicio que tiene ItemsJson con datos: la migración 3 abortará.
            await Dapper.SqlMapper.ExecuteAsync(c, "DROP TABLE PlaylistItems; ALTER TABLE Playlists ADD COLUMN ItemsJson TEXT NOT NULL DEFAULT '[]'; INSERT INTO Playlists (Name, ItemsJson) VALUES ('x', '[{\"a\":1}]')");
        }
        var readiness = Create();

        await readiness.StartAsync(_db.Initializer, CancellationToken.None);

        Assert.Equal(DatabaseState.Failed, readiness.Status.State);
        Assert.Contains("migración 3", readiness.Status.Message);
        Assert.NotNull(readiness.Status.BackupPath);
        Assert.True(File.Exists(readiness.Status.BackupPath));
    }
}