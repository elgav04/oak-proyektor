using Dapper;
using Microsoft.Data.Sqlite;
using OakProyektor.App;
using OakProyektor.App.Cli;
using OakProyektor.Data;
using OakProyektor.Tests.Data;

namespace OakProyektor.Tests.Scripture;

/// <summary>
/// Pruebas de <c>CliHost.Execute</c> (la orden completa: instancia única, marca de consola, registro y ejecución) sobre carpetas de datos temporales.
/// Antes solo se probaba <c>CliRunner</c>, así que la regla «se rechaza con la aplicación abierta» no tenía prueba automática (hallazgo 12).
/// </summary>
public sealed class CliHostTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "oak-proyektor-tests", "clihost-" + Guid.NewGuid().ToString("N"));
    private string DataDir => Path.Combine(_root, "datos");
    private string DbPath => Path.Combine(DataDir, "oak-proyektor.db");
    private ApplicationPaths Paths => new(DataDir, Path.Combine(DataDir, "logs"), Path.Combine(_root, "web"), "prueba");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* limpieza best-effort */ }
    }

    /// <summary>Lee un archivo aunque SQLite lo tenga abierto (el -wal); vacío si no existe.</summary>
    private static byte[] ReadShared(string path)
    {
        if (!File.Exists(path)) return [];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static (StringWriter Out, StringWriter Err) Writers() => (new StringWriter(), new StringWriter());

    private int Run(string[] args, out string output, out string error, Func<string, SingleInstanceGuard?>? acquire = null, CancellationToken ct = default)
    {
        var (o, e) = Writers();
        var code = CliHost.Execute(args, Paths, o, e, acquire ?? SingleInstanceGuard.TryAcquire, ct);
        output = o.ToString(); error = e.ToString();
        return code;
    }

    /// <summary>Una carpeta de datos con la base ya creada y migrada (como la deja la aplicación).</summary>
    private async Task InitializeDatabaseAsync()
    {
        Directory.CreateDirectory(DataDir);
        var options = DatabaseOptions.ForDataDirectory(DataDir);
        var initializer = new DatabaseInitializer(options, new SqliteConnectionFactory(options), Microsoft.Extensions.Logging.Abstractions.NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync(CancellationToken.None);
        SqliteConnection.ClearAllPools();
    }

    private string SyntheticFile()
    {
        var dir = Path.Combine(_root, "archivos");
        return SyntheticBible.WriteYouVersion(dir, SyntheticBible.Small(), fileName: "sintetica.json");
    }

    private string[] ImportArgs(string path) => ["--import-bible", path, "--abbr", "SIN", "--name", "Sintetica", "--lang", "es"];

    private async Task<long> ScalarAsync(string sql)
    {
        await using var c = await SqliteConnectionFactory.OpenFileReadOnlyAsync(DbPath, CancellationToken.None);
        return await c.ExecuteScalarAsync<long>(sql);
    }

    // ───────────────────────────── instancia única ─────────────────────────────

    [Fact]
    public async Task An_import_is_refused_while_the_application_holds_the_single_instance_and_changes_nothing()
    {
        await InitializeDatabaseAsync();
        var file = SyntheticFile();
        using var app = SingleInstanceGuard.TryAcquire(SingleInstanceGuard.NameFor(DataDir));   // «la aplicación abierta»
        Assert.NotNull(app);
        var before = File.ReadAllBytes(DbPath);

        var code = Run(ImportArgs(file), out var output, out var error);

        Assert.Equal(CliRunner.Failed, code);
        Assert.Contains("está abierto", error);
        Assert.Contains("No se cambió nada", error);
        Assert.DoesNotContain("Importando", output);                       // ni siquiera empezó
        Assert.Equal(before, File.ReadAllBytes(DbPath));
        Assert.Equal(0, await ScalarAsync("SELECT COUNT(*) FROM Bibles"));
    }

    [Fact]
    public async Task A_restore_is_refused_too_while_the_application_is_open()
    {
        await InitializeDatabaseAsync();
        using var app = SingleInstanceGuard.TryAcquire(SingleInstanceGuard.NameFor(DataDir));
        var code = Run(["--restore-backup", "lo-que-sea"], out _, out var error);
        Assert.Equal(CliRunner.Failed, code);
        Assert.Contains("está abierto", error);
        Assert.Empty(Directory.Exists(Path.Combine(DataDir, "backups")) ? Directory.GetFiles(Path.Combine(DataDir, "backups")) : []);
    }

    [Fact]
    public async Task Two_console_imports_cannot_run_at_the_same_time()
    {
        await InitializeDatabaseAsync();
        var file = SyntheticFile();
        var names = new List<string>();
        SingleInstanceGuard? first = null;
        var code = Run(ImportArgs(file), out _, out var error, name =>
        {
            names.Add(name);
            var guard = SingleInstanceGuard.TryAcquire(name);
            if (first is null && guard is not null) first = guard;   // el primero (el candado principal) queda tomado: simula otra instancia
            return guard;
        });
        Assert.Equal(CliRunner.Ok, code);
        first?.Dispose();

        using var other = SingleInstanceGuard.TryAcquire(SingleInstanceGuard.NameFor(DataDir));
        Assert.NotNull(other);
        Assert.Equal(CliRunner.Failed, Run(ImportArgs(file), out _, out error));
        Assert.Contains("está abierto", error);
    }

    [Fact]
    public async Task A_write_command_takes_the_main_lock_and_the_console_marker_and_releases_both_when_it_ends()
    {
        await InitializeDatabaseAsync();
        var file = SyntheticFile();
        var requested = new List<string>();
        var markerSeenWhileRunning = false;
        var code = Run(ImportArgs(file), out _, out var error, name =>
        {
            requested.Add(name);
            var guard = SingleInstanceGuard.TryAcquire(name);
            if (name == SingleInstanceGuard.ConsoleMarkerNameFor(DataDir)) markerSeenWhileRunning = SingleInstanceGuard.Exists(name);
            return guard;
        });

        Assert.Equal(CliRunner.Ok, code);
        Assert.Equal([SingleInstanceGuard.NameFor(DataDir), SingleInstanceGuard.ConsoleMarkerNameFor(DataDir)], requested);
        Assert.True(markerSeenWhileRunning);
        Assert.False(SingleInstanceGuard.Exists(SingleInstanceGuard.ConsoleMarkerNameFor(DataDir)));   // liberada al terminar
        Assert.NotNull(SingleInstanceGuard.TryAcquire(SingleInstanceGuard.NameFor(DataDir)));            // y el candado principal también
        Assert.Empty(error);
    }

    [Fact]
    public void The_marker_is_detectable_by_name_only_while_it_is_held()
    {
        var name = SingleInstanceGuard.ConsoleMarkerNameFor(DataDir);
        Assert.False(SingleInstanceGuard.Exists(name));
        using (SingleInstanceGuard.TryAcquire(name)) Assert.True(SingleInstanceGuard.Exists(name));
        Assert.False(SingleInstanceGuard.Exists(name));
        Assert.NotEqual(SingleInstanceGuard.NameFor(DataDir), name);   // no se confunde con el candado de la aplicación
    }

    [Fact]
    public void Opening_the_application_during_a_console_operation_explains_it_and_does_not_send_the_operator_to_the_task_manager()
    {
        var message = Program.ConsoleOperationRunningMessage;
        Assert.DoesNotContain("Administrador de tareas", message);
        Assert.Contains("orden de consola", message);
        Assert.Contains("importación", message);
        Assert.Contains("Espera", message);
        Assert.Contains("Ctrl+C", message);
        Assert.Contains("no cambia la base de datos", message);
    }

    // ───────────────────────────── órdenes de solo lectura ─────────────────────────────

    [Theory]
    [InlineData("--list-bibles")]
    [InlineData("--show-verse", "Jn 3:16")]
    [InlineData("--verify-bible", "--abbr", "SIN", "--against", "x.json")]
    public void Read_only_commands_do_not_create_a_database_nor_the_data_folder_when_it_is_missing(params string[] args)
    {
        var code = Run(args, out _, out var error);

        Assert.Equal(CliRunner.Failed, code);
        Assert.Contains("No existe la base de datos", error);
        Assert.Contains("OAK_DATA_DIR", error);
        Assert.False(File.Exists(DbPath));
        Assert.False(Directory.Exists(DataDir));   // ni la carpeta, ni su registro
    }

    [Fact]
    public async Task Read_only_commands_do_not_migrate_an_older_database()
    {
        await InitializeDatabaseAsync();
        await using (var c = await SqliteConnectionFactory.OpenFileReadOnlyAsync(DbPath, CancellationToken.None)) { /* solo para comprobar que abre */ }
        var options = DatabaseOptions.ForDataDirectory(DataDir);
        await using (var c = await new SqliteConnectionFactory(options).OpenAsync(CancellationToken.None))
            await c.ExecuteAsync("PRAGMA user_version = 3;");   // como una base de una versión anterior de la aplicación
        SqliteConnection.ClearAllPools();
        var before = File.ReadAllBytes(DbPath);

        foreach (var args in new[] { new[] { "--list-bibles" }, ["--show-verse", "Jn 3:16"], ["--verify-bible", "--abbr", "SIN", "--against", "x.json"] })
        {
            var code = Run(args, out _, out var error);
            Assert.Equal(CliRunner.Failed, code);
            Assert.Contains("versión 3 del esquema", error);
            Assert.Contains("abre la aplicación gráfica", error);
        }

        Assert.Equal(3, await ScalarAsync("PRAGMA user_version"));          // NO se migró
        Assert.Equal(before, File.ReadAllBytes(DbPath));
        Assert.False(Directory.Exists(Path.Combine(DataDir, "backups")));  // ni se hizo la copia previa de una migración
    }

    [Fact]
    public async Task A_database_from_a_newer_version_is_reported_clearly()
    {
        await InitializeDatabaseAsync();
        await using (var c = await new SqliteConnectionFactory(DatabaseOptions.ForDataDirectory(DataDir)).OpenAsync(CancellationToken.None))
            await c.ExecuteAsync("PRAGMA user_version = 99;");
        SqliteConnection.ClearAllPools();
        var code = Run(["--list-bibles"], out _, out var error);
        Assert.Equal(CliRunner.Failed, code);
        Assert.Contains("más nueva", error);
    }

    [Fact]
    public async Task A_corrupt_file_is_reported_without_touching_it()
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(DbPath, new string('x', 6000));
        var code = Run(["--list-bibles"], out _, out var error);
        Assert.Equal(CliRunner.Failed, code);
        Assert.Contains("No se puede leer la base de datos", error);
        Assert.Equal(new string('x', 6000), File.ReadAllText(DbPath));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Read_only_commands_work_on_a_current_database_without_modifying_it_even_with_the_application_open()
    {
        await InitializeDatabaseAsync();
        // Importa algo primero (con la aplicación cerrada) para tener una Biblia que listar.
        Assert.Equal(CliRunner.Ok, Run(ImportArgs(SyntheticFile()), out _, out _));
        SqliteConnection.ClearAllPools();
        var before = File.ReadAllBytes(DbPath);
        var walBefore = ReadShared(DbPath + "-wal");

        using var app = SingleInstanceGuard.TryAcquire(SingleInstanceGuard.NameFor(DataDir));   // la aplicación está abierta: no las molesta
        var code = Run(["--list-bibles"], out var output, out var error);

        Assert.True(code == CliRunner.Ok, error);
        Assert.Contains("SIN", output);
        Assert.Equal(before, File.ReadAllBytes(DbPath));
        Assert.Equal(walBefore, ReadShared(DbPath + "-wal"));
    }

    [Fact]
    public async Task The_read_only_factory_cannot_write()
    {
        await InitializeDatabaseAsync();
        var factory = new ReadOnlySqliteConnectionFactory(DatabaseOptions.ForDataDirectory(DataDir));
        await using var c = await factory.OpenAsync(CancellationToken.None);
        Assert.Equal(0, await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Bibles"));
        await Assert.ThrowsAsync<SqliteException>(() => c.ExecuteAsync("INSERT INTO Bibles (Abbreviation, Name, Language) VALUES ('X', 'X', 'es')"));
    }

    [Fact]
    public async Task The_read_only_factory_never_creates_a_missing_file()
    {
        Directory.CreateDirectory(DataDir);
        var factory = new ReadOnlySqliteConnectionFactory(DatabaseOptions.ForDataDirectory(DataDir));
        await Assert.ThrowsAsync<SqliteException>(() => factory.OpenAsync(CancellationToken.None));
        Assert.False(File.Exists(DbPath));
    }

    // ───────────────────────────── Ctrl+C ─────────────────────────────

    [Fact]
    public async Task A_cancelled_command_says_Cancelado_changes_nothing_and_releases_the_locks()
    {
        await InitializeDatabaseAsync();
        var file = SyntheticFile();
        using var cts = new CancellationTokenSource();
        cts.Cancel();   // lo que hace el manejador de Ctrl+C de CliHost.Run

        var code = Run(ImportArgs(file), out _, out var error, ct: cts.Token);

        Assert.Equal(CliRunner.Failed, code);
        Assert.Contains("Cancelado", error);
        Assert.Contains("No se cambió nada", error);
        Assert.Equal(0, await ScalarAsync("SELECT COUNT(*) FROM Bibles"));
        Assert.False(SingleInstanceGuard.Exists(SingleInstanceGuard.ConsoleMarkerNameFor(DataDir)));
        Assert.NotNull(SingleInstanceGuard.TryAcquire(SingleInstanceGuard.NameFor(DataDir)));
    }

    [Fact]
    public async Task Cancelling_a_restore_is_reported_the_same_way()
    {
        await InitializeDatabaseAsync();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var code = Run(["--restore-backup", Path.Combine(_root, "no-existe.db")], out _, out var error, ct: cts.Token);
        Assert.Equal(CliRunner.Failed, code);
        Assert.True(error.Contains("Cancelado") || error.Contains("No existe"), error);   // sin copia que restaurar el fallo es otro, pero nunca «Error inesperado»
        Assert.DoesNotContain("inesperado", error);
    }
}
