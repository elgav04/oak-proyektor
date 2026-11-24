using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using OakProyektor.App.Cli;
using OakProyektor.Core.Database;
using OakProyektor.Data;
using OakProyektor.Data.Backups;

namespace OakProyektor.Tests.Data;

/// <summary>Sin avisos de respaldos (para los tests de handlers que no los usan).</summary>
internal sealed class NoBackupWarnings : IBackupStatus
{
    public IReadOnlyList<string> Warnings => [];
    public Task Finished => Task.CompletedTask;
}

internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}

internal sealed class ReadyNow : IDatabaseReadiness
{
    public DatabaseStatus Status => new(DatabaseState.Ready, null, null);
    public event Action<DatabaseStatus>? Changed { add { } remove { } }
    public Task WhenReadyAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class FailingVerifier(string detail = "fallo simulado") : IBackupVerifier
{
    public Task<BackupVerification> VerifyAsync(string path, CancellationToken cancellationToken) => Task.FromResult(new BackupVerification(false, detail, 0));
}

public class BackupTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 11, 24, 9, 0, 0, TimeSpan.Zero));

    public void Dispose() => _db.Dispose();

    private DatabaseBackupService Service(IBackupVerifier? verifier = null, Func<IEnumerable<string?>>? roots = null) =>
        new(_db.Connections, _db.Options, new ReadyNow(), _clock, NullLogger<DatabaseBackupService>.Instance)
        { StartDelay = TimeSpan.Zero, Verifier = verifier ?? new SqliteBackupVerifier(), SyncRoots = roots ?? (() => []) };

    private async Task SeedAsync(string text)
    {
        await _db.InitializeAsync();
        await using var c = await _db.OpenAsync();
        await TestDatabase.InsertVerseAsync(c, text, "SIN" + Guid.NewGuid().ToString("N")[..4]);
    }

    private static async Task<long> CountVersesAsync(string path)
    {
        await using var c = await SqliteConnectionFactory.OpenFileReadOnlyAsync(path, CancellationToken.None);
        return await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Verses;");
    }

    private string Backups => _db.Options.BackupDirectory;

    private void Touch(string name)
    {
        Directory.CreateDirectory(Backups);
        File.WriteAllText(Path.Combine(Backups, name), "x");
    }

    // ---- diario ----

    [Fact]
    public async Task The_daily_backup_is_created_verified_and_contains_the_data()
    {
        await SeedAsync("texto sintetico uno");
        var service = Service();
        await service.RunDailyAsync(CancellationToken.None);

        Assert.Equal(DailyBackupOutcome.Created, service.LastResult.Outcome);
        var expected = Path.Combine(Backups, "oak-proyektor.daily.2026-11-24.db");
        Assert.Equal(expected, service.LastResult.Path);
        Assert.True(File.Exists(expected));
        Assert.True(service.LastResult.Verification!.Ok);
        Assert.Equal(1, await CountVersesAsync(expected));
        Assert.Empty(service.Warnings);
        Assert.True(service.Finished.IsCompletedSuccessfully);
        Assert.Empty(Directory.GetFiles(Backups, "*.partial"));
    }

    [Fact]
    public async Task At_most_one_daily_backup_per_day_and_a_new_one_the_next_day()
    {
        await SeedAsync("a");
        await Service().RunDailyAsync(CancellationToken.None);
        var first = new FileInfo(Path.Combine(Backups, "oak-proyektor.daily.2026-11-24.db")).LastWriteTimeUtc;

        var again = Service();
        await again.RunDailyAsync(CancellationToken.None);
        Assert.Equal(DailyBackupOutcome.AlreadyDone, again.LastResult.Outcome);
        Assert.Equal(first, new FileInfo(Path.Combine(Backups, "oak-proyektor.daily.2026-11-24.db")).LastWriteTimeUtc);   // no se rehizo

        _clock.Now = _clock.Now.AddDays(1);
        var next = Service();
        await next.RunDailyAsync(CancellationToken.None);
        Assert.Equal(DailyBackupOutcome.Created, next.LastResult.Outcome);
        Assert.Equal(2, Directory.GetFiles(Backups, "oak-proyektor.daily.*.db").Length);
    }

    [Fact]
    public async Task Only_the_last_14_daily_backups_are_kept_and_other_kinds_are_never_touched()
    {
        await SeedAsync("a");
        for (var d = 1; d <= 16; d++) Touch($"oak-proyektor.daily.2026-10-{d:00}.db");   // 16 antiguas
        // Copias de OTRAS clases, muchas y antiguas: no deben contarse ni borrarse por la retención de las diarias.
        for (var i = 1; i <= 7; i++) Touch($"oak-proyektor.bible-import.2026010{i}T000000000Z.SIN1.db");   // 7 > 5, pero esa clase la poda el importador
        Touch("oak-proyektor.v2.20260101T000000000Z.db");
        Touch("notas.db");
        Touch("oak-proyektor.daily.2026-11-01.db.partial");

        var service = Service();
        await service.RunDailyAsync(CancellationToken.None);

        var daily = Directory.GetFiles(Backups, "oak-proyektor.daily.*.db").Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(14, daily.Count);
        Assert.Contains("oak-proyektor.daily.2026-11-24.db", daily);          // la de hoy se queda
        Assert.DoesNotContain("oak-proyektor.daily.2026-10-01.db", daily);    // las más antiguas se van
        Assert.Contains("oak-proyektor.daily.2026-10-04.db", daily);          // 14 = hoy + las 13 más recientes de octubre (4..16)
        Assert.Equal(5, Directory.GetFiles(Backups, "oak-proyektor.bible-import.*.db").Length);   // su propia retención (5): se podan por su cuenta, no por la de las diarias
        Assert.True(File.Exists(Path.Combine(Backups, "oak-proyektor.v2.20260101T000000000Z.db")));   // migración intacta
        Assert.True(File.Exists(Path.Combine(Backups, "notas.db")));                                    // lo ajeno, en paz
    }

    [Fact]
    public async Task Retention_of_each_kind_is_independent()
    {
        for (var i = 1; i <= 25; i++) Touch($"oak-proyektor.v{i}.20260101T0000{i:00}000Z.db");
        for (var i = 1; i <= 12; i++) Touch($"oak-proyektor.before-restore.20260101T0000{i:00}000Z.db");
        for (var d = 1; d <= 20; d++) Touch($"oak-proyektor.daily.2026-10-{d:00}.db");

        foreach (var kind in Enum.GetValues<BackupKind>()) BackupCatalog.Prune(Backups, kind);

        Assert.Equal(20, BackupCatalog.List(Backups).Count(b => b.Kind == BackupKind.Migration));
        Assert.Equal(10, BackupCatalog.List(Backups).Count(b => b.Kind == BackupKind.BeforeRestore));
        Assert.Equal(14, BackupCatalog.List(Backups).Count(b => b.Kind == BackupKind.Daily));
        // Se conservan las MÁS RECIENTES de cada una.
        Assert.True(File.Exists(Path.Combine(Backups, "oak-proyektor.v25.20260101T000025000Z.db")));
        Assert.False(File.Exists(Path.Combine(Backups, "oak-proyektor.v1.20260101T000001000Z.db")));
    }

    [Fact]
    public async Task A_failing_integrity_check_deletes_the_copy_warns_and_never_throws()
    {
        await SeedAsync("a");
        var service = Service(new FailingVerifier("fila 3 corrupta"));
        await service.RunDailyAsync(CancellationToken.None);   // no lanza

        Assert.Equal(DailyBackupOutcome.Failed, service.LastResult.Outcome);
        Assert.False(File.Exists(Path.Combine(Backups, "oak-proyektor.daily.2026-11-24.db")));   // una copia mala no se hace pasar por buena
        Assert.Empty(Directory.GetFiles(Backups, "*.partial"));
        var warning = Assert.Single(service.Warnings);
        Assert.Contains("integrity_check", warning);
        Assert.Contains("fila 3 corrupta", warning);
        Assert.True(service.Finished.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task An_impossible_backup_folder_is_logged_as_a_warning_and_does_not_throw()
    {
        await SeedAsync("a");
        // La «carpeta de respaldos» es un ARCHIVO: Directory.CreateDirectory falla.
        File.WriteAllText(Backups, "no soy una carpeta");

        var service = Service();
        await service.RunDailyAsync(CancellationToken.None);

        Assert.Equal(DailyBackupOutcome.Failed, service.LastResult.Outcome);
        Assert.Contains("No se pudo hacer el respaldo diario", Assert.Single(service.Warnings));
        Assert.True(service.Finished.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task A_database_that_failed_to_initialize_gives_a_warning_not_a_crash()
    {
        var service = new DatabaseBackupService(_db.Connections, _db.Options, new FailedReadiness(), _clock, NullLogger<DatabaseBackupService>.Instance)
        { StartDelay = TimeSpan.Zero, SyncRoots = () => [] };
        await service.RunDailyAsync(CancellationToken.None);
        Assert.Equal(DailyBackupOutcome.Failed, service.LastResult.Outcome);
        Assert.Single(service.Warnings);
    }

    private sealed class FailedReadiness : IDatabaseReadiness
    {
        public DatabaseStatus Status => new(DatabaseState.Failed, "inicializacion fallida", null);
        public event Action<DatabaseStatus>? Changed { add { } remove { } }
        public Task WhenReadyAsync(CancellationToken cancellationToken) => throw new DatabaseUnavailableException("inicializacion fallida");   // como la real
    }

    [Fact]
    public async Task Cancelling_at_shutdown_is_not_a_failure()
    {
        await SeedAsync("a");
        var service = new DatabaseBackupService(_db.Connections, _db.Options, new ReadyNow(), _clock, NullLogger<DatabaseBackupService>.Instance)
        { StartDelay = TimeSpan.FromMinutes(5), SyncRoots = () => [] };
        using var cts = new CancellationTokenSource();
        var run = service.RunDailyAsync(cts.Token);
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(service.Warnings);
        Assert.True(service.Finished.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task The_backup_does_not_block_writers_while_it_copies()
    {
        await SeedAsync("a");
        var service = Service();
        var run = service.RunDailyAsync(CancellationToken.None);
        await using (var c = await _db.OpenAsync())   // una escritura concurrente (modo WAL) no espera a la copia
            await c.ExecuteAsync("UPDATE Bibles SET Name = 'otro nombre' WHERE 1;");
        await run;
        Assert.Equal(DailyBackupOutcome.Created, service.LastResult.Outcome);
    }

    // ---- carpetas sincronizadas ----

    [Theory]
    [InlineData(@"C:\Users\x\OneDrive\Documents\oak\data", @"C:\Users\x\OneDrive", "OneDrive")]
    [InlineData(@"C:\Users\x\OneDrive2\OneDrive\oak\data", null, "OneDrive")]          // por el nombre del segmento
    [InlineData(@"D:\datos\oak", @"D:\datos", "OneDrive")]                              // por la raíz del entorno
    [InlineData(@"C:\Users\x\Dropbox\oak", null, "Dropbox")]
    [InlineData(@"C:\Users\x\Google Drive\oak", null, "Google Drive")]
    [InlineData(@"C:\Users\x\AppData\Local\Oak-Proyektor", @"C:\Users\x\OneDrive", null)]
    [InlineData(@"C:\Users\x\OneDriveX\oak", @"C:\Users\x\OneDrive", "OneDrive")]      // prefijo del nombre: se avisa (mejor un aviso de más)
    [InlineData(@"C:\proyectos\oak\data", @"", null)]
    public void Synced_folders_are_detected(string path, string? root, string? expected) =>
        Assert.Equal(expected, SyncedFolders.Find(path, [root]));

    [Fact]
    public async Task A_synced_data_folder_produces_a_warning_before_the_delay()
    {
        await SeedAsync("a");
        var data = Path.GetDirectoryName(_db.Options.DatabasePath)!;
        var service = new DatabaseBackupService(_db.Connections, _db.Options, new ReadyNow(), _clock, NullLogger<DatabaseBackupService>.Instance)
        { StartDelay = TimeSpan.FromMinutes(5), SyncRoots = () => [Path.GetDirectoryName(data)] };
        using var cts = new CancellationTokenSource();
        var run = service.RunDailyAsync(cts.Token);
        var warning = Assert.Single(service.Warnings);   // ya está, aunque la copia aún no empezó
        Assert.Contains("sincronizada", warning);
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // ---- catálogo ----

    [Theory]
    [InlineData("oak-proyektor.daily.2026-11-24.db", BackupKind.Daily)]
    [InlineData("oak-proyektor.v3.20261124T031500123Z.db", BackupKind.Migration)]
    [InlineData("oak-proyektor.bible-import.20261124T031500123Z.RVR1960.db", BackupKind.BibleImport)]
    [InlineData("oak-proyektor.before-restore.20261124T031500123Z.db", BackupKind.BeforeRestore)]
    public void Backups_are_classified_by_name(string name, BackupKind kind) => Assert.Equal(kind, BackupCatalog.Classify(name));

    [Theory]
    [InlineData("oak-proyektor.db")]
    [InlineData("oak-proyektor.daily.2026-11-24.db.partial")]
    [InlineData("oak-proyektor.daily.2026-11-24.db-wal")]
    [InlineData("otra.db")]
    [InlineData("oak-proyektor.vX.20261124T031500123Z.db")]
    public void Anything_else_is_not_a_backup(string name) => Assert.Null(BackupCatalog.Classify(name));

    [Fact]
    public void Resolve_accepts_a_name_with_or_without_extension_a_fragment_or_a_full_path_and_rejects_ambiguity()
    {
        Touch("oak-proyektor.daily.2026-11-23.db");
        Touch("oak-proyektor.daily.2026-11-24.db");
        var exact = Path.Combine(Backups, "oak-proyektor.daily.2026-11-24.db");
        Assert.Equal(exact, BackupCatalog.Resolve(Backups, "oak-proyektor.daily.2026-11-24.db"));
        Assert.Equal(exact, BackupCatalog.Resolve(Backups, "oak-proyektor.daily.2026-11-24"));
        Assert.Equal(exact, BackupCatalog.Resolve(Backups, "11-24"));
        Assert.Equal(exact, BackupCatalog.Resolve(Backups, exact));
        Assert.Contains("2 copias", Assert.Throws<BackupException>(() => BackupCatalog.Resolve(Backups, "daily")).Message);
        Assert.Contains("No hay ninguna", Assert.Throws<BackupException>(() => BackupCatalog.Resolve(Backups, "nada")).Message);
        Assert.Throws<BackupException>(() => BackupCatalog.Resolve(Backups, " "));
    }

    // ---- verificación ----

    [Fact]
    public async Task The_verifier_accepts_a_good_copy_and_flags_garbage_truncated_and_empty_files()
    {
        await SeedAsync("a");
        var service = Service();
        await service.RunDailyAsync(CancellationToken.None);
        var good = service.LastResult.Path!;
        var verifier = new SqliteBackupVerifier();
        var ok = await verifier.VerifyAsync(good, CancellationToken.None);
        Assert.True(ok.Ok);
        Assert.Equal(DatabaseInitializer.LatestSchemaVersion, ok.SchemaVersion);

        var garbage = Path.Combine(Backups, "basura.db");
        File.WriteAllText(garbage, new string('x', 5000));
        Assert.False((await verifier.VerifyAsync(garbage, CancellationToken.None)).Ok);

        var truncated = Path.Combine(Backups, "cortada.db");
        var bytes = File.ReadAllBytes(good);
        File.WriteAllBytes(truncated, bytes[..(bytes.Length / 2)]);
        Assert.False((await verifier.VerifyAsync(truncated, CancellationToken.None)).Ok);

        var empty = Path.Combine(Backups, "vacia.db");
        File.WriteAllBytes(empty, []);
        Assert.False((await verifier.VerifyAsync(empty, CancellationToken.None)).Ok);
        Assert.False((await verifier.VerifyAsync(Path.Combine(Backups, "no-existe.db"), CancellationToken.None)).Ok);
    }

    [Fact]
    public async Task Flipped_bytes_inside_a_page_are_caught_when_they_break_the_structure()
    {
        await SeedAsync("a");
        var service = Service();
        await service.RunDailyAsync(CancellationToken.None);
        var damaged = Path.Combine(Backups, "danada.db");
        var bytes = File.ReadAllBytes(service.LastResult.Path!);
        for (var i = 4096; i < Math.Min(bytes.Length, 4096 * 6); i++) bytes[i] = 0xFF;   // páginas 2 a 6 destruidas
        File.WriteAllBytes(damaged, bytes);
        Assert.False((await new SqliteBackupVerifier().VerifyAsync(damaged, CancellationToken.None)).Ok);
    }

    // ---- restauración ----

    [Fact]
    public async Task End_to_end_create_data_back_up_corrupt_restore_and_check()
    {
        await SeedAsync("texto sintetico ORIGINAL");
        var backup = Service();
        await backup.RunDailyAsync(CancellationToken.None);
        var copy = backup.LastResult.Path!;
        var originalCount = await CountVersesAsync(_db.Options.DatabasePath);

        // Después del respaldo el operador sigue trabajando… y entonces la base se corrompe.
        await using (var c = await _db.OpenAsync())
            await c.ExecuteAsync("INSERT INTO Verses (BookId, Chapter, VerseNumber, Text) SELECT BookId, 2, 1, 'posterior al respaldo' FROM Verses LIMIT 1;");
        SqliteConnection.ClearAllPools();
        File.WriteAllText(_db.Options.DatabasePath, new string('Z', 20000));
        File.WriteAllText(_db.Options.DatabasePath + "-wal", "wal viejo que no debe aplicarse");

        var result = await new DatabaseRestorer(_db.Options, time: _clock).RestoreAsync(copy, CancellationToken.None);

        // La base restaurada es la del respaldo, sana, sin el WAL viejo, y la dañada quedó guardada aparte.
        Assert.True((await new SqliteBackupVerifier().VerifyAsync(_db.Options.DatabasePath, CancellationToken.None)).Ok);
        Assert.Equal(originalCount, await CountVersesAsync(_db.Options.DatabasePath));
        Assert.False(File.Exists(_db.Options.DatabasePath + "-wal"));
        Assert.NotNull(result.PreviousSavedAs);
        Assert.StartsWith(Path.Combine(Backups, "oak-proyektor.before-restore."), result.PreviousSavedAs);
        Assert.Equal(new string('Z', 20000), File.ReadAllText(result.PreviousSavedAs!));   // la dañada se conservó tal cual
        Assert.True(File.Exists(result.PreviousSavedAs + "-wal"));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(_db.Options.DatabasePath)!, "*.restoring"));

        // Y la aplicación puede abrirla y migrarla con normalidad.
        var init = await _db.Initializer.InitializeAsync(CancellationToken.None);
        Assert.Equal(DatabaseInitializer.LatestSchemaVersion, init.ToVersion);
        Assert.Equal(originalCount, await CountVersesAsync(_db.Options.DatabasePath));
    }

    [Fact]
    public async Task Restoring_over_a_healthy_database_saves_it_first_with_everything_it_had()
    {
        await SeedAsync("version A");
        var backup = Service();
        await backup.RunDailyAsync(CancellationToken.None);
        await using (var c = await _db.OpenAsync())
            await c.ExecuteAsync("UPDATE Verses SET Text = 'version B';");   // cambio posterior, aún en el WAL

        var result = await new DatabaseRestorer(_db.Options, time: _clock).RestoreAsync(backup.LastResult.Path!, CancellationToken.None);

        await using (var restored = await SqliteConnectionFactory.OpenFileReadOnlyAsync(_db.Options.DatabasePath, CancellationToken.None))
            Assert.Equal("version A", await restored.ExecuteScalarAsync<string>("SELECT Text FROM Verses;"));
        await using var saved = await SqliteConnectionFactory.OpenFileReadOnlyAsync(result.PreviousSavedAs!, CancellationToken.None);
        Assert.Equal("version B", await saved.ExecuteScalarAsync<string>("SELECT Text FROM Verses;"));   // lo del WAL también se guardó
        Assert.True((await new SqliteBackupVerifier().VerifyAsync(result.PreviousSavedAs!, CancellationToken.None)).Ok);
    }

    [Fact]
    public async Task Restoring_when_there_is_no_current_database_just_places_the_copy()
    {
        await SeedAsync("a");
        var backup = Service();
        await backup.RunDailyAsync(CancellationToken.None);
        SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(_db.Options.DatabasePath)!, "oak-proyektor.db*")) File.Delete(f);

        var result = await new DatabaseRestorer(_db.Options, time: _clock).RestoreAsync(backup.LastResult.Path!, CancellationToken.None);
        Assert.Null(result.PreviousSavedAs);
        Assert.Equal(1, await CountVersesAsync(_db.Options.DatabasePath));
    }

    [Fact]
    public async Task A_corrupt_backup_is_refused_and_nothing_changes()
    {
        await SeedAsync("a");
        SqliteConnection.ClearAllPools();
        var before = File.ReadAllBytes(_db.Options.DatabasePath);
        var bad = Path.Combine(Backups, "oak-proyektor.daily.2026-11-01.db");
        Directory.CreateDirectory(Backups);
        File.WriteAllText(bad, new string('q', 9000));

        var ex = await Assert.ThrowsAsync<BackupException>(() => new DatabaseRestorer(_db.Options, time: _clock).RestoreAsync(bad, CancellationToken.None));
        Assert.Contains("integrity_check", ex.Message);
        Assert.Equal(before, File.ReadAllBytes(_db.Options.DatabasePath));
        Assert.Empty(Directory.GetFiles(Backups, "oak-proyektor.before-restore.*"));   // ni siquiera se guardó la actual: no se tocó nada
    }

    [Fact]
    public async Task A_backup_from_a_newer_schema_is_refused()
    {
        await SeedAsync("a");
        var service = Service();
        await service.RunDailyAsync(CancellationToken.None);
        var newer = Path.Combine(Backups, "oak-proyektor.daily.2026-01-01.db");
        File.Copy(service.LastResult.Path!, newer);
        await using (var c = await new SqliteConnectionFactory(new DatabaseOptions(newer)).OpenAsync(CancellationToken.None))
            await c.ExecuteAsync($"PRAGMA user_version = {DatabaseInitializer.LatestSchemaVersion + 5};");
        SqliteConnection.ClearAllPools();

        var ex = await Assert.ThrowsAsync<BackupException>(() => new DatabaseRestorer(_db.Options, time: _clock).RestoreAsync(newer, CancellationToken.None));
        Assert.Contains("más nueva", ex.Message);
    }

    [Fact]
    public async Task Restoring_the_current_database_onto_itself_or_a_missing_file_is_refused()
    {
        await SeedAsync("a");
        var restorer = new DatabaseRestorer(_db.Options, time: _clock);
        await Assert.ThrowsAsync<BackupException>(() => restorer.RestoreAsync(_db.Options.DatabasePath, CancellationToken.None));
        await Assert.ThrowsAsync<BackupException>(() => restorer.RestoreAsync(Path.Combine(Backups, "no-existe.db"), CancellationToken.None));
    }

    [Fact]
    public async Task Two_restores_in_the_same_millisecond_never_overwrite_the_saved_database()
    {
        await SeedAsync("a");
        var backup = Service();
        await backup.RunDailyAsync(CancellationToken.None);
        var restorer = new DatabaseRestorer(_db.Options, time: _clock);   // reloj fijo: mismo sello las dos veces
        var first = await restorer.RestoreAsync(backup.LastResult.Path!, CancellationToken.None);
        var second = await restorer.RestoreAsync(backup.LastResult.Path!, CancellationToken.None);
        Assert.NotEqual(first.PreviousSavedAs, second.PreviousSavedAs);
        Assert.True(File.Exists(first.PreviousSavedAs));
        Assert.True(File.Exists(second.PreviousSavedAs));
    }

    // ---- consola ----

    [Theory]
    [InlineData("--list-backups")]
    [InlineData("--restore-backup", "x")]
    public void The_console_commands_are_recognised(params string[] args)
    {
        Assert.True(CliArguments.IsCliInvocation(args));
        Assert.NotNull(CliArguments.Parse(args).Command);
    }

    [Fact]
    public void A_restore_needs_a_name_and_cannot_be_combined_with_other_commands()
    {
        Assert.NotNull(CliArguments.Parse(["--restore-backup"]).Error);
        Assert.NotNull(CliArguments.Parse(["--restore-backup", "x", "--list-backups"]).Error);
        Assert.Contains("--restore-backup", CliArguments.HelpText());
        Assert.Contains("--list-backups", CliArguments.HelpText());
    }

    private (CliRunner Runner, StringWriter Out, StringWriter Err) Console_(string dataDir)
    {
        var o = new StringWriter(); var e = new StringWriter();
        return (new CliRunner(new OakProyektor.App.ApplicationPaths(dataDir, Path.Combine(dataDir, "logs"), dataDir), NullLoggerFactory.Instance, o, e), o, e);
    }

    [Fact]
    public async Task The_console_lists_backups_by_kind_and_restores_one_by_name()
    {
        await SeedAsync("texto sintetico X");
        var backup = Service();
        await backup.RunDailyAsync(CancellationToken.None);
        Touch("oak-proyektor.v1.20260101T000000000Z.db");
        var dataDir = Path.GetDirectoryName(_db.Options.DatabasePath)!;
        SqliteConnection.ClearAllPools();
        File.WriteAllText(_db.Options.DatabasePath, "danada");   // la actual está rota

        var (runner, output, error) = Console_(dataDir);
        Assert.Equal(CliRunner.Ok, await runner.RunAsync(CliArguments.Parse(["--list-backups"]).Command!, CancellationToken.None));
        var listing = output.ToString();
        Assert.Contains("diaria", listing);
        Assert.Contains("antes de una migración", listing);
        Assert.Contains("oak-proyektor.daily.2026-11-24.db", listing);

        var (runner2, output2, error2) = Console_(dataDir);
        var code = await runner2.RunAsync(CliArguments.Parse(["--restore-backup", "daily.2026-11-24"]).Command!, CancellationToken.None);
        Assert.True(code == CliRunner.Ok, error2.ToString());
        Assert.Contains("La base anterior se guardó en", output2.ToString());
        Assert.Equal(1, await CountVersesAsync(_db.Options.DatabasePath));
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task The_console_reports_an_unknown_backup_and_changes_nothing()
    {
        await SeedAsync("a");
        SqliteConnection.ClearAllPools();
        var before = File.ReadAllBytes(_db.Options.DatabasePath);
        var (runner, _, error) = Console_(Path.GetDirectoryName(_db.Options.DatabasePath)!);
        var code = await runner.RunAsync(CliArguments.Parse(["--restore-backup", "no-existe"]).Command!, CancellationToken.None);
        Assert.Equal(CliRunner.Failed, code);
        Assert.Contains("No hay ninguna copia", error.ToString());
        Assert.Equal(before, File.ReadAllBytes(_db.Options.DatabasePath));
    }

    [Fact]
    public void Restoring_is_refused_while_the_application_holds_the_single_instance_lock()
    {
        // La orden de restaurar toma la misma instancia única que la aplicación gráfica (CliHost): si hay otra con la misma carpeta de datos, se niega.
        var name = OakProyektor.App.SingleInstanceGuard.NameFor(Path.GetDirectoryName(_db.Options.DatabasePath)!);
        using var app = OakProyektor.App.SingleInstanceGuard.TryAcquire(name);
        Assert.NotNull(app);
        Assert.Null(OakProyektor.App.SingleInstanceGuard.TryAcquire(name));   // lo que vería CliHost: no puede continuar
    }
}
